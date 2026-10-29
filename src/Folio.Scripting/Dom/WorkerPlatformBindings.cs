using System.Security.Cryptography;
using System.Text;

namespace Folio.Scripting.Dom;

/// <summary>DOM-free platform objects shared by dedicated workers.</summary>
internal static class WorkerPlatformBindings
{
    internal static void Install(IScriptEngine engine, Uri? baseUri = null,
        Action<string, string>? reportConsole = null,
        Func<string, string>? createBlobUrl = null,
        Action<string>? revokeBlobUrl = null)
    {
        DomBindings.InstallDomException(engine);
        var host = new HostState(engine, baseUri, reportConsole, createBlobUrl, revokeBlobUrl);
        var template = new HostObjectTemplate("WorkerPlatformHost")
            .DefineProperty("processorCount", (_, _) => ScriptValue.FromNumber(Environment.ProcessorCount))
            .DefineProperty("language", (_, _) => ScriptValue.FromString(System.Globalization.CultureInfo.CurrentUICulture.Name is { Length: > 0 } language ? language : "en-US"))
            .DefineMethod("encode", (t, _, a) => ((HostState)t).Encode(a.Length == 0 ? "" : a[0].AsString() ?? ""))
            .DefineMethod("decode", (t, _, a) => ((HostState)t).Decode(a.Length == 0 ? [] : ReadNumbers(((HostState)t).Engine, a[0]), a.Length > 1 && a[1].AsBoolean()))
            .DefineMethod("blobText", (t, _, a) => ((HostState)t).DecodeBlob(ReadNumbers(((HostState)t).Engine, a[0])))
            .DefineMethod("random", (t, _, a) => ((HostState)t).Random(a.Length == 0 ? 0 : checked((int)a[0].AsNumber())))
            .DefineMethod("base64Encode", (t, _, a) => ((HostState)t).Base64Encode(a.Length == 0 ? "" : a[0].AsString() ?? ""))
            .DefineMethod("base64Decode", (t, _, a) => ((HostState)t).Base64Decode(a.Length == 0 ? "" : a[0].AsString() ?? ""))
            .DefineMethod("resolveUrl", (t, _, a) => ((HostState)t).Resolve(a.Length == 0 ? "" : a[0].AsString() ?? "", a.Length > 1 ? a[1].AsString() : null))
            .DefineMethod("console", (t, _, a) => { ((HostState)t).Console(a.Length > 0 ? a[0].AsString() ?? "log" : "log", a.Length > 1 ? a[1].AsString() ?? "" : ""); return ScriptValue.Undefined; })
            .DefineMethod("blobUrl", (t, _, a) => ((HostState)t).BlobUrl(a.Length == 0 ? "" : a[0].AsString() ?? ""))
            .DefineMethod("revokeBlobUrl", (t, _, a) => { ((HostState)t).RevokeBlobUrl(a.Length > 0 ? a[0].AsString() ?? "" : ""); return ScriptValue.Undefined; });
        if (engine is Folio.Scripting.Native.NativeJavaScriptEngine)
        {
            var navigator = engine.CreatePlainObject(new Dictionary<string, ScriptValue>
            {
                ["userAgent"] = ScriptValue.FromString("Folio"),
                ["hardwareConcurrency"] = ScriptValue.FromNumber(Environment.ProcessorCount),
                ["language"] = ScriptValue.FromString(System.Globalization.CultureInfo.CurrentUICulture.Name is { Length: > 0 } language ? language : "en-US"),
            });
            engine.SetGlobalProperty("navigator", navigator);
            engine.SetGlobalProperty("console", engine.CreateHostObject(new HostObjectTemplate("NativeWorkerConsole")
                .DefineMethod("log", (target, _, args) => { ((HostState)target).Console("log", args.Length > 0 ? args[0].AsString() ?? "" : ""); return ScriptValue.Undefined; })
                .DefineMethod("warn", (target, _, args) => { ((HostState)target).Console("warn", args.Length > 0 ? args[0].AsString() ?? "" : ""); return ScriptValue.Undefined; }), host));
        }
        engine.SetGlobalProperty("__folio_worker_platform", engine.CreateHostObject(template, host));
        engine.EvaluateHostScript(Source, "folio:worker-platform");
        engine.Evaluate("delete globalThis.__folio_worker_platform;", "folio:worker-platform-cleanup");
    }

