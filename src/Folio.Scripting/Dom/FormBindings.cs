using System.Runtime.CompilerServices;
using Folio.Dom;

namespace Folio.Scripting.Dom;

/// <summary>A live list of elements a script sees as a collection, recomputed on each read (<c>form.elements</c>).</summary>
internal sealed class LiveElementList(Func<IReadOnlyList<ElementNode>> compute)
{
    internal IReadOnlyList<ElementNode> Items => compute();
}

/// <summary>The object behind <c>control.validity</c>.</summary>
internal sealed class ValidityTarget(FormControls forms, ElementNode element)
{
    internal ValidityFlags Flags => forms.Validity(element);

    internal bool Valid => !forms.WillValidate(element) || Flags == ValidityFlags.None;
}

/// <summary>
/// The scripting side of forms and their controls (#466, #467): <c>HTMLFormElement</c>, <c>ValidityState</c>, the members all the
/// controls share, the selection API and <c>setRangeText</c> of text controls, and <c>click()</c>. The behaviour is
/// <see cref="FormControls"/>'s; these are the WebIDL conversions and exceptions around it.
/// </summary>
internal static class FormBindings
{
    private static readonly ConditionalWeakTable<ElementNode, ValidityTarget> Validities = new();

    private static ElementNode El(object target) => (ElementNode)target;

    private static bool IsHtml(ElementNode e, string name) => e.Name.Namespace == Namespaces.Html && e.LocalName == name;

    private static string Reflect(ElementNode e, string attribute, string fallback = "") => e.GetAttribute(attribute) ?? fallback;

    internal static HostObjectTemplate CreateValidityStateTemplate()
    {
        var t = new HostObjectTemplate("ValidityState");
        void Flag(string name, ValidityFlags flag) => t.DefineProperty(name, (target, _) => ScriptValue.FromBoolean(((ValidityTarget)target).Flags.HasFlag(flag)));
        Flag("valueMissing", ValidityFlags.ValueMissing);
        Flag("typeMismatch", ValidityFlags.TypeMismatch);
        Flag("patternMismatch", ValidityFlags.PatternMismatch);
        Flag("tooLong", ValidityFlags.TooLong);
        Flag("tooShort", ValidityFlags.TooShort);
        Flag("rangeUnderflow", ValidityFlags.RangeUnderflow);
        Flag("rangeOverflow", ValidityFlags.RangeOverflow);
        Flag("stepMismatch", ValidityFlags.StepMismatch);
        Flag("badInput", ValidityFlags.BadInput);
        Flag("customError", ValidityFlags.CustomError);
        t.DefineProperty("valid", (target, _) => ScriptValue.FromBoolean(((ValidityTarget)target).Valid));
        return t;
    }

    internal static HostObjectTemplate CreateControlsCollectionTemplate(DomContext context)
    {
        var t = new HostObjectTemplate("HTMLFormControlsCollection");
        ElementNode? Named(LiveElementList list, string name) => list.Items.FirstOrDefault(e => e.GetAttribute("id") == name || e.GetAttribute("name") == name);
        t.DefineProperty("length", (target, _) => ScriptValue.FromNumber(((LiveElementList)target).Items.Count));
        t.DefineMethod("item", (target, _, args) =>
        {
            var index = args.Length == 0 ? -1 : WebIdlConversions.ToInt32(args[0]);
            var items = ((LiveElementList)target).Items;
            return index >= 0 && index < items.Count ? context.Wrap(items[index]) : ScriptValue.Null;
        });
        t.DefineMethod("namedItem", (target, _, args) => args.Length == 0 ? ScriptValue.Null : context.Wrap(Named((LiveElementList)target, WebIdlConversions.ToDOMString(args[0]))));
        t.DefineIndexedProperty((target, _, index) =>
        {
            var items = ((LiveElementList)target).Items;
            return index >= 0 && index < items.Count ? context.Wrap(items[index]) : ScriptValue.Undefined;
        });
        t.DefineNamedProperty(
            getter: (target, _, name) => Named((LiveElementList)target, name) is { } match ? context.Wrap(match) : ScriptValue.Undefined,
            enumerator: (target, _) => ((LiveElementList)target).Items.Select(e => e.GetAttribute("id") ?? e.GetAttribute("name")).Where(n => !string.IsNullOrEmpty(n)).Distinct().Cast<string>().ToList());
        return t;
    }

