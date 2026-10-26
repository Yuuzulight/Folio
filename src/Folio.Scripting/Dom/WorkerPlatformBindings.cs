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
        PlatformBindings.Install(engine, reportConsole is null ? null : (level, text, _, _) => reportConsole(level, text));
        var host = new HostState(engine, createBlobUrl, revokeBlobUrl);
        var template = new HostObjectTemplate("WorkerPlatformHost")
            .DefineProperty("processorCount", (_, _) => ScriptValue.FromNumber(Environment.ProcessorCount))
            .DefineProperty("language", (_, _) => ScriptValue.FromString(System.Globalization.CultureInfo.CurrentUICulture.Name is { Length: > 0 } language ? language : "en-US"))
            .DefineMethod("encode", (t, _, a) => ((HostState)t).Encode(a.Length == 0 ? "" : a[0].AsString() ?? ""))
            .DefineMethod("blobText", (t, _, a) => ((HostState)t).DecodeBlob(ReadNumbers(((HostState)t).Engine, a[0])))
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
            return;
        }
        engine.SetGlobalProperty("__folio_worker_platform", engine.CreateHostObject(template, host));
        UrlBindings.Install(engine);
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
        internal HostState(IScriptEngine engine, Func<string, string>? createBlobUrl, Action<string>? revokeBlobUrl)
            => (Engine, CreateBlobUrl, RevokeCallback) = (engine, createBlobUrl, revokeBlobUrl);
        internal IScriptEngine Engine { get; }
        private Func<string, string>? CreateBlobUrl { get; }
        private Action<string>? RevokeCallback { get; }
        internal ScriptValue Encode(string value)
        {
            if (Encoding.UTF8.GetByteCount(value) > 16 * 1024 * 1024)
                throw WebIdlException.DomException("QuotaExceededError", "The encoded input is too large.");
            return Engine.CreateArray(Encoding.UTF8.GetBytes(value).Select(b => ScriptValue.FromNumber(b)).ToArray());
        }
        internal ScriptValue DecodeBlob(IReadOnlyList<byte> bytes)
        {
            var text = Encoding.UTF8.GetString(bytes.ToArray());
            return ScriptValue.FromString(text.StartsWith('\uFEFF') ? text[1..] : text);
        }
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
            if(isView(v)) {
              const typed=typedBrand(v)!==undefined;
              return new U8(apply(typed?typedBuffer:dataBuffer,v,[]),apply(typed?typedOffset:dataOffset,v,[]),apply(typed?typedLength:dataLength,v,[]));
            }
            try {apply(arrayLength,v,[]);return new U8(v);}catch(_){throw new TypeError('Expected a BufferSource.');}
          }
          function byteList(view){const length=apply(typedLength,view,[]),bytes=[];for(let i=0;i<length;i++)bytes[i]=view[i];return bytes;}
          URL.createObjectURL=function(blob){const state=blobStates.get(blob);return host.blobUrl(state?host.blobText(byteList(state.bytes)):'');}; URL.revokeObjectURL=function(url){host.revokeBlobUrl(S(url));};
          const blobStates=new WeakMap(); function blobPart(p){if(typeof p==='string')return new U8(host.encode(p));if(blobStates.has(p))return blobStates.get(p).bytes.slice();if(p instanceof AB)return new U8(p);if(isView(p))return new U8(p.buffer,p.byteOffset,p.byteLength);return new U8(host.encode(S(p)));}
          const BlobCtor=function(parts,options){const chunks=Afrom(parts||[]).map(blobPart);let n=0;for(const c of chunks)n+=c.byteLength;if(n>16*1024*1024)throw new DOMException('The Blob is too large.','QuotaExceededError');const bytes=new U8(n);let at=0;for(const c of chunks){bytes.set(c,at);at+=c.byteLength;}let type=options&&options.type?S(options.type).toLowerCase():'';if(/[^\x20-\x7E]/.test(type))type='';blobStates.set(this,{bytes,type});Object.defineProperty(this,'size',{get:()=>bytes.byteLength});Object.defineProperty(this,'type',{get:()=>type});}; BlobCtor.prototype.text=function(){return P.resolve(host.blobText(byteList(blobStates.get(this).bytes)));}; BlobCtor.prototype.arrayBuffer=function(){return P.resolve(blobStates.get(this).bytes.slice().buffer);}; BlobCtor.prototype.slice=function(start,end,type){const state=blobStates.get(this);let a=start===undefined?0:Number(start)||0,b=end===undefined?state.bytes.length:Number(end)||0;if(a<0)a=Math.max(state.bytes.length+a,0);else a=Math.min(a,state.bytes.length);if(b<0)b=Math.max(state.bytes.length+b,0);else b=Math.min(b,state.bytes.length);if(b<a)b=a;return new BlobCtor([state.bytes.slice(a,b)],{type:type===undefined?'':S(type).toLowerCase()});}; globalThis.Blob=BlobCtor;
          globalThis.FormData=function(form){if(!new.target||form!==undefined)throw new TypeError('Expected an HTMLFormElement.');this._entries=[];};
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