    private static IReadOnlyList<byte> ReadNumbers(IScriptEngine engine, ScriptValue value)
    {
        if (!engine.TryGetArrayItems(value, out var items) || items.Count > 16 * 1024 * 1024)
            throw WebIdlException.TypeError("The input is not a bounded byte sequence.");
        var bytes = new byte[items.Count];
        for (var i = 0; i < bytes.Length; i++)
        {
            var n = items[i].AsNumber();
            if (!double.IsFinite(n) || n < 0 || n > 255) throw WebIdlException.TypeError("The input contains an invalid byte.");
            bytes[i] = (byte)n;
        }
        return bytes;
    }

    private sealed class HostState
    {
        internal HostState(IScriptEngine engine, Uri? baseUri, Action<string, string>? reportConsole, Func<string, string>? createBlobUrl, Action<string>? revokeBlobUrl)
            => (Engine, BaseUri, ReportConsole, CreateBlobUrl, RevokeCallback) = (engine, baseUri, reportConsole, createBlobUrl, revokeBlobUrl);
        internal IScriptEngine Engine { get; }
        private Uri? BaseUri { get; }
        private Action<string, string>? ReportConsole { get; }
        private Func<string, string>? CreateBlobUrl { get; }
        private Action<string>? RevokeCallback { get; }
        internal ScriptValue Encode(string value)
        {
            if (Encoding.UTF8.GetByteCount(value) > 16 * 1024 * 1024)
                throw WebIdlException.DomException("QuotaExceededError", "The encoded input is too large.");
            return Engine.CreateArray(Encoding.UTF8.GetBytes(value).Select(b => ScriptValue.FromNumber(b)).ToArray());
        }
        internal ScriptValue Decode(IReadOnlyList<byte> bytes, bool fatal)
        {
            try { return ScriptValue.FromString(new UTF8Encoding(false, fatal).GetString(bytes.ToArray())); }
            catch (DecoderFallbackException) { throw WebIdlException.TypeError("The byte sequence is not valid UTF-8."); }
        }
        internal ScriptValue DecodeBlob(IReadOnlyList<byte> bytes)
        {
            var text = Encoding.UTF8.GetString(bytes.ToArray());
            return ScriptValue.FromString(text.StartsWith('\uFEFF') ? text[1..] : text);
        }
        internal ScriptValue Random(int length)
        {
            if (length < 0 || length > 65536) throw WebIdlException.DomException("QuotaExceededError", "The requested random buffer is too large.");
            var bytes = new byte[length]; RandomNumberGenerator.Fill(bytes);
            return Engine.CreateArray(bytes.Select(b => ScriptValue.FromNumber(b)).ToArray());
        }
        internal ScriptValue Base64Encode(string value) => ScriptValue.FromString(Convert.ToBase64String(Encoding.Latin1.GetBytes(value)));
        internal ScriptValue Base64Decode(string value)
        {
            value = string.Concat(value.Where(c => c is not (' ' or '\n' or '\r' or '\t' or '\f')));
            if (value.Length % 4 == 0)
            {
                if (value.EndsWith("==", StringComparison.Ordinal)) value = value[..^2];
                else if (value.EndsWith('=')) value = value[..^1];
            }
            if (value.Length % 4 == 1 || value.Any(c => !(c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '+' or '/')))
                throw WebIdlException.DomException("InvalidCharacterError", "The string is not valid base64.");
            value = value.PadRight((value.Length + 3) / 4 * 4, '=');
            try { return ScriptValue.FromString(Encoding.Latin1.GetString(Convert.FromBase64String(value))); }
            catch (FormatException) { throw WebIdlException.DomException("InvalidCharacterError", "The string is not valid base64."); }
        }
        internal ScriptValue Resolve(string value, string? baseText)
        {
            var baseValue = Uri.TryCreate(baseText, UriKind.Absolute, out var supplied) ? supplied : null;
            if (Uri.TryCreate(value, UriKind.Absolute, out var absolute)) return ScriptValue.FromString(absolute.AbsoluteUri);
            if (baseValue is not null && Uri.TryCreate(baseValue, value, out var resolved)) return ScriptValue.FromString(resolved.AbsoluteUri);
            throw WebIdlException.TypeError("Invalid URL.");
        }
        internal void Console(string level, string message) { try { ReportConsole?.Invoke(level, message); } catch { } }
        internal ScriptValue BlobUrl(string text) => CreateBlobUrl is null ? throw WebIdlException.DomException("NotSupportedError", "Object URLs are unavailable in this worker.") : ScriptValue.FromString(CreateBlobUrl(text));
        internal void RevokeBlobUrl(string url) { try { RevokeCallback?.Invoke(url); } catch { } }
    }