    internal static HostObjectTemplate CreateFormTemplate(IHostObjectTemplate htmlElementProto, DomContext context, HostObjectTemplate collection)
    {
        var t = new HostObjectTemplate("HTMLFormElement", htmlElementProto);
        FormControls Forms() => context.Forms;
        t.DefineProperty("elements", (target, _) => context.WrapperCache.GetOrCreate(context.Engine, collection, ElementListFor(El(target), () => Forms().Elements(El(target)))));
        t.DefineProperty("length", (target, _) => ScriptValue.FromNumber(Forms().Elements(El(target)).Count));
        t.DefineIndexedProperty((target, _, index) =>
        {
            var items = Forms().Elements(El(target));
            return index >= 0 && index < items.Count ? context.Wrap(items[index]) : ScriptValue.Undefined;
        });
        t.DefineMethod("submit", (target, _, _) =>
        {
            Forms().SubmitNow(El(target), null);
            return ScriptValue.Undefined;
        });
        t.DefineMethod("requestSubmit", (target, _, args) =>
        {
            ElementNode? submitter = null;
            if (args.Length > 0 && !(args[0].IsUndefined))
            {
                if (!context.TryUnwrapElement(args[0], out submitter) || !FormControls.IsSubmitter(submitter))
                    throw WebIdlException.TypeError("The specified element is not a submit button.");
                if (Forms().FormOwner(submitter) != El(target))
                    throw WebIdlException.DomException("NotFoundError", "The specified element is not owned by this form element.");
            }
            Forms().RequestSubmit(El(target), submitter);
            return ScriptValue.Undefined;
        });
        t.DefineMethod("reset", (target, _, _) =>
        {
            Forms().RequestReset(El(target));
            return ScriptValue.Undefined;
        });
        t.DefineMethod("checkValidity", (target, _, _) => ScriptValue.FromBoolean(Forms().CheckForm(El(target), report: false)));
        t.DefineMethod("reportValidity", (target, _, _) => ScriptValue.FromBoolean(Forms().CheckForm(El(target), report: true)));
        t.DefineProperty("noValidate", (target, _) => ScriptValue.FromBoolean(El(target).GetAttribute("novalidate") is not null),
            (target, _, value) => SetBoolean(El(target), "novalidate", value));
        foreach (var name in new[] { "action", "target", "name", "acceptCharset", "autocomplete" })
        {
            var attribute = name == "acceptCharset" ? "accept-charset" : name;
            t.DefineProperty(name, (target, _) => ScriptValue.FromString(Reflect(El(target), attribute)),
                (target, _, value) => El(target).SetAttribute(attribute, WebIdlConversions.ToDOMString(value)));
        }
        t.DefineProperty("method", (target, _) =>
        {
            var value = Reflect(El(target), "method").ToLowerInvariant();
            return ScriptValue.FromString(value is "post" or "dialog" ? value : "get");
        }, (target, _, value) => El(target).SetAttribute("method", WebIdlConversions.ToDOMString(value)));
        t.DefineProperty("enctype", (target, _) =>
        {
            var value = Reflect(El(target), "enctype").ToLowerInvariant();
            return ScriptValue.FromString(value is "multipart/form-data" or "text/plain" ? value : "application/x-www-form-urlencoded");
        }, (target, _, value) => El(target).SetAttribute("enctype", WebIdlConversions.ToDOMString(value)));
        return t;
    }

    private static readonly ConditionalWeakTable<ElementNode, LiveElementList> Lists = new();

    private static LiveElementList ElementListFor(ElementNode form, Func<IReadOnlyList<ElementNode>> compute) => Lists.GetValue(form, _ => new LiveElementList(compute));

    private static void SetBoolean(ElementNode e, string attribute, ScriptValue value)
    {
        if (WebIdlConversions.ToBoolean(value))
            e.SetAttribute(attribute, "");
        else
            e.RemoveAttribute(attribute);
    }

