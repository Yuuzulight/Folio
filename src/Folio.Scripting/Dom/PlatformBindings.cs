using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Folio.Html;

namespace Folio.Scripting.Dom;

/// <summary>The seed a test-mode engine was made with, for the random sources of the platform APIs.</summary>
internal static class ScriptEngineSeeds
{
    private static readonly ConditionalWeakTable<IScriptEngine, StrongBox<int>> Seeds = new();

    internal static void Register(IScriptEngine engine, int seed) => Seeds.AddOrUpdate(engine, new StrongBox<int>(seed));

    internal static int? Of(IScriptEngine engine) => Seeds.TryGetValue(engine, out var seed) ? seed.Value : null;
}

/// <summary>
/// The platform objects a document and a worker share: <c>console</c>, <c>atob</c>/<c>btoa</c>, <c>TextEncoder</c>/<c>TextDecoder</c> and
/// <c>crypto</c>. The Console Standard's formatting is script; the host keeps what needs bytes, randomness and the diagnostics sink.
/// </summary>
internal static class PlatformBindings
{
    /// <summary>A console message is cut to this many UTF-16 code units.</summary>
    internal const int MaxMessageLength = 16 * 1024;

    /// <param name="console">Receives each console message: level (info, warn or error), text, and the line and column of the call (0 when unknown).</param>
    internal static void Install(IScriptEngine engine, Action<string, string, int, int>? console = null)
    {
        DomBindings.InstallDomException(engine);
        var host = new HostState(engine, console, ScriptEngineSeeds.Of(engine));
        var template = new HostObjectTemplate("PlatformHost")
            .DefineMethod("encode", (t, _, a) => ((HostState)t).Encode(Text(a, 0)))
            .DefineMethod("encodingFor", (_, _, a) => ScriptValue.FromString(EncodingName(Text(a, 0))))
            .DefineMethod("decode", (t, _, a) => ((HostState)t).Decode(ReadBytes(((HostState)t).Engine, a[0]), a.Length > 1 && a[1].AsBoolean(), Text(a, 2)))
            .DefineMethod("random", (t, _, a) => ((HostState)t).Random(a.Length == 0 ? 0 : checked((int)a[0].AsNumber())))
            .DefineMethod("randomUUID", (t, _, _) => ((HostState)t).RandomUuid())
            .DefineMethod("base64Encode", (t, _, a) => ((HostState)t).Base64Encode(Text(a, 0)))
            .DefineMethod("base64Decode", (t, _, a) => ((HostState)t).Base64Decode(Text(a, 0)))
            .DefineMethod("write", (t, _, a) =>
            {
                ((HostState)t).Write(Text(a, 0), Text(a, 1), a.Length > 2 ? (int)a[2].AsNumber() : 0, a.Length > 3 ? (int)a[3].AsNumber() : 0);
                return ScriptValue.Undefined;
            });
        engine.SetGlobalProperty("__folio_platform", engine.CreateHostObject(template, host));
        engine.EvaluateHostScript(Source, "folio:platform");
        engine.Evaluate("delete globalThis.__folio_platform;", "folio:platform-cleanup");
    }

    private static string Text(ReadOnlySpan<ScriptValue> args, int index) => index < args.Length ? args[index].AsString() ?? "" : "";

    /// <summary>The canonical name of the decoder a label stands for (https://encoding.spec.whatwg.org/#names-and-labels), for the encodings Folio has.</summary>
    internal static string? EncodingName(string label) => EncodingSniffer.FromLabel(label) switch
    {
        EncodingSniffer.Kind.Utf8 => "utf-8",
        EncodingSniffer.Kind.Utf16LE => "utf-16le",
        EncodingSniffer.Kind.Utf16BE => "utf-16be",
        EncodingSniffer.Kind.Windows1252 => "windows-1252",
        _ => null,
    };

