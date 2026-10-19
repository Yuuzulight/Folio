using System.Text.Json;
using Folio.Resources;

namespace Folio.Scripting.Dom;

/// <summary>
/// The restricted Fetch API (issue #497, study 16): <c>fetch</c>, <c>Headers</c>, <c>Request</c> and <c>Response</c>.
/// GET and HEAD only; <c>data:</c> URLs are decoded in the content process and HTTP(S) goes to the host's loader as a
/// <see cref="ResourceKind.Fetch"/> request, which sends no cookies, credentials or Referer. Anything refused rejects with a
/// <c>TypeError</c> and reports a diagnostic. Response headers are the content type and length, nothing else.
/// </summary>
internal static class FetchBindings
{
    internal static ScriptValue Install(IScriptEngine engine, DomContext context)
    {
        var host = new HostObjectTemplate("FolioFetchHost")
            .DefineMethod("resolve", (_, _, args) =>
                ResourceLoader.Resolve(context.BaseUri?.AbsoluteUri, args.Length > 0 ? args[0].AsString() ?? "" : "") is { } url
                    ? ScriptValue.FromString(url)
                    : ScriptValue.Null)
            .DefineMethod("load", (_, _, args) => ScriptValue.FromString(Load(context, args.Length > 0 ? args[0].AsString() ?? "" : "", args.Length > 1 && args[1].AsBoolean())))
            // A body stays in the host until read: text() decodes it as UTF-8 (a BOM is dropped), bytes() hands it over in base64.
            .DefineMethod("text", (_, _, args) => ScriptValue.FromString(Utf8(context.FetchBody(Handle(args)))))
            .DefineMethod("bytes", (_, _, args) => ScriptValue.FromString(Convert.ToBase64String(context.FetchBody(Handle(args)) ?? [])))
            .DefineMethod("copy", (_, _, args) => ScriptValue.FromNumber(context.FetchBody(Handle(args)) is { } data ? context.StoreFetchBody(data) : 0))
            .DefineMethod("free", (_, _, args) => { context.FreeFetchBody(Handle(args)); return ScriptValue.Undefined; });
        engine.SetGlobalProperty("__folio_fetch_host", engine.CreateHostObject(host, context));
        var cacheApi = engine.EvaluateHostScript(Source, "folio:fetch");
        engine.Evaluate("delete globalThis.__folio_fetch_host");
        return cacheApi;
    }

    // The outcome as JSON: { ok, contentType, length, body } (body is a handle, 0 for a HEAD) or { ok: false, error }.
    private static string Load(DomContext context, string url, bool head)
    {
        var (outcome, error) = context.Fetch(url);
        if (outcome is not { } response)
            return JsonSerializer.Serialize(new { ok = false, error });
        return JsonSerializer.Serialize(new { ok = true, contentType = response.ContentType, length = response.Data!.Length, body = head ? 0 : context.StoreFetchBody(response.Data) });
    }

    private static int Handle(ReadOnlySpan<ScriptValue> args) => args.Length > 0 ? (int)args[0].AsNumber() : 0;

    private static string Utf8(byte[]? data)
    {
        if (data is null)
            return "";
        var bom = data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF ? 3 : 0;
        return System.Text.Encoding.UTF8.GetString(data, bom, data.Length - bom);
    }