    /// <summary>What every form-associated control (<c>input</c>, <c>select</c>, <c>textarea</c>, <c>button</c>) has in common.</summary>
    internal static void DefineControlMembers(HostObjectTemplate t, DomContext context, HostObjectTemplate validityState)
    {
        FormControls Forms() => context.Forms;
        t.DefineProperty("form", (target, _) => context.Wrap(Forms().FormOwner(El(target))));
        t.DefineProperty("willValidate", (target, _) => ScriptValue.FromBoolean(Forms().WillValidate(El(target))));
        t.DefineProperty("validity", (target, _) =>
        {
            var element = El(target);
            var validity = Validities.GetValue(element, e => new ValidityTarget(Forms(), e));
            return context.WrapperCache.GetOrCreate(context.Engine, validityState, validity);
        });
        t.DefineProperty("validationMessage", (target, _) => ScriptValue.FromString(Forms().ValidationMessage(El(target))));
        t.DefineMethod("setCustomValidity", (target, _, args) =>
        {
            if (args.Length == 0)
                throw WebIdlException.TypeError("Failed to execute 'setCustomValidity': 1 argument required, but only 0 present.");
            Forms().SetCustomValidity(El(target), WebIdlConversions.ToDOMString(args[0]));
            return ScriptValue.Undefined;
        });
        t.DefineMethod("checkValidity", (target, _, _) => ScriptValue.FromBoolean(Forms().CheckValidity(El(target), fire: true)));
        t.DefineMethod("reportValidity", (target, _, _) =>
        {
            var element = El(target);
            var valid = Forms().CheckValidity(element, fire: true);
            if (!valid)
                context.Focus(element);
            return ScriptValue.FromBoolean(valid);
        });
    }

    /// <summary>The value and the selection API of <c>input</c> and <c>textarea</c>.</summary>
    internal static void DefineTextControlMembers(HostObjectTemplate t, DomContext context)
    {
        FormControls Forms() => context.Forms;
        bool Selectable(ElementNode e) => FormControls.SupportsSelection(e);
        void Require(ElementNode e)
        {
            if (!Selectable(e))
                throw WebIdlException.DomException("InvalidStateError", "The input element's type does not support selection.");
        }
        int Clamp(double value, ElementNode e) => (int)Math.Clamp(double.IsNaN(value) ? 0 : value, 0, Forms().Value(e).Length);

        t.DefineProperty("value",
            (target, _) =>
            {
                var element = El(target);
                if (Forms().TextState(element) is not null)
                    return ScriptValue.FromString(Forms().Value(element));
                var fallback = IsHtml(element, "input") && (element.GetAttribute("type") ?? "").ToLowerInvariant() is "checkbox" or "radio" ? "on" : "";
                return ScriptValue.FromString(element.Value ?? element.GetAttribute("value") ?? fallback);
            },
            (target, _, value) =>
            {
                var element = El(target);
                var text = WebIdlConversions.ToDOMString(value);
                if (IsHtml(element, "input") && (element.GetAttribute("type") ?? "").Equals("file", StringComparison.OrdinalIgnoreCase) && text.Length > 0)
                    throw WebIdlException.DomException("InvalidStateError", "This input element accepts a filename, which may only be programmatically set to the empty string.");
                if (Forms().TextState(element) is not null)
                    Forms().SetValue(element, text);
                else
                    element.Value = text;
            });
        t.DefineProperty("selectionStart",
            (target, _) => Selectable(El(target)) ? ScriptValue.FromNumber(Forms().SelectionStart(El(target))) : ScriptValue.Null,
            (target, _, value) =>
            {
                Require(El(target));
                Forms().SetSelectionStart(El(target), Clamp(WebIdlConversions.ToDouble(value), El(target)));
            });
        t.DefineProperty("selectionEnd",
            (target, _) => Selectable(El(target)) ? ScriptValue.FromNumber(Forms().SelectionEnd(El(target))) : ScriptValue.Null,
            (target, _, value) =>
            {
                Require(El(target));
                Forms().SetSelectionEnd(El(target), Clamp(WebIdlConversions.ToDouble(value), El(target)));
            });
        t.DefineProperty("selectionDirection",
            (target, _) => Selectable(El(target)) ? ScriptValue.FromString(Forms().SelectionDirection(El(target))) : ScriptValue.Null,
            (target, _, value) =>
            {
                Require(El(target));
                var element = El(target);
                Forms().SetSelectionRange(element, Forms().SelectionStart(element), Forms().SelectionEnd(element), WebIdlConversions.ToDOMString(value));
            });
        t.DefineMethod("setSelectionRange", (target, _, args) =>
        {
            var element = El(target);
            if (args.Length < 2)
                throw WebIdlException.TypeError($"Failed to execute 'setSelectionRange': 2 arguments required, but only {args.Length} present.");
            Require(element);
            var start = Clamp(WebIdlConversions.ToDouble(args[0]), element);
            var end = Clamp(WebIdlConversions.ToDouble(args[1]), element);
            Forms().SetSelectionRange(element, Math.Min(start, end), end, args.Length > 2 && !args[2].IsUndefined ? WebIdlConversions.ToDOMString(args[2]) : null);
            return ScriptValue.Undefined;
        });
        t.DefineMethod("select", (target, _, _) =>
        {
            var element = El(target);
            if (!Selectable(element))
                return ScriptValue.Undefined;
            Forms().SelectAll(element);
            Forms().Fire(element, "select", bubbles: true, cancelable: false);
            return ScriptValue.Undefined;
        });
        t.DefineMethod("setRangeText", (target, _, args) =>
        {
            var element = El(target);
            if (args.Length == 0)
                throw WebIdlException.TypeError("Failed to execute 'setRangeText': 1 argument required, but only 0 present.");
            Require(element);
            var replacement = WebIdlConversions.ToDOMString(args[0]);
            int start, end;
            var length = Forms().Value(element).Length;
            if (args.Length >= 3)
            {
                start = (int)Math.Min(WebIdlConversions.ToUint32(args[1]), (uint)int.MaxValue);
                end = (int)Math.Min(WebIdlConversions.ToUint32(args[2]), (uint)int.MaxValue);
                if (start > end)
                    throw WebIdlException.DomException("IndexSizeError", "The start index is greater than the end index.");
            }
            else
                (start, end) = (Forms().SelectionStart(element), Forms().SelectionEnd(element));
            var mode = args.Length >= 4 ? WebIdlConversions.ToDOMString(args[3]) : "preserve";
            if (mode is not ("select" or "start" or "end" or "preserve"))
                throw WebIdlException.TypeError($"The provided value '{mode}' is not a valid enum value of type SelectionMode.");
            Forms().SetRangeText(element, replacement, Math.Min(start, length), Math.Min(end, length), mode);
            return ScriptValue.Undefined;
        });
    }