    private static byte[] ReadBytes(IScriptEngine engine, ScriptValue value)
    {
        if (!engine.TryGetArrayItems(value, out var items) || items.Count > 16 * 1024 * 1024)
            throw WebIdlException.TypeError("The input is not a bounded byte sequence.");
        var bytes = new byte[items.Count];
        for (var i = 0; i < bytes.Length; i++)
        {
            var n = items[i].AsNumber();
            if (!double.IsFinite(n) || n < 0 || n > 255)
                throw WebIdlException.TypeError("The input contains an invalid byte.");
            bytes[i] = (byte)n;
        }
        return bytes;
    }

    private sealed class HostState(IScriptEngine engine, Action<string, string, int, int>? console, int? seed)
    {
        private readonly Random? _seeded = seed is { } value ? new Random(value) : null;

        internal IScriptEngine Engine { get; } = engine;

        internal ScriptValue Encode(string value)
        {
            if (Encoding.UTF8.GetByteCount(value) > 16 * 1024 * 1024)
                throw WebIdlException.DomException("QuotaExceededError", "The encoded input is too large.");
            return Engine.CreateArray(Encoding.UTF8.GetBytes(value).Select(b => ScriptValue.FromNumber(b)).ToArray());
        }

        internal ScriptValue Decode(byte[] bytes, bool fatal, string name)
        {
            try
            {
                return ScriptValue.FromString(name switch
                {
                    "utf-16le" => new UnicodeEncoding(false, false, fatal).GetString(bytes),
                    "utf-16be" => new UnicodeEncoding(true, false, fatal).GetString(bytes),
                    "windows-1252" => EncodingSniffer.Decode(bytes, EncodingSniffer.Kind.Windows1252),
                    _ => new UTF8Encoding(false, fatal).GetString(bytes),
                });
            }
            catch (DecoderFallbackException)
            {
                throw WebIdlException.TypeError("The encoded data was not valid for the encoding.");
            }
        }

        private byte[] RandomBytes(int length)
        {
            var bytes = new byte[length];
            if (_seeded is not null)
                _seeded.NextBytes(bytes);
            else
                RandomNumberGenerator.Fill(bytes);
            return bytes;
        }

        internal ScriptValue Random(int length)
        {
            if (length < 0 || length > 65536)
                throw WebIdlException.DomException("QuotaExceededError", "The requested random buffer is too large.");
            return Engine.CreateArray(RandomBytes(length).Select(b => ScriptValue.FromNumber(b)).ToArray());
        }

        internal ScriptValue RandomUuid()
        {
            var bytes = RandomBytes(16);
            bytes[6] = (byte)(bytes[6] & 0x0F | 0x40);
            bytes[8] = (byte)(bytes[8] & 0x3F | 0x80);
            return ScriptValue.FromString(new Guid(bytes, bigEndian: true).ToString());
        }

        internal ScriptValue Base64Encode(string value) => ScriptValue.FromString(Convert.ToBase64String(Encoding.Latin1.GetBytes(value)));

        // Forgiving-base64 decode (https://infra.spec.whatwg.org/#forgiving-base64-decode).
        internal ScriptValue Base64Decode(string value)
        {
            value = string.Concat(value.Where(c => c is not (' ' or '\n' or '\r' or '\t' or '\f')));
            if (value.Length % 4 == 0)
            {
                if (value.EndsWith("==", StringComparison.Ordinal))
                    value = value[..^2];
                else if (value.EndsWith('='))
                    value = value[..^1];
            }
            if (value.Length % 4 == 1 || value.Any(c => !(c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '+' or '/')))
                throw WebIdlException.DomException("InvalidCharacterError", "The string is not valid base64.");
            value = value.PadRight((value.Length + 3) / 4 * 4, '=');
            try
            {
                return ScriptValue.FromString(Encoding.Latin1.GetString(Convert.FromBase64String(value)));
            }
            catch (FormatException)
            {
                throw WebIdlException.DomException("InvalidCharacterError", "The string is not valid base64.");
            }
        }