    private const string Source = """
        (function(host) {
          'use strict';
          const U8 = Uint8Array, U8C = Uint8ClampedArray, AB = ArrayBuffer, isView = AB.isView;
          const Afrom = Array.from, S = String, P = Promise, Okeys = Object.keys;
          const workerNavigator=Object.create(null);
          Object.defineProperties(workerNavigator,{
            userAgent:{value:'Folio',enumerable:true},
            hardwareConcurrency:{value:host.processorCount,enumerable:true},
            language:{value:host.language,enumerable:true},
            languages:{value:Object.freeze([host.language]),enumerable:true},
            [Symbol.toStringTag]:{value:'WorkerNavigator'}
          });
          Object.freeze(workerNavigator);
          Object.defineProperty(globalThis,'navigator',{get:function(){return workerNavigator;},configurable:true,enumerable:true});
          const views = [Int8Array, Uint8Array, Uint8ClampedArray, Int16Array, Uint16Array, Int32Array, Uint32Array];
          const apply=Reflect.apply, getDescriptor=Object.getOwnPropertyDescriptor;
          const typedProto=Object.getPrototypeOf(U8.prototype), typedTag=getDescriptor(typedProto,Symbol.toStringTag).get;
          const typedBuffer=getDescriptor(typedProto,'buffer').get, typedOffset=getDescriptor(typedProto,'byteOffset').get, typedLength=getDescriptor(typedProto,'byteLength').get;
          const typedCount=getDescriptor(typedProto,'length').get;
          const dataBuffer=getDescriptor(DataView.prototype,'buffer').get,dataOffset=getDescriptor(DataView.prototype,'byteOffset').get,dataLength=getDescriptor(DataView.prototype,'byteLength').get;
          const arrayLength=getDescriptor(AB.prototype,'byteLength').get;
          function typedBrand(v){try{return apply(typedTag,v,[]);}catch(_){return undefined;}}
          function bytesOf(v) {
            if(isView(v)) return new U8(v.buffer, v.byteOffset, v.byteLength);
            if(v && typeof v.byteLength === 'number') return new U8(v);
            throw new TypeError('Expected a BufferSource.');
          }
          function byteList(view){const length=typeof view.byteLength==='number'?view.byteLength:view.length,bytes=[];for(let i=0;i<length;i++)bytes[i]=view[i];return bytes;}
          globalThis.TextEncoder = function() {};
          TextEncoder.prototype.encode = function(v) { return new U8(host.encode(v===undefined?'':S(v))); };
          TextEncoder.prototype.encodeInto = function(v, dest) { if(!dest||typeof dest.length!=='number')throw new TypeError('Expected Uint8Array.'); const text=v===undefined?'':S(v),capacity=dest.length; let read=0,written=0; while(read<text.length){const cp=text.codePointAt(read), chars=cp>65535?2:1, b=host.encode(text.slice(read,read+chars)); if(written+b.length>capacity) break; for(let i=0;i<b.length;i++) dest[written+i]=b[i]; read+=chars; written+=b.length;} return { read, written }; };
          const decoderStates = new WeakMap();
          globalThis.TextDecoder = function(label, options) {
            if (!new.target) throw new TypeError('TextDecoder requires new.');
            const name=label===undefined?'utf-8':S(label).trim().toLowerCase();
            if(name!=='utf-8'&&name!=='utf8'&&name!=='unicode-1-1-utf-8') throw new RangeError('The encoding is not supported.');
            decoderStates.set(this,{fatal:!!(options&&options.fatal),ignoreBOM:!!(options&&options.ignoreBOM),pending:[],bomSeen:false});
          };
          for(const key of ['fatal','ignoreBOM','encoding']) Object.defineProperty(TextDecoder.prototype,key,{get:function(){const state=decoderStates.get(this);if(!state)throw new TypeError('Illegal invocation.');return key==='encoding'?'utf-8':state[key];}});
          TextDecoder.prototype.decode = function(v, options) {
            const state=decoderStates.get(this);if(!state)throw new TypeError('Illegal invocation.');
            const chunk=v===undefined?[]:Afrom(bytesOf(v)), all=state.pending.concat(chunk), stream=!!(options&&options.stream);
            let end=all.length;
            if(stream) for(let start=Math.max(0,end-3);start<end;start++) {
              const lead=all[start], need=lead>=0xC2&&lead<=0xDF?2:lead>=0xE0&&lead<=0xEF?3:lead>=0xF0&&lead<=0xF4?4:0;
              if(!need||end-start>=need)continue;
              let valid=true;
              for(let j=start+1;j<end;j++) {
                const low=j===start+1&&lead===0xE0?0xA0:j===start+1&&lead===0xF0?0x90:0x80;
                const high=j===start+1&&lead===0xED?0x9F:j===start+1&&lead===0xF4?0x8F:0xBF;
                if(all[j]<low||all[j]>high){valid=false;break;}
              }
              if(valid){end=start;break;}
            }
            let output;
            try {output=host.decode(all.slice(0,end),state.fatal);}
            catch(error){state.pending=[];state.bomSeen=false;throw error;}
            state.pending=stream?all.slice(end):[];
            if(!state.bomSeen&&output.length){state.bomSeen=true;if(!state.ignoreBOM&&output.charCodeAt(0)===0xFEFF)output=output.slice(1);}
            if(!stream)state.bomSeen=false;
            return output;
          };
          const Encoder=TextEncoder, Decoder=TextDecoder;
          globalThis.atob = function(v) { return host.base64Decode(S(v)); };
          globalThis.btoa = function(v) { v=S(v); for (let i=0;i<v.length;i++) if (v.charCodeAt(i)>255) throw new DOMException('Invalid character','InvalidCharacterError'); return host.base64Encode(v); };
          const bigViews = [typeof BigInt64Array==='function'?BigInt64Array:null, typeof BigUint64Array==='function'?BigUint64Array:null].filter(Boolean); const allViews=views.concat(bigViews);
          globalThis.crypto = { getRandomValues: function(v) {
            if(!v||typeof v.length!=='number')throw new TypeError('Expected an integer typed array.');
            const out=bytesOf(v),length=typeof v.length==='number'?v.length:out.length;if(length>65536)throw new DOMException('The requested random buffer is too large.','QuotaExceededError');
            const b=host.random(length);for(let i=0;i<length;i++)out[i]=b[i];return v;
          } };
          globalThis.console = {}; ['log','info','warn','error','debug','dir','trace'].forEach(k => console[k] = function(...a) { host.console(k, a.map(S).join(' ')); });
          function formDecode(value) {
            const text=S(value).replace(/\+/g,' '), bytes=[];
            for(let i=0;i<text.length;) {
              if(text[i]==='%'&&i+2<text.length&&/^[0-9a-fA-F]{2}$/.test(text.slice(i+1,i+3))){bytes.push(parseInt(text.slice(i+1,i+3),16));i+=3;}
              else {const cp=text.codePointAt(i),count=cp>65535?2:1;for(const byte of host.encode(text.slice(i,i+count)))bytes.push(byte);i+=count;}
            }
            return host.decode(bytes,false);
          }
          function formEncode(value) {
            let out='';for(const byte of host.encode(S(value))){if(byte===32)out+='+';else if(byte>=65&&byte<=90||byte>=97&&byte<=122||byte>=48&&byte<=57||byte===42||byte===45||byte===46||byte===95)out+=S.fromCharCode(byte);else out+='%'+byte.toString(16).toUpperCase().padStart(2,'0');}return out;
          }
          globalThis.URLSearchParams = function(init) {
            this._p=[];
            if(init===undefined||init===null)return;
            if(typeof init==='object'&&typeof init[Symbol.iterator]==='function') {for(const pair of init){const parts=Afrom(pair);if(parts.length!==2)throw new TypeError('Expected a pair.');this.append(parts[0],parts[1]);}}
            else if(typeof init==='object'){for(const key of Okeys(init))this.append(key,init[key]);}
            else for(const x of S(init).replace(/^\?/,'').split('&'))if(x){const i=x.indexOf('=');this.append(formDecode(i<0?x:x.slice(0,i)),formDecode(i<0?'':x.slice(i+1)));}
          };
          URLSearchParams.prototype.append=function(k,v){this._p.push([S(k),S(v)]);};
          URLSearchParams.prototype.get=function(k){const x=this._p.find(p=>p[0]===S(k));return x?x[1]:null;};
          URLSearchParams.prototype.getAll=function(k){return this._p.filter(p=>p[0]===S(k)).map(p=>p[1]);};
          URLSearchParams.prototype.has=function(k,v){return this._p.some(p=>p[0]===S(k)&&(v===undefined||p[1]===S(v)));};
          URLSearchParams.prototype.delete=function(k,v){this._p=this._p.filter(p=>!(p[0]===S(k)&&(v===undefined||p[1]===S(v))));};
          URLSearchParams.prototype.set=function(k,v){k=S(k);v=S(v);let found=false;this._p=this._p.filter(p=>{if(p[0]!==k)return true;if(found)return false;p[1]=v;found=true;return true;});if(!found)this._p.push([k,v]);};
          URLSearchParams.prototype.sort=function(){this._p.sort((a,b)=>a[0]<b[0]?-1:a[0]>b[0]?1:0);};
          URLSearchParams.prototype.toString=function(){return this._p.map(p=>formEncode(p[0])+'='+formEncode(p[1])).join('&');};
          URLSearchParams.prototype.entries=function(){return this._p.map(p=>p.slice())[Symbol.iterator]();};
          URLSearchParams.prototype.keys=function(){return this._p.map(p=>p[0])[Symbol.iterator]();};
          URLSearchParams.prototype.values=function(){return this._p.map(p=>p[1])[Symbol.iterator]();};
          URLSearchParams.prototype.forEach=function(fn,receiver){for(const p of this._p)fn.call(receiver,p[1],p[0],this);};
          URLSearchParams.prototype[Symbol.iterator]=URLSearchParams.prototype.entries;
          Object.defineProperty(URLSearchParams.prototype,'size',{get:function(){return this._p.length;}});
          globalThis.URL = function(v,b) { const href=host.resolveUrl(S(v), b===undefined?undefined:S(b)); const m=/^(?:([^:]+):)?(?:\/\/([^\/]*))?([^?#]*)(\?[^#]*)?(#.*)?$/.exec(href)||[]; this.href=href; this.protocol=m[1]?m[1]+':':''; this.host=(m[2]||'').replace(/^.*@/,''); this.origin=this.protocol&&this.host?this.protocol+'//'+this.host:'null'; this.pathname=m[3]||''; this.search=m[4]||''; this.hash=m[5]||''; this.searchParams=new URLSearchParams(this.search); };
          globalThis.URL.createObjectURL=function(blob){const state=blobStates.get(blob);return host.blobUrl(state?host.blobText(byteList(state.bytes)):'');}; globalThis.URL.revokeObjectURL=function(url){host.revokeBlobUrl(S(url));};
          const blobStates=new WeakMap(); function blobPart(p){if(typeof p==='string')return new U8(host.encode(p));if(blobStates.has(p))return blobStates.get(p).bytes.slice();if(p instanceof AB)return new U8(p);if(isView(p))return new U8(p.buffer,p.byteOffset,p.byteLength);return new U8(host.encode(S(p)));}
          const BlobCtor=function(parts,options){const chunks=Afrom(parts||[]).map(blobPart);let n=0;for(const c of chunks)n+=c.byteLength;if(n>16*1024*1024)throw new DOMException('The Blob is too large.','QuotaExceededError');const bytes=new U8(n);let at=0;for(const c of chunks){bytes.set(c,at);at+=c.byteLength;}let type=options&&options.type?S(options.type).toLowerCase():'';if(/[^\x20-\x7E]/.test(type))type='';blobStates.set(this,{bytes,type});Object.defineProperty(this,'size',{get:()=>bytes.byteLength});Object.defineProperty(this,'type',{get:()=>type});}; BlobCtor.prototype.text=function(){return P.resolve(host.blobText(byteList(blobStates.get(this).bytes)));}; BlobCtor.prototype.arrayBuffer=function(){return P.resolve(blobStates.get(this).bytes.slice().buffer);}; BlobCtor.prototype.slice=function(start,end,type){const state=blobStates.get(this);let a=start===undefined?0:Number(start)||0,b=end===undefined?state.bytes.length:Number(end)||0;if(a<0)a=Math.max(state.bytes.length+a,0);else a=Math.min(a,state.bytes.length);if(b<0)b=Math.max(state.bytes.length+b,0);else b=Math.min(b,state.bytes.length);if(b<a)b=a;return new BlobCtor([state.bytes.slice(a,b)],{type:type===undefined?'':S(type).toLowerCase()});}; globalThis.Blob=BlobCtor;
          globalThis.FormData=function(form){if(!new.target||form!==undefined)throw new TypeError('Expected an HTMLFormElement.');this._entries=[];};
          if(typeof EventTarget==='undefined'){
            globalThis.Event=function(type,init){this.type=S(type);this.bubbles=!!(init&&init.bubbles);this.cancelable=!!(init&&init.cancelable);this.defaultPrevented=false;};
            globalThis.EventTarget=function(){this._listeners={};};
            EventTarget.prototype.addEventListener=function(type,fn,options){if(typeof fn!=='function')return;type=S(type);var list=this._listeners[type]||(this._listeners[type]=[]);if(list.indexOf(fn)<0)list.push(fn);if(options&&options.signal&&typeof options.signal._watch==='function')options.signal._watch(this,type,fn);};
            EventTarget.prototype.removeEventListener=function(type,fn){var list=this._listeners[S(type)]||[],i=list.indexOf(fn);if(i>=0)list.splice(i,1);};
            EventTarget.prototype.dispatchEvent=function(event){event.target=this;var list=(this._listeners[event.type]||[]).slice();for(var i=0;i<list.length;i++)list[i].call(this,event);return true;};
            globalThis.AbortSignal=function(){this.aborted=false;this.reason=undefined;this._targets=[];};
            AbortSignal.prototype._watch=function(target,type,fn){if(!this.aborted)this._targets.push([target,type,fn]);else target.removeEventListener(type,fn);};
            globalThis.AbortController=function(){this.signal=new AbortSignal();};
            AbortController.prototype.abort=function(reason){if(this.signal.aborted)return;this.signal.aborted=true;this.signal.reason=reason===undefined?new DOMException('The operation was aborted.','AbortError'):reason;var targets=this.signal._targets.splice(0);for(var i=0;i<targets.length;i++)targets[i][0].removeEventListener(targets[i][1],targets[i][2]);};
          }
          function formValue(v){return blobStates.has(v)?v:S(v);}
          FormData.prototype.append=function(k,v){this._entries.push([S(k),formValue(v)]);};
          FormData.prototype.get=function(k){const x=this._entries.find(p=>p[0]===S(k));return x?x[1]:null;};
          FormData.prototype.getAll=function(k){return this._entries.filter(p=>p[0]===S(k)).map(p=>p[1]);};
          FormData.prototype.has=function(k){return this._entries.some(p=>p[0]===S(k));};
          FormData.prototype.delete=function(k){this._entries=this._entries.filter(p=>p[0]!==S(k));};
          FormData.prototype.set=function(k,v){k=S(k);v=formValue(v);let found=false;this._entries=this._entries.filter(p=>{if(p[0]!==k)return true;if(found)return false;p[1]=v;found=true;return true;});if(!found)this._entries.push([k,v]);};
          FormData.prototype.entries=function(){return this._entries.map(p=>p.slice())[Symbol.iterator]();};
          FormData.prototype.keys=function(){return this._entries.map(p=>p[0])[Symbol.iterator]();};
          FormData.prototype.values=function(){return this._entries.map(p=>p[1])[Symbol.iterator]();};
          FormData.prototype.forEach=function(fn,receiver){for(const p of this._entries)fn.call(receiver,p[1],p[0],this);};
          FormData.prototype[Symbol.iterator]=FormData.prototype.entries;
        })(__folio_worker_platform);
        """;
}