    /// <summary><c>HTMLElement.click()</c>.</summary>
    internal static void DefineClick(HostObjectTemplate t, DomContext context) =>
        t.DefineMethod("click", (target, _, _) =>
        {
            context.Forms.Click(El(target));
            return ScriptValue.Undefined;
        });

    /// <summary><c>FormData</c>: the entry list of a form, or built by script (https://xhr.spec.whatwg.org/#interface-formdata).</summary>
    internal static void InstallFormData(IScriptEngine engine, DomContext context)
    {
        var host = new HostObjectTemplate("FormDataHost")
            .DefineMethod("entries", (_, _, args) =>
            {
                if (args.Length == 0 || !context.TryUnwrapElement(args[0], out var form) || !IsHtml(form, "form"))
                    throw WebIdlException.TypeError("Failed to construct 'FormData': parameter 1 is not of type 'HTMLFormElement'.");
                ElementNode? submitter = null;
                if (args.Length > 1 && !args[1].IsNullOrUndefined)
                {
                    if (!context.TryUnwrapElement(args[1], out submitter) || !FormControls.IsSubmitter(submitter))
                        throw WebIdlException.TypeError("Failed to construct 'FormData': parameter 2 is not a submit button.");
                    if (context.Forms.FormOwner(submitter) != form)
                        throw WebIdlException.DomException("NotFoundError", "The specified submitter is not owned by this form element.");
                }
                return ScriptValue.FromString(System.Text.Json.JsonSerializer.Serialize(context.Forms.EntryList(form, submitter).Select(p => new[] { p.Key, p.Value })));
            });
        engine.SetGlobalProperty("__folio_form_host", engine.CreateHostObject(host, new object()));
        engine.EvaluateHostScript(FormDataSource, "folio:formdata");
        engine.Evaluate("delete globalThis.__folio_form_host;", "folio:formdata-cleanup");
    }