        internal void Write(string level, string text, int line, int column)
        {
            if (text.Length > MaxMessageLength)
                text = text[..MaxMessageLength] + "... (message cut at " + MaxMessageLength + " characters)";
            try
            {
                console?.Invoke(level, text, line, column);
            }
            catch
            {
                // A host that cannot take the message loses it; the script goes on.
            }
        }
    }

    private const string Source = """
        (function (host) {
          'use strict';
          const U8 = Uint8Array, AB = ArrayBuffer, isView = AB.isView;
          const Afrom = Array.from, S = String;
          const apply = Reflect.apply, getDescriptor = Object.getOwnPropertyDescriptor;
          const typedProto = Object.getPrototypeOf(U8.prototype), typedTag = getDescriptor(typedProto, Symbol.toStringTag).get;
          const typedBuffer = getDescriptor(typedProto, 'buffer').get, typedOffset = getDescriptor(typedProto, 'byteOffset').get, typedLength = getDescriptor(typedProto, 'byteLength').get;
          const typedCount = getDescriptor(typedProto, 'length').get;
          const dataBuffer = getDescriptor(DataView.prototype, 'buffer').get, dataOffset = getDescriptor(DataView.prototype, 'byteOffset').get, dataLength = getDescriptor(DataView.prototype, 'byteLength').get;
          const arrayLength = getDescriptor(AB.prototype, 'byteLength').get;
          function typedBrand(v) { try { return apply(typedTag, v, []); } catch (_) { return undefined; } }
          function bytesOf(v) {
            if (isView(v)) {
              const typed = typedBrand(v) !== undefined;
              return new U8(apply(typed ? typedBuffer : dataBuffer, v, []), apply(typed ? typedOffset : dataOffset, v, []), apply(typed ? typedLength : dataLength, v, []));
            }
            try { apply(arrayLength, v, []); return new U8(v); } catch (_) { throw new TypeError('Expected a BufferSource.'); }
          }
          function needNew(target, name) {
            if (!target) throw new TypeError("Failed to construct '" + name + "': Please use the 'new' operator, this DOM object constructor cannot be called as a function.");
          }
          function method(proto, name, length, fn) {
            Object.defineProperty(proto, name, { value: fn, writable: true, enumerable: true, configurable: true });
            Object.defineProperty(fn, 'length', { value: length, configurable: true });
          }

          // ---- TextEncoder ----
          function TextEncoder() { needNew(new.target, 'TextEncoder'); }
          method(TextEncoder.prototype, 'encode', 0, function encode(v) { return new U8(host.encode(v === undefined ? '' : S(v))); });
          method(TextEncoder.prototype, 'encodeInto', 2, function encodeInto(v, dest) {
            if (typedBrand(dest) !== 'Uint8Array') throw new TypeError("Failed to execute 'encodeInto' on 'TextEncoder': parameter 2 is not of type 'Uint8Array'.");
            const text = S(v), capacity = apply(typedCount, dest, []);
            let read = 0, written = 0;
            while (read < text.length) {
              const cp = text.codePointAt(read), chars = cp > 65535 ? 2 : 1, b = host.encode(text.slice(read, read + chars));
              if (written + b.length > capacity) break;
              for (let i = 0; i < b.length; i++) dest[written + i] = b[i];
              read += chars; written += b.length;
            }
            return { read: read, written: written };
          });
          Object.defineProperty(TextEncoder.prototype, 'encoding', { get: function () { return 'utf-8'; }, enumerable: true, configurable: true });
          Object.defineProperty(TextEncoder.prototype, Symbol.toStringTag, { value: 'TextEncoder', configurable: true });

          // ---- TextDecoder ----
          const decoderStates = new WeakMap();
          function TextDecoder(label, options) {
            needNew(new.target, 'TextDecoder');
            const name = host.encodingFor(label === undefined ? 'utf-8' : S(label));
            if (name === null) throw new RangeError("Failed to construct 'TextDecoder': The encoding label provided ('" + S(label) + "') is invalid.");
            decoderStates.set(this, { name: name, fatal: !!(options && options.fatal), ignoreBOM: !!(options && options.ignoreBOM), pending: [], bomSeen: false });
          }
          function decoderState(self) { const state = decoderStates.get(self); if (!state) throw new TypeError('Illegal invocation'); return state; }
          Object.defineProperty(TextDecoder.prototype, 'encoding', { get: function () { return decoderState(this).name; }, enumerable: true, configurable: true });
          Object.defineProperty(TextDecoder.prototype, 'fatal', { get: function () { return decoderState(this).fatal; }, enumerable: true, configurable: true });
          Object.defineProperty(TextDecoder.prototype, 'ignoreBOM', { get: function () { return decoderState(this).ignoreBOM; }, enumerable: true, configurable: true });
          Object.defineProperty(TextDecoder.prototype, Symbol.toStringTag, { value: 'TextDecoder', configurable: true });
          // Where a streaming chunk ends: before a character it does not hold all of yet.
          function completeEnd(state, all) {
            let end = all.length;
            if (state.name === 'utf-8') {
              for (let start = Math.max(0, end - 3); start < end; start++) {
                const lead = all[start], need = lead >= 0xC2 && lead <= 0xDF ? 2 : lead >= 0xE0 && lead <= 0xEF ? 3 : lead >= 0xF0 && lead <= 0xF4 ? 4 : 0;
                if (!need || end - start >= need) continue;
                let valid = true;
                for (let j = start + 1; j < end; j++) {
                  const low = j === start + 1 && lead === 0xE0 ? 0xA0 : j === start + 1 && lead === 0xF0 ? 0x90 : 0x80;
                  const high = j === start + 1 && lead === 0xED ? 0x9F : j === start + 1 && lead === 0xF4 ? 0x8F : 0xBF;
                  if (all[j] < low || all[j] > high) { valid = false; break; }
                }
                if (valid) { end = start; break; }
              }
            } else if (state.name !== 'windows-1252') {
              if (end % 2) end -= 1;
              if (end >= 2) {
                const unit = state.name === 'utf-16le' ? all[end - 2] | (all[end - 1] << 8) : (all[end - 2] << 8) | all[end - 1];
                if (unit >= 0xD800 && unit <= 0xDBFF) end -= 2;
              }
            }
            return end;
          }
          method(TextDecoder.prototype, 'decode', 0, function decode(v, options) {
            const state = decoderState(this);
            const all = state.pending.concat(v === undefined ? [] : Afrom(bytesOf(v))), stream = !!(options && options.stream);
            const end = stream ? completeEnd(state, all) : all.length;
            let output;
            try { output = host.decode(all.slice(0, end), state.fatal, state.name); }
            catch (error) { state.pending = []; state.bomSeen = false; throw error; }
            state.pending = stream ? all.slice(end) : [];
            if (state.name !== 'windows-1252') {
              if (!state.bomSeen && output.length) {
                state.bomSeen = true;
                if (!state.ignoreBOM && output.charCodeAt(0) === 0xFEFF) output = output.slice(1);
              }
              if (!stream) state.bomSeen = false;
            }
            return output;
          });

          // ---- atob and btoa ----
          function atob(data) {
            if (arguments.length === 0) throw new TypeError("Failed to execute 'atob' on 'Window': 1 argument required, but only 0 present.");
            return host.base64Decode(S(data));
          }
          function btoa(data) {
            if (arguments.length === 0) throw new TypeError("Failed to execute 'btoa' on 'Window': 1 argument required, but only 0 present.");
            data = S(data);
            for (let i = 0; i < data.length; i++) if (data.charCodeAt(i) > 255) throw new DOMException("The string to be encoded contains characters outside of the Latin1 range.", 'InvalidCharacterError');
            return host.base64Encode(data);
          }

          // ---- crypto ----
          const cryptoObject = {};
          method(cryptoObject, 'getRandomValues', 1, function getRandomValues(v) {
            const brand = typedBrand(v);
            if (brand !== 'Int8Array' && brand !== 'Uint8Array' && brand !== 'Uint8ClampedArray' && brand !== 'Int16Array' && brand !== 'Uint16Array' && brand !== 'Int32Array' && brand !== 'Uint32Array' && brand !== 'BigInt64Array' && brand !== 'BigUint64Array')
              throw new DOMException("The data argument must be an integer-type TypedArray.", 'TypeMismatchError');
            const out = bytesOf(v), length = apply(typedLength, out, []);
            if (length > 65536) throw new DOMException("The ArrayBufferView's byte length (" + length + ") exceeds the number of bytes of entropy available via this API (65536).", 'QuotaExceededError');
            const bytes = host.random(length);
            for (let i = 0; i < length; i++) out[i] = bytes[i];
            return v;
          });
          method(cryptoObject, 'randomUUID', 0, function randomUUID() { return host.randomUUID(); });
          Object.defineProperty(cryptoObject, Symbol.toStringTag, { value: 'Crypto', configurable: true });

          // ---- console (https://console.spec.whatwg.org/) ----
          const frameAt = /\(?([^()\s]+):(\d+):(\d+)\)?\s*$/;
          function where() {
            const stack = new Error().stack;
            if (typeof stack !== 'string') return [0, 0];
            const lines = stack.split('\n');
            for (let i = 0; i < lines.length; i++) {
              const m = frameAt.exec(lines[i]);
              if (m && m[1] !== 'folio:platform') return [Number(m[2]), Number(m[3])];
            }
            return [0, 0];
          }
          function quote(text) {
            const q = text.indexOf("'") < 0 ? "'" : text.indexOf('"') < 0 ? '"' : text.indexOf('`') < 0 ? '`' : "'";
            return q + text.replace(/\\/g, '\\\\').replace(/\n/g, '\\n').replace(/\r/g, '\\r').replace(/\t/g, '\\t').split(q).join('\\' + q) + q;
          }
          function typeName(value) {
            const proto = Object.getPrototypeOf(value);
            if (proto === null) return null;
            const ctor = proto.constructor;
            return typeof ctor === 'function' && ctor.name ? ctor.name : 'Object';
          }
          function inspect(value, level, seen) {
            if (value === null) return 'null';
            const type = typeof value;
            if (type === 'string') return level === 0 ? value : quote(value);
            if (type === 'number') return Object.is(value, -0) ? '-0' : S(value);
            if (type === 'bigint') return S(value) + 'n';
            if (type === 'symbol') return value.toString();
            if (type !== 'object' && type !== 'function') return S(value);
            if (type === 'function') {
              // A class's prototype property is not writable; a function's is.
              const prototype = getDescriptor(value, 'prototype');
              return prototype && prototype.writable === false ? '[class ' + (value.name || '(anonymous)') + ']' : value.name ? '[Function: ' + value.name + ']' : '[Function (anonymous)]';
            }
            if (seen.indexOf(value) >= 0) return '[Circular]';
            if (level > 2) return Array.isArray(value) ? '[Array]' : '[Object]';
            seen.push(value);
            try {
              if (value instanceof Error) return S(value.stack || value);
              if (value instanceof Date) return isNaN(value) ? 'Invalid Date' : value.toISOString();
              if (value instanceof RegExp) return S(value);
              if (value instanceof Number || value instanceof String || value instanceof Boolean) return '[' + typeName(value) + ': ' + inspect(value.valueOf(), 1, seen) + ']';
              const parts = [];
              let open = '{', close = '}', prefix = '';
              if (Array.isArray(value)) {
                open = '['; close = ']';
                let holes = 0;
                for (let i = 0; i < value.length && parts.length < 100; i++) {
                  if (!(i in value)) { holes++; continue; }
                  if (holes) { parts.push('<' + holes + ' empty item' + (holes > 1 ? 's' : '') + '>'); holes = 0; }
                  parts.push(inspect(value[i], level + 1, seen));
                }
                if (holes) parts.push('<' + holes + ' empty item' + (holes > 1 ? 's' : '') + '>');
                if (value.length > 100) parts.push('... ' + (value.length - 100) + ' more item' + (value.length - 100 > 1 ? 's' : ''));
                if (typeName(value) !== 'Array') prefix = typeName(value) + '(' + value.length + ') ';
              } else if (value instanceof Map) {
                prefix = 'Map(' + value.size + ') ';
                value.forEach(function (v, k) { parts.push(inspect(k, level + 1, seen) + ' => ' + inspect(v, level + 1, seen)); });
              } else if (value instanceof Set) {
                prefix = 'Set(' + value.size + ') ';
                value.forEach(function (v) { parts.push(inspect(v, level + 1, seen)); });
              } else if (isView(value) && typedBrand(value) !== undefined) {
                open = '['; close = ']';
                const length = apply(typedCount, value, []);
                for (let i = 0; i < length && i < 100; i++) parts.push(inspect(value[i], level + 1, seen));
                if (length > 100) parts.push('... ' + (length - 100) + ' more items');
                prefix = typeName(value) + '(' + length + ') ';
              } else if (value instanceof AB) {
                prefix = 'ArrayBuffer ';
                parts.push('byteLength: ' + apply(arrayLength, value, []));
              } else {
                const name = typeName(value);
                prefix = name === null ? '[Object: null prototype] ' : name === 'Object' ? '' : name + ' ';
              }
              if (!Array.isArray(value) && !(isView(value) && typedBrand(value) !== undefined) && !(value instanceof AB)) {
                for (const key of Object.keys(value)) {
                  const descriptor = getDescriptor(value, key);
                  const shown = /^[A-Za-z_$][\w$]*$/.test(key) ? key : quote(key);
                  parts.push(shown + ': ' + (descriptor && (descriptor.get || descriptor.set) ? (descriptor.get && descriptor.set ? '[Getter/Setter]' : descriptor.get ? '[Getter]' : '[Setter]') : inspect(value[key], level + 1, seen)));
                }
              }
              if (parts.length === 0) return prefix + open + close;
              const flat = prefix + open + ' ' + parts.join(', ') + ' ' + close;
              if (flat.length <= 72 && flat.indexOf('\n') < 0) return flat;
              return prefix + open + '\n' + parts.map(function (p) { return '  ' + p.split('\n').join('\n  '); }).join(',\n') + '\n' + close;
            } finally { seen.pop(); }
          }
          function formatNumber(n) { return Object.is(n, -0) ? '-0' : S(n); }
          function hasOwnToString(value) {
            let proto = value;
            while (proto !== null && proto !== Object.prototype && proto !== Array.prototype && proto !== Error.prototype) {
              if (Object.prototype.hasOwnProperty.call(proto, 'toString')) return true;
              proto = Object.getPrototypeOf(proto);
            }
            return false;
          }
          function format(args) {
            if (args.length === 0) return '';
            const first = args[0];
            let out, index = 1;
            if (typeof first === 'string') {
              if (args.length === 1) return first;
              let text = '';
              for (let i = 0; i < first.length; i++) {
                const c = first[i];
                if (c === '%' && i + 1 < first.length) {
                  const n = first[i + 1];
                  if (n === '%') { text += '%'; i++; continue; }
                  if (index < args.length && 'sdifoOc'.indexOf(n) >= 0) {
                    const arg = args[index++];
                    if (n === 's') text += typeof arg === 'object' && arg !== null ? (hasOwnToString(arg) ? S(arg) : inspect(arg, 1, [])) : typeof arg === 'symbol' ? arg.toString() : typeof arg === 'bigint' ? S(arg) + 'n' : S(arg);
                    else if (n === 'd') text += typeof arg === 'symbol' ? 'NaN' : typeof arg === 'bigint' ? S(arg) + 'n' : formatNumber(typeof arg === 'object' && arg !== null ? NaN : Number(arg));
                    else if (n === 'i') text += typeof arg === 'symbol' ? 'NaN' : typeof arg === 'bigint' ? S(arg) + 'n' : formatNumber(typeof arg === 'object' && arg !== null ? NaN : parseInt(arg, 10));
                    else if (n === 'f') text += typeof arg === 'symbol' ? 'NaN' : formatNumber(typeof arg === 'object' && arg !== null ? NaN : parseFloat(arg));
                    else if (n === 'o' || n === 'O') text += inspect(arg, 1, []);
                    i++;
                    continue;
                  }
                }
                text += c;
              }
              out = text;
            } else out = inspect(first, 0, []);
            for (; index < args.length; index++) out += ' ' + inspect(args[index], 0, []);
            return out;
          }
          let groupDepth = 0;
          function emit(level, text) {
            if (groupDepth > 0) {
              const pad = '  '.repeat(groupDepth);
              text = text.split('\n').map(function (line) { return pad + line; }).join('\n');
            }
            const at = where();
            host.write(level, text, at[0], at[1]);
          }
          const counts = new Map(), timers = new Map();
          function label(l) { return l === undefined ? 'default' : S(l); }
          function elapsed(since) { return (Date.now() - since) + 'ms'; }
          function stackText() {
            const stack = new Error().stack;
            if (typeof stack !== 'string') return '';
            return '\n' + stack.split('\n').filter(function (line) { return line.indexOf('folio:platform') < 0 && line.trim() !== '' && line.indexOf('Error') !== 0; }).join('\n');
          }
          function cell(text, width) {
            const left = Math.floor((width - text.length) / 2);
            return ' '.repeat(left) + text + ' '.repeat(width - text.length - left);
          }
          function renderTable(data, columns) {
            if (typeof data !== 'object' || data === null) return format([data]);
            const rows = [];
            if (data instanceof Map) { let i = 0; data.forEach(function (v, k) { rows.push([S(i++), 'Key', k, v]); }); }
            else if (data instanceof Set) { let i = 0; data.forEach(function (v) { rows.push([S(i++), undefined, undefined, v]); }); }
            else Object.keys(data).forEach(function (k) { rows.push([k, undefined, undefined, data[k]]); });
            const isMap = data instanceof Map;
            const keys = [];
            let hasValues = false;
            const body = rows.map(function (row) {
              const value = isMap ? row[3] : row[3], cells = {};
              if (typeof value === 'object' && value !== null && typeof value !== 'function') {
                Object.keys(value).forEach(function (k) { if (keys.indexOf(k) < 0) keys.push(k); cells[k] = inspect(value[k], 1, []); });
                return { index: row[0], cells: cells, value: undefined, mapKey: isMap ? inspect(row[2], 1, []) : undefined };
              }
              hasValues = true;
              return { index: row[0], cells: cells, value: inspect(value, 1, []), mapKey: isMap ? inspect(row[2], 1, []) : undefined };
            });
            const shownKeys = Array.isArray(columns) ? columns.map(S) : keys;
            const head = [isMap ? '(iteration index)' : '(index)'].concat(isMap ? ['Key'] : [], shownKeys, hasValues ? ['Values'] : []);
            const lines = body.map(function (b) {
              return [b.index].concat(isMap ? [b.mapKey] : [], shownKeys.map(function (k) { return b.cells[k] === undefined ? '' : b.cells[k]; }), hasValues ? [b.value === undefined ? '' : b.value] : []);
            });
            const widths = head.map(function (h, c) { return Math.max(h.length, ...lines.map(function (l) { return l[c].length; })) + 2; });
            const rule = function (l, m, r) { return l + widths.map(function (w) { return '─'.repeat(w); }).join(m) + r; };
            const row = function (cells) { return '│' + cells.map(function (t, c) { return cell(t, widths[c]); }).join('│') + '│'; };
            return [rule('┌', '┬', '┐'), row(head), rule('├', '┼', '┤')].concat(lines.map(row), [rule('└', '┴', '┘')]).join('\n');
          }
          const consoleObject = {};
          function define(name, length, fn) { method(consoleObject, name, length, fn); }
          function logger(level) { return function () { emit(level, format(Afrom(arguments))); }; }
          define('log', 0, logger('info'));
          define('info', 0, logger('info'));
          define('debug', 0, logger('info'));
          define('warn', 0, logger('warn'));
          define('error', 0, logger('error'));
          define('dirxml', 0, logger('info'));
          define('dir', 0, function dir(value) { emit('info', inspect(value, 1, [])); });
          define('trace', 0, function trace() { const text = format(Afrom(arguments)); emit('info', 'Trace' + (text ? ': ' + text : '') + stackText()); });
          define('assert', 0, function assert(condition) {
            if (condition) return;
            const rest = Afrom(arguments).slice(1);
            if (rest.length === 0) rest.push('Assertion failed');
            else if (typeof rest[0] === 'string') rest[0] = 'Assertion failed: ' + rest[0];
            else rest.unshift('Assertion failed:');
            emit('error', format(rest));
          });
          define('count', 0, function count(l) { l = label(l); const n = (counts.get(l) || 0) + 1; counts.set(l, n); emit('info', l + ': ' + n); });
          define('countReset', 0, function countReset(l) {
            l = label(l);
            if (!counts.has(l)) { emit('warn', "Count for '" + l + "' does not exist"); return; }
            counts.set(l, 0);
          });
          define('time', 0, function time(l) {
            l = label(l);
            if (timers.has(l)) { emit('warn', "Timer '" + l + "' already exists"); return; }
            timers.set(l, Date.now());
          });
          define('timeLog', 0, function timeLog(l) {
            l = label(l);
            if (!timers.has(l)) { emit('warn', "Timer '" + l + "' does not exist"); return; }
            const rest = Afrom(arguments).slice(1);
            emit('info', l + ': ' + elapsed(timers.get(l)) + (rest.length ? ' ' + format(rest) : ''));
          });
          define('timeEnd', 0, function timeEnd(l) {
            l = label(l);
            if (!timers.has(l)) { emit('warn', "Timer '" + l + "' does not exist"); return; }
            emit('info', l + ': ' + elapsed(timers.get(l)));
            timers.delete(l);
          });
          function group() { if (arguments.length) emit('info', format(Afrom(arguments))); groupDepth++; }
          define('group', 0, function group_() { group.apply(null, arguments); });
          define('groupCollapsed', 0, function groupCollapsed() { group.apply(null, arguments); });
          define('groupEnd', 0, function groupEnd() { if (groupDepth > 0) groupDepth--; });
          define('table', 1, function table(data, columns) { emit('info', renderTable(data, columns)); });
          define('clear', 0, function clear() { });
          Object.defineProperty(consoleObject, Symbol.toStringTag, { value: 'console', configurable: true });

          for (const [name, value] of [['TextEncoder', TextEncoder], ['TextDecoder', TextDecoder], ['atob', atob], ['btoa', btoa]])
            Object.defineProperty(globalThis, name, { value: value, writable: true, enumerable: false, configurable: true });
          Object.defineProperty(globalThis, 'crypto', { value: cryptoObject, writable: true, enumerable: false, configurable: true });
          Object.defineProperty(globalThis, 'console', { value: consoleObject, writable: true, enumerable: false, configurable: true });
        })(__folio_platform);
        """;
}