    private const string Source = """
        (function(host) {
            'use strict';
            const global = globalThis;
            const TOKEN = /^[!#$%&'*+\-.^_`|~0-9A-Za-z]+$/;

            function utf8Encode(text) {
                const bytes = [];
                for (let i = 0; i < text.length; i++) {
                    let code = text.charCodeAt(i);
                    if (code >= 0xD800 && code <= 0xDBFF && i + 1 < text.length) {
                        const next = text.charCodeAt(i + 1);
                        if (next >= 0xDC00 && next <= 0xDFFF) { code = 0x10000 + ((code - 0xD800) << 10) + (next - 0xDC00); i++; }
                    }
                    if (code >= 0xD800 && code <= 0xDFFF) code = 0xFFFD;
                    if (code < 0x80) bytes.push(code);
                    else if (code < 0x800) bytes.push(0xC0 | (code >> 6), 0x80 | (code & 63));
                    else if (code < 0x10000) bytes.push(0xE0 | (code >> 12), 0x80 | ((code >> 6) & 63), 0x80 | (code & 63));
                    else bytes.push(0xF0 | (code >> 18), 0x80 | ((code >> 12) & 63), 0x80 | ((code >> 6) & 63), 0x80 | (code & 63));
                }
                return new Uint8Array(bytes);
            }

            function utf8Decode(bytes) {
                let out = '';
                let i = bytes.length >= 3 && bytes[0] === 0xEF && bytes[1] === 0xBB && bytes[2] === 0xBF ? 3 : 0;
                while (i < bytes.length) {
                    const b = bytes[i];
                    if (b < 0x80) { out += String.fromCharCode(b); i++; continue; }
                    let need = 0, code = 0, min = 0;
                    if (b >= 0xC2 && b <= 0xDF) { need = 1; code = b & 0x1F; min = 0x80; }
                    else if (b >= 0xE0 && b <= 0xEF) { need = 2; code = b & 0x0F; min = 0x800; }
                    else if (b >= 0xF0 && b <= 0xF4) { need = 3; code = b & 0x07; min = 0x10000; }
                    let valid = need > 0 && i + need < bytes.length;
                    for (let k = 1; valid && k <= need; k++) {
                        if ((bytes[i + k] & 0xC0) !== 0x80) valid = false;
                        else code = (code << 6) | (bytes[i + k] & 0x3F);
                    }
                    if (!valid || code < min || code > 0x10FFFF || (code >= 0xD800 && code <= 0xDFFF)) { out += '�'; i++; continue; }
                    if (code >= 0x10000) { code -= 0x10000; out += String.fromCharCode(0xD800 + (code >> 10), 0xDC00 + (code & 0x3FF)); }
                    else out += String.fromCharCode(code);
                    i += need + 1;
                }
                return out;
            }

            const ALPHABET = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/';
            const TABLE = {};
            for (let i = 0; i < 64; i++) TABLE[ALPHABET[i]] = i;
            function base64Decode(text) {
                const table = TABLE;
                const clean = text.replace(/=+$/, '');
                const bytes = new Uint8Array(Math.floor(clean.length * 3 / 4));
                let bits = 0, value = 0, at = 0;
                for (let i = 0; i < clean.length; i++) {
                    value = (value << 6) | table[clean[i]];
                    bits += 6;
                    if (bits >= 8) { bits -= 8; bytes[at++] = (value >> bits) & 0xFF; }
                }
                return bytes;
            }

            // ------------------------------------------------------------------ Headers
            const headerState = new WeakMap();
            const state = (headers) => {
                const s = headerState.get(headers);
                if (s === undefined) throw new TypeError('Illegal invocation');
                return s;
            };
            const headerName = (name) => {
                name = String(name);
                if (!TOKEN.test(name)) throw new TypeError("Invalid header name: '" + name + "'");
                return name.toLowerCase();
            };
            const sorted = (headers) => Array.from(state(headers), ([name, values]) => [name, values.join(', ')]).sort((a, b) => a[0] < b[0] ? -1 : a[0] > b[0] ? 1 : 0);
            const headerValue = (value) => String(value).replace(/^[\t\n\r ]+|[\t\n\r ]+$/g, '');

            class Headers {
                constructor(init) {
                    headerState.set(this, new Map());
                    if (init === undefined || init === null) return;
                    if (init instanceof Headers) {
                        for (const [name, value] of init) this.append(name, value);
                    } else if (typeof init[Symbol.iterator] === 'function') {
                        for (const pair of init) {
                            const entry = Array.from(pair);
                            if (entry.length !== 2) throw new TypeError('Each header pair must be a name/value tuple');
                            this.append(entry[0], entry[1]);
                        }
                    } else if (typeof init === 'object') {
                        for (const name of Object.keys(init)) this.append(name, init[name]);
                    } else {
                        throw new TypeError('The provided value is not of type HeadersInit');
                    }
                }
                append(name, value) {
                    const key = headerName(name), map = state(this);
                    if (!map.has(key)) map.set(key, []);
                    map.get(key).push(headerValue(value));
                }
                delete(name) { state(this).delete(headerName(name)); }
                get(name) { const values = state(this).get(headerName(name)); return values === undefined ? null : values.join(', '); }
                has(name) { return state(this).has(headerName(name)); }
                set(name, value) { state(this).set(headerName(name), [headerValue(value)]); }
                forEach(callback, thisArg) {
                    if (typeof callback !== 'function') throw new TypeError('The callback provided as parameter 1 is not a function');
                    for (const [name, value] of this) callback.call(thisArg, value, name, this);
                }
                keys() { return sorted(this).map((entry) => entry[0])[Symbol.iterator](); }
                values() { return sorted(this).map((entry) => entry[1])[Symbol.iterator](); }
                entries() { return sorted(this)[Symbol.iterator](); }
                [Symbol.iterator]() { return this.entries(); }
            }
            Object.defineProperty(Headers.prototype, Symbol.toStringTag, { value: 'Headers', configurable: true });

            // ------------------------------------------------------------------ Request
            const requestState = new WeakMap();
            const METHODS = { delete: 'DELETE', get: 'GET', head: 'HEAD', options: 'OPTIONS', post: 'POST', put: 'PUT' };

            class Request {
                constructor(input, init) {
                    init = init === undefined || init === null ? {} : init;
                    let url, method = 'GET', headers, signal = null;
                    if (input instanceof Request) {
                        const source = requestState.get(input);
                        url = source.url; method = source.method; headers = new Headers(source.headers); signal = source.signal;
                    } else {
                        const resolved = host.resolve(String(input));
                        if (resolved === null) throw new TypeError("Failed to construct 'Request': Failed to parse URL from " + String(input));
                        url = resolved;
                    }
                    if (init.method !== undefined) {
                        const raw = String(init.method);
                        if (!TOKEN.test(raw)) throw new TypeError("'" + raw + "' is not a valid HTTP method.");
                        const lower = raw.toLowerCase();
                        if (lower === 'connect' || lower === 'trace' || lower === 'track') throw new TypeError("'" + raw + "' HTTP method is unsupported.");
                        method = METHODS[lower] || raw;
                    }
                    if (init.headers !== undefined) headers = new Headers(init.headers);
                    if (init.signal !== undefined) {
                        if (init.signal !== null && !(init.signal instanceof AbortSignal)) throw new TypeError("Failed to construct 'Request': member signal is not of type AbortSignal.");
                        signal = init.signal;
                    }
                    if (init.body !== undefined && init.body !== null) throw new TypeError("Failed to construct 'Request': Request with GET/HEAD method cannot have body.");
                    requestState.set(this, { url, method, headers: headers || new Headers(), signal });
                }
                get url() { return requestState.get(this).url; }
                get method() { return requestState.get(this).method; }
                get headers() { return requestState.get(this).headers; }
                get signal() { return requestState.get(this).signal; }
                // No credentials, cookies or Referer are ever sent, whatever the page asks for.
                get credentials() { return 'omit'; }
                get referrer() { return ''; }
                get mode() { return 'cors'; }
                get redirect() { return 'follow'; }
                get cache() { return 'default'; }
                clone() { return new Request(this); }
            }
            Object.defineProperty(Request.prototype, Symbol.toStringTag, { value: 'Request', configurable: true });

            // ------------------------------------------------------------------ Response
            const responseState = new WeakMap();
            const INTERNAL = {};

            function bodyBytes(body) {
                if (body === undefined || body === null) return null;
                if (typeof body === 'string') return utf8Encode(body);
                if (body instanceof ArrayBuffer) return new Uint8Array(body.slice(0));
                if (ArrayBuffer.isView(body)) return new Uint8Array(body.buffer.slice(body.byteOffset, body.byteOffset + body.byteLength));
                return utf8Encode(String(body));
            }

            // Reads the body once, as 'text' or 'bytes'. A fetched body is held by the host (s.id); a constructed one is in s.bytes.
            function readBody(response, kind) {
                const s = responseState.get(response);
                if (s === undefined) throw new TypeError('Illegal invocation');
                if (s.signal && s.signal.aborted) throw s.signal.reason;
                if (s.used) throw new TypeError('Body has already been consumed.');
                s.used = s.id !== 0 && (s.id !== null || s.bytes !== null);
                if (s.id !== null) {
                    const id = s.id;
                    s.id = null;
                    try { return kind === 'text' ? host.text(id) : base64Decode(host.bytes(id)); } finally { host.free(id); }
                }
                const bytes = s.bytes === null ? new Uint8Array(0) : s.bytes;
                return kind === 'text' ? utf8Decode(bytes) : bytes;
            }

            class Response {
                constructor(body, init, token) {
                    init = init === undefined || init === null ? {} : init;
                    const status = init.status === undefined ? 200 : Number(init.status);
                    if (!Number.isInteger(status) || status < 200 || status > 599) throw new RangeError("Failed to construct 'Response': The status provided (" + init.status + ") is outside the range [200, 599].");
                    const headers = new Headers(init.headers);
                    const bytes = bodyBytes(body);
                    if (typeof body === 'string' && !headers.has('content-type')) headers.set('content-type', 'text/plain;charset=UTF-8');
                    responseState.set(this, { status, statusText: init.statusText === undefined ? '' : String(init.statusText), headers, bytes, id: null, used: false, url: '', type: 'default', signal: null });
                    if (token !== undefined && token !== INTERNAL) throw new TypeError('Illegal constructor');
                }
                static error() {
                    const response = new Response(null);
                    Object.assign(responseState.get(response), { status: 0, type: 'error' });
                    return response;
                }
                static json(data, init) {
                    const text = JSON.stringify(data);
                    if (text === undefined) throw new TypeError('The data is not JSON serializable');
                    init = Object.assign({}, init);
                    const headers = new Headers(init.headers);
                    if (!headers.has('content-type')) headers.set('content-type', 'application/json');
                    return new Response(text, Object.assign(init, { headers }));
                }
                get status() { return responseState.get(this).status; }
                get ok() { const status = this.status; return status >= 200 && status <= 299; }
                get statusText() { return responseState.get(this).statusText; }
                get headers() { return responseState.get(this).headers; }
                get url() { return responseState.get(this).url; }
                get type() { return responseState.get(this).type; }
                get redirected() { return false; }
                get bodyUsed() { return responseState.get(this).used; }
                get body() { return null; }
                text() { try { return Promise.resolve(readBody(this, 'text')); } catch (e) { return Promise.reject(e); } }
                json() { try { return Promise.resolve(JSON.parse(readBody(this, 'text'))); } catch (e) { return Promise.reject(e); } }
                arrayBuffer() { try { return Promise.resolve(readBody(this, 'bytes').slice().buffer); } catch (e) { return Promise.reject(e); } }
                bytes() { try { return Promise.resolve(readBody(this, 'bytes').slice()); } catch (e) { return Promise.reject(e); } }
                blob() { return Promise.reject(new TypeError('Response.blob() is not supported.')); }
                formData() { return Promise.reject(new TypeError('Response.formData() is not supported.')); }
                clone() {
                    const s = responseState.get(this);
                    if (s.used) throw new TypeError('Response body is already used');
                    const copy = new Response(null, { status: s.status === 0 ? 200 : s.status, statusText: s.statusText, headers: s.headers });
                    Object.assign(responseState.get(copy), { status: s.status, bytes: s.bytes === null ? null : s.bytes.slice(), id: s.id === null ? null : host.copy(s.id), url: s.url, type: s.type, signal: s.signal });
                    return copy;
                }
            }
            Object.defineProperty(Response.prototype, Symbol.toStringTag, { value: 'Response', configurable: true });

            // ------------------------------------------------------------------ fetch
            function fetch(input, init) {
                return new Promise(function(resolve, reject) {
                    let request;
                    try { request = new Request(input, init); } catch (e) { reject(e); return; }
                    const signal = request.signal;
                    if (signal && signal.aborted) { reject(signal.reason); return; }
                    if (request.method !== 'GET' && request.method !== 'HEAD') {
                        reject(new TypeError('Failed to fetch: the ' + request.method + ' method is not allowed, only GET and HEAD.'));
                        return;
                    }
                    let settled = false;
                    const onAbort = function() { if (!settled) { settled = true; reject(signal.reason); } };
                    if (signal) signal.addEventListener('abort', onAbort, { once: true });
                    // The load is a task, so an abort() after the call and before the load wins (a microtask where no timers are installed).
                    const defer = typeof global.setTimeout === 'function' ? function(task) { global.setTimeout(task, 0); } : function(task) { Promise.resolve().then(task); };
                    defer(function() {
                        if (settled) return;
                        settled = true;
                        if (signal) signal.removeEventListener('abort', onAbort);
                        if (signal && signal.aborted) { reject(signal.reason); return; }
                        const controller = global.navigator && global.navigator.serviceWorker && global.navigator.serviceWorker.controller;
                        const interception = controller && typeof controller._dispatchFetch === 'function' ? controller._dispatchFetch(request) : Promise.resolve(null);
                        Promise.resolve(interception).then(function(intercepted) {
                            if (intercepted instanceof Response) { resolve(intercepted); return; }
                            try {
                                const result = JSON.parse(host.load(request.url, request.method === 'HEAD'));
                                if (!result.ok) { reject(new TypeError('Failed to fetch: ' + result.error)); return; }
                                const headers = new Headers();
                                if (result.contentType) headers.set('content-type', result.contentType);
                                headers.set('content-length', String(result.length));
                                const response = new Response(null, { headers, statusText: 'OK' }, INTERNAL);
                                Object.assign(responseState.get(response), { id: result.body === 0 ? null : result.body, url: request.url, type: 'basic', signal: signal });
                                resolve(response);
                            } catch (e) {
                                reject(new TypeError('Failed to fetch: ' + (e && e.message)));
                            }
                        }, function(error) { reject(error); });
                    });
                });
            }

            for (const [name, value] of Object.entries({ fetch, Headers, Request, Response }))
                Object.defineProperty(global, name, { value, writable: true, configurable: true, enumerable: false });
            // A private seam for Cache: metadata and body access use the actual
            // Fetch states, never artifact-defined getters or public clone hooks.
            const cacheApply = Reflect.apply, cacheGet = WeakMap.prototype.get, cacheSet = WeakMap.prototype.set;
            const cacheMapEach = Map.prototype.forEach, cacheMapSet = Map.prototype.set;
            const cacheJoin = Array.prototype.join, cacheKeys = Object.keys, cacheCreate = Object.create;
            const CacheString = String, CacheUint8Array = Uint8Array;
            const requestPrototype = Request.prototype, responsePrototype = Response.prototype, headersPrototype = Headers.prototype;
            const NativeMap = Map;
            function cacheRecord() { return cacheCreate(null); }
            function cacheHeaders(headers) {
                const map = cacheApply(cacheGet, headerState, [headers]), result = [];
                cacheApply(cacheMapEach, map, [function(values, name) {
                    result[result.length] = [name, cacheApply(cacheJoin, values, [', '])];
                }]);
                return result;
            }
            function cacheRequest(input) {
                const existing = input !== null && (typeof input === 'object' || typeof input === 'function')
                    ? cacheApply(cacheGet, requestState, [input]) : undefined;
                const original = existing ? input : new Request(CacheString(input));
                const value = cacheApply(cacheGet, requestState, [original]), result = cacheRecord();
                result.url = value.url; result.method = value.method; result.headers = cacheHeaders(value.headers); result.signal = value.signal;
                return result;
            }
            function restoreHeaders(values) {
                const result = cacheCreate(headersPrototype), map = new NativeMap();
                const names = cacheKeys(values);
                for (let i = 0; i < names.length; i++) cacheApply(cacheMapSet, map, [names[i], [values[names[i]]]]);
                cacheApply(cacheSet, headerState, [result, map]);
                return result;
            }
            function restoreRequest(value, lower) {
                const result = cacheCreate(requestPrototype), data = cacheRecord();
                data.url = lower ? value.url : value.Url; data.method = lower ? value.method : value.Method;
                if (lower) {
                    const map = cacheCreate(null);
                    for (let i = 0; i < value.headers.length; i++) map[value.headers[i][0]] = value.headers[i][1];
                    data.headers = restoreHeaders(map);
                } else data.headers = restoreHeaders(value.Headers);
                data.signal = lower ? value.signal : null;
                cacheApply(cacheSet, requestState, [result, data]);
                return result;
            }
            function cacheResponse(request, response, fetchOnly) {
                const s = response !== null && (typeof response === 'object' || typeof response === 'function')
                    ? cacheApply(cacheGet, responseState, [response]) : undefined;
                if (!s || s.used || s.status === 206 || (fetchOnly && (s.status < 200 || s.status > 299)))
                    throw new TypeError('The response cannot be cached');
                const headers = cacheHeaders(s.headers);
                for (let i = 0; i < headers.length; i++)
                    if (headers[i][0] === 'vary' && /(^|,)\s*\*\s*(,|$)/.test(headers[i][1]))
                        throw new TypeError('A Vary:* response cannot be cached');
                if (s.signal && s.signal.aborted) throw s.signal.reason;
                const result = cacheRecord();
                result.request = request; result.status = s.status; result.statusText = s.statusText;
                result.headers = headers; result.url = s.url; result.type = s.type;
                result.bodyNull = s.id === 0 || (s.id === null && s.bytes === null);
                s.used = s.id !== 0 && (s.id !== null || s.bytes !== null);
                if (s.id !== null) {
                    const id = s.id; s.id = null;
                    try { result.bytes = base64Decode(host.bytes(id)); } finally { host.free(id); }
                } else result.bytes = s.bytes === null ? new CacheUint8Array(0) : s.bytes;
                return result;
            }
            function restoreResponse(value) {
                const result = cacheCreate(responsePrototype), data = cacheRecord();
                data.status = value.Status; data.statusText = value.StatusText;
                data.headers = restoreHeaders(value.Headers); data.bytes = value.BodyNull ? null : base64Decode(value.Bytes);
                data.id = null; data.used = false; data.url = value.Url; data.type = value.Type; data.signal = null;
                cacheApply(cacheSet, responseState, [result, data]);
                return result;
            }
            const cacheApi = cacheRecord();
            cacheApi.request = cacheRequest; cacheApi.response = cacheResponse; cacheApi.restore = restoreResponse;
            cacheApi.restoreRequest = value => restoreRequest(value, false);
            cacheApi.restoreRequestLower = value => restoreRequest(value, true);
            return cacheApi;
        })(globalThis.__folio_fetch_host);
        """;
}