    private const string FormDataSource = """
        (function (host) {
          'use strict';
          const S = String, defineProperty = Object.defineProperty;
          const states = new WeakMap();
          function stateOf(self) { const s = states.get(self); if (!s) throw new TypeError('Illegal invocation'); return s; }
          function isBlob(v) { return typeof Blob === 'function' && v instanceof Blob; }
          function need(count, given, method) {
            if (given < count) throw new TypeError("Failed to execute '" + method + "' on 'FormData': " + count + " argument" + (count === 1 ? '' : 's') + ' required, but only ' + given + ' present.');
          }
          function FormData(form, submitter) {
            if (!new.target) throw new TypeError("Failed to construct 'FormData': Please use the 'new' operator, this DOM object constructor cannot be called as a function.");
            const state = { entries: [] };
            states.set(this, state);
            if (form !== undefined) {
              state.entries = JSON.parse(host.entries(form, submitter));
              if (typeof FormDataEvent === 'function')
                form.dispatchEvent(new FormDataEvent('formdata', { bubbles: true, cancelable: false, formData: this }));
            }
          }
          function method(name, length, fn) {
            defineProperty(FormData.prototype, name, { value: fn, writable: true, enumerable: true, configurable: true });
            defineProperty(fn, 'length', { value: length, configurable: true });
          }
          function entryValue(v) { return isBlob(v) ? v : S(v); }
          method('append', 2, function append(name, value) { const s = stateOf(this); need(2, arguments.length, 'append'); s.entries.push([S(name), entryValue(value)]); });
          method('delete', 1, function (name) { const s = stateOf(this); need(1, arguments.length, 'delete'); name = S(name); s.entries = s.entries.filter(function (e) { return e[0] !== name; }); });
          method('get', 1, function get(name) { const s = stateOf(this); need(1, arguments.length, 'get'); name = S(name); const e = s.entries.find(function (x) { return x[0] === name; }); return e ? e[1] : null; });
          method('getAll', 1, function getAll(name) { const s = stateOf(this); need(1, arguments.length, 'getAll'); name = S(name); return s.entries.filter(function (x) { return x[0] === name; }).map(function (x) { return x[1]; }); });
          method('has', 1, function has(name) { const s = stateOf(this); need(1, arguments.length, 'has'); name = S(name); return s.entries.some(function (x) { return x[0] === name; }); });
          method('set', 2, function set(name, value) {
            const s = stateOf(this); need(2, arguments.length, 'set');
            name = S(name); value = entryValue(value);
            let found = false;
            const next = [];
            for (const e of s.entries) {
              if (e[0] !== name) next.push(e);
              else if (!found) { found = true; next.push([name, value]); }
            }
            if (!found) next.push([name, value]);
            s.entries = next;
          });
          const IteratorProto = {};
          function iterator(self, kind) {
            const state = stateOf(self);
            let at = 0;
            const it = Object.create(IteratorProto);
            defineProperty(it, '_next', { value: function () {
              if (at >= state.entries.length) { at = Infinity; return { value: undefined, done: true }; }
              const e = state.entries[at++];
              return { value: kind === 0 ? e[0] : kind === 1 ? e[1] : [e[0], e[1]], done: false };
            } });
            return it;
          }
          defineProperty(IteratorProto, 'next', { value: function next() { return this._next(); }, writable: true, configurable: true });
          defineProperty(IteratorProto, Symbol.iterator, { value: function () { return this; }, writable: true, configurable: true });
          defineProperty(IteratorProto, Symbol.toStringTag, { value: 'FormData Iterator', configurable: true });
          method('keys', 0, function keys() { return iterator(this, 0); });
          method('values', 0, function values() { return iterator(this, 1); });
          method('entries', 0, function entries() { return iterator(this, 2); });
          method('forEach', 1, function forEach(callback, receiver) {
            const s = stateOf(this); need(1, arguments.length, 'forEach');
            if (typeof callback !== 'function') throw new TypeError("Failed to execute 'forEach' on 'FormData': parameter 1 is not of type 'Function'.");
            for (let i = 0; i < s.entries.length; i++) callback.call(receiver, s.entries[i][1], s.entries[i][0], this);
          });
          defineProperty(FormData.prototype, Symbol.iterator, { value: FormData.prototype.entries, writable: true, configurable: true });
          defineProperty(FormData.prototype, Symbol.toStringTag, { value: 'FormData', configurable: true });
          defineProperty(globalThis, 'FormData', { value: FormData, writable: true, configurable: true });
        })(__folio_form_host);
        """;
}
