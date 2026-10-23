using System.Text.Json;

namespace Folio.Scripting.Dom;

/// <summary>
/// The URL and URLSearchParams classes (https://url.spec.whatwg.org/#api). Parsing, serializing and the setters are
/// <see cref="WhatwgUrl"/>; the classes here only hold a record each and keep a URL and its search parameters in step.
/// </summary>
internal static class UrlBindings
{
    internal static void Install(IScriptEngine engine)
    {
        var record = new HostObjectTemplate("UrlRecord")
            .DefineMethod("get", (t, _, a) => ScriptValue.FromString(Get((WhatwgUrl)t, Text(a, 0))))
            .DefineMethod("set", (t, _, a) =>
            {
                Set((WhatwgUrl)t, Text(a, 0), Text(a, 1));
                return ScriptValue.Undefined;
            })
            .DefineMethod("setQuery", (t, _, a) =>
            {
                var url = (WhatwgUrl)t;
                url.Query = a.Length > 0 ? a[0].AsString() : null;
                url.StripTrailingSpacesFromOpaquePath();
                return ScriptValue.Undefined;
            });
        var host = new HostObjectTemplate("UrlHost")
            .DefineMethod("parse", (_, _, a) =>
            {
                WhatwgUrl? baseUrl = null;
                if (a.Length > 1 && a[1].AsString() is { } baseText && (baseUrl = WhatwgUrl.Parse(baseText)) is null)
                    return ScriptValue.Null;
                return WhatwgUrl.Parse(Text(a, 0), baseUrl) is { } url ? engine.CreateHostObject(record, url) : ScriptValue.Null;
            })
            .DefineMethod("formParse", (_, _, a) =>
                ScriptValue.FromString(JsonSerializer.Serialize(WhatwgUrl.ParseForm(Text(a, 0)).Select(p => new[] { p.Name, p.Value }))))
            .DefineMethod("formSerialize", (_, _, a) =>
            {
                var pairs = JsonSerializer.Deserialize<string[][]>(Text(a, 0)) ?? [];
                return ScriptValue.FromString(WhatwgUrl.SerializeForm(pairs.Select(p => (p[0], p[1]))));
            });
        engine.SetGlobalProperty("__folio_url_host", engine.CreateHostObject(host, new object()));
        engine.EvaluateHostScript(Source, "folio:url");
        engine.Evaluate("delete globalThis.__folio_url_host;", "folio:url-cleanup");
    }

    private static string Text(ReadOnlySpan<ScriptValue> args, int index) => index < args.Length ? args[index].AsString() ?? "" : "";

    private static string? Get(WhatwgUrl url, string name) => name switch
    {
        "href" => url.Serialize(),
        "origin" => url.Origin,
        "protocol" => url.Scheme + ":",
        "username" => url.Username,
        "password" => url.Password,
        "host" => url.HostString,
        "hostname" => url.Host ?? "",
        "port" => url.Port?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "",
        "pathname" => url.PathString,
        "search" => string.IsNullOrEmpty(url.Query) ? "" : "?" + url.Query,
        "hash" => string.IsNullOrEmpty(url.Fragment) ? "" : "#" + url.Fragment,
        "query" => url.Query,
        _ => null,
    };

    private static void Set(WhatwgUrl url, string name, string value)
    {
        switch (name)
        {
            case "protocol": url.SetProtocol(value); break;
            case "username": url.SetUsername(value); break;
            case "password": url.SetPassword(value); break;
            case "host": url.SetHost(value); break;
            case "hostname": url.SetHostname(value); break;
            case "port": url.SetPort(value); break;
            case "pathname": url.SetPathname(value); break;
            case "search": url.SetSearch(value); break;
            case "hash": url.SetHash(value); break;
        }
    }

    private const string Source = """
        (function (host) {
          'use strict';
          var S = String, Okeys = Object.keys, defineProperty = Object.defineProperty;
          var urls = new WeakMap();
          var lists = new WeakMap();

          function illegal() { return new TypeError('Illegal invocation'); }
          function newRequired(name) { return new TypeError("Failed to construct '" + name + "': Please use the 'new' operator, this DOM object constructor cannot be called as a function."); }
          function listOf(self) { var state = lists.get(self); if (!state) throw illegal(); return state; }
          function urlOf(self) { var state = urls.get(self); if (!state) throw illegal(); return state; }
          function need(count, given, name, method) {
            if (given < count) throw new TypeError("Failed to execute '" + method + "' on '" + name + "': " + count + " argument" + (count === 1 ? '' : 's') + ' required, but only ' + given + ' present.');
          }

          function URLSearchParams(init) {
            if (!new.target) throw newRequired('URLSearchParams');
            var state = { list: [], url: null };
            lists.set(this, state);
            if (init === undefined) return;
            if ((typeof init === 'object' && init !== null) || typeof init === 'function') {
              var iterator = init[Symbol.iterator];
              if (iterator !== undefined && iterator !== null) {
                for (var pair of init) {
                  if (pair === null || (typeof pair !== 'object' && typeof pair !== 'function') || typeof pair[Symbol.iterator] !== 'function') throw new TypeError("Failed to construct 'URLSearchParams': The provided value cannot be converted to a sequence.");
                  var items = Array.from(pair);
                  if (items.length !== 2) throw new TypeError("Failed to construct 'URLSearchParams': Sequence initializer must only contain pair elements.");
                  state.list.push([S(items[0]), S(items[1])]);
                }
              } else {
                var keys = Okeys(init);
                for (var i = 0; i < keys.length; i++) state.list.push([S(keys[i]), S(init[keys[i]])]);
              }
            } else {
              var text = S(init);
              state.list = JSON.parse(host.formParse(text.charAt(0) === '?' ? text.slice(1) : text));
            }
          }
          function update(state) {
            if (!state.url) return;
            var query = host.formSerialize(JSON.stringify(state.list));
            state.url.rec.setQuery(query === '' ? null : query);
          }
          var proto = URLSearchParams.prototype;
          function method(name, length, fn) {
            defineProperty(proto, name, { value: fn, writable: true, enumerable: true, configurable: true });
            defineProperty(fn, 'length', { value: length, configurable: true });
          }
          method('append', 2, function append(name, value) {
            var state = listOf(this); need(2, arguments.length, 'URLSearchParams', 'append');
            state.list.push([S(name), S(value)]); update(state);
          });
          method('delete', 1, function (name, value) {
            var state = listOf(this); need(1, arguments.length, 'URLSearchParams', 'delete');
            name = S(name);
            if (value === undefined) state.list = state.list.filter(function (p) { return p[0] !== name; });
            else { value = S(value); state.list = state.list.filter(function (p) { return !(p[0] === name && p[1] === value); }); }
            update(state);
          });
          method('get', 1, function get(name) {
            var state = listOf(this); need(1, arguments.length, 'URLSearchParams', 'get');
            name = S(name);
            for (var i = 0; i < state.list.length; i++) if (state.list[i][0] === name) return state.list[i][1];
            return null;
          });
          method('getAll', 1, function getAll(name) {
            var state = listOf(this); need(1, arguments.length, 'URLSearchParams', 'getAll');
            name = S(name);
            return state.list.filter(function (p) { return p[0] === name; }).map(function (p) { return p[1]; });
          });
          method('has', 1, function has(name, value) {
            var state = listOf(this); need(1, arguments.length, 'URLSearchParams', 'has');
            name = S(name);
            if (value !== undefined) value = S(value);
            return state.list.some(function (p) { return p[0] === name && (value === undefined || p[1] === value); });
          });
          method('set', 2, function set(name, value) {
            var state = listOf(this); need(2, arguments.length, 'URLSearchParams', 'set');
            name = S(name); value = S(value);
            var found = false, next = [];
            for (var i = 0; i < state.list.length; i++) {
              var p = state.list[i];
              if (p[0] !== name) next.push(p);
              else if (!found) { found = true; next.push([name, value]); }
            }
            if (!found) next.push([name, value]);
            state.list = next; update(state);
          });
          method('sort', 0, function sort() {
            var state = listOf(this);
            // Stable, by UTF-16 code units of the names.
            state.list = state.list.map(function (p, i) { return [p, i]; }).sort(function (a, b) {
              return a[0][0] < b[0][0] ? -1 : a[0][0] > b[0][0] ? 1 : a[1] - b[1];
            }).map(function (x) { return x[0]; });
            update(state);
          });
          method('toString', 0, function toString() { return host.formSerialize(JSON.stringify(listOf(this).list)); });
          method('forEach', 1, function forEach(callback, receiver) {
            var state = listOf(this); need(1, arguments.length, 'URLSearchParams', 'forEach');
            if (typeof callback !== 'function') throw new TypeError("Failed to execute 'forEach' on 'URLSearchParams': parameter 1 is not of type 'Function'.");
            for (var i = 0; i < state.list.length; i++) callback.call(receiver, state.list[i][1], state.list[i][0], this);
          });
          var IteratorProto = {};
          defineProperty(IteratorProto, 'next', { value: function next() {
            var it = this._it, list = it.state.list;
            if (it.at >= list.length) { it.at = Infinity; return { value: undefined, done: true }; }
            var pair = list[it.at++];
            return { value: it.kind === 0 ? pair[0] : it.kind === 1 ? pair[1] : [pair[0], pair[1]], done: false };
          }, writable: true, configurable: true });
          defineProperty(IteratorProto, Symbol.iterator, { value: function () { return this; }, writable: true, configurable: true });
          defineProperty(IteratorProto, Symbol.toStringTag, { value: 'URLSearchParams Iterator', configurable: true });
          function iterate(self, kind) {
            var iterator = Object.create(IteratorProto);
            defineProperty(iterator, '_it', { value: { state: listOf(self), at: 0, kind: kind } });
            return iterator;
          }
          method('keys', 0, function keys() { return iterate(this, 0); });
          method('values', 0, function values() { return iterate(this, 1); });
          method('entries', 0, function entries() { return iterate(this, 2); });
          defineProperty(proto, Symbol.iterator, { value: proto.entries, writable: true, configurable: true });
          defineProperty(proto, 'size', { get: function () { return listOf(this).list.length; }, enumerable: true, configurable: true });
          defineProperty(proto, Symbol.toStringTag, { value: 'URLSearchParams', configurable: true });

          function URL(url, base) {
            if (!new.target) throw newRequired('URL');
            need(1, arguments.length, 'URL', 'construct');
            var rec = host.parse(S(url), base === undefined ? null : S(base));
            if (rec === null) throw new TypeError("Failed to construct 'URL': Invalid URL");
            attach(this, rec);
          }
          function attach(self, rec) {
            var state = { rec: rec, params: null };
            urls.set(self, state);
            var params = new URLSearchParams(rec.get('query') || '');
            lists.get(params).url = state;
            state.params = params;
          }
          function parseOrNull(url, base) {
            return host.parse(S(url), base === undefined ? null : S(base));
          }
          function accessor(name, set) {
            defineProperty(URL.prototype, name, {
              get: function () { return urlOf(this).rec.get(name); },
              set: set === false ? undefined : function (value) {
                var state = urlOf(this);
                state.rec.set(name, S(value));
                if (name === 'search') lists.get(state.params).list = JSON.parse(host.formParse(state.rec.get('query') || ''));
              },
              enumerable: true, configurable: true
            });
          }
          defineProperty(URL.prototype, 'href', {
            get: function () { return urlOf(this).rec.get('href'); },
            set: function (value) {
              var state = urlOf(this), rec = host.parse(S(value), null);
              if (rec === null) throw new TypeError("Failed to set the 'href' property on 'URL': Invalid URL");
              state.rec = rec;
              lists.get(state.params).list = JSON.parse(host.formParse(rec.get('query') || ''));
            },
            enumerable: true, configurable: true
          });
          accessor('origin', false);
          ['protocol', 'username', 'password', 'host', 'hostname', 'port', 'pathname', 'search'].forEach(function (n) { accessor(n); });
          defineProperty(URL.prototype, 'searchParams', { get: function () { return urlOf(this).params; }, enumerable: true, configurable: true });
          accessor('hash');
          defineProperty(URL.prototype, 'toString', { value: function toString() { return urlOf(this).rec.get('href'); }, writable: true, enumerable: true, configurable: true });
          defineProperty(URL.prototype, 'toJSON', { value: function toJSON() { return urlOf(this).rec.get('href'); }, writable: true, enumerable: true, configurable: true });
          defineProperty(URL.prototype, Symbol.toStringTag, { value: 'URL', configurable: true });
          defineProperty(URL, 'canParse', { value: function canParse(url, base) {
            need(1, arguments.length, 'URL', 'canParse');
            return parseOrNull(url, base) !== null;
          }, writable: true, enumerable: true, configurable: true });
          defineProperty(URL, 'parse', { value: function parse(url, base) {
            need(1, arguments.length, 'URL', 'parse');
            var rec = parseOrNull(url, base);
            if (rec === null) return null;
            var result = Object.create(URL.prototype);
            attach(result, rec);
            return result;
          }, writable: true, enumerable: true, configurable: true });
          defineProperty(globalThis, 'URL', { value: URL, writable: true, configurable: true });
          defineProperty(globalThis, 'URLSearchParams', { value: URLSearchParams, writable: true, configurable: true });
        })(__folio_url_host);
        """;
}
