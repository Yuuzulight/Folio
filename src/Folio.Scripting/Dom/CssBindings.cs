using Folio.Css;

namespace Folio.Scripting.Dom;

/// <summary>
/// The <c>CSS</c> namespace (issue #500): <c>CSS.escape</c> (https://drafts.csswg.org/cssom/#serialize-an-identifier) and
/// <c>CSS.supports</c> (https://drafts.csswg.org/css-conditional-3/#the-css-namespace), the latter answering with the same
/// evaluator an <c>@supports</c> rule uses, so the two can never disagree.
/// </summary>
internal static class CssBindings
{
    internal static void Install(IScriptEngine engine)
    {
        var host = new HostObjectTemplate("FolioCssHost")
            .DefineMethod("condition", (_, _, args) => ScriptValue.FromBoolean(SupportsCondition(args.Length > 0 ? args[0].AsString() ?? "" : "")))
            .DefineMethod("declaration", (_, _, args) =>
                ScriptValue.FromBoolean(Conditions.SupportsDeclaration(args.Length > 0 ? args[0].AsString() ?? "" : "", args.Length > 1 ? args[1].AsString() ?? "" : "")));
        engine.SetGlobalProperty("__folio_css_host", engine.CreateHostObject(host, new object()));
        engine.EvaluateHostScript(Source, "folio:css");
        engine.Evaluate("delete globalThis.__folio_css_host");
    }

    // One argument: a <supports-condition>, or, when that doesn't parse, the text in parentheses ("display: grid").
    private static bool SupportsCondition(string text)
    {
        var (source, values) = CssParser.ParseComponentValues(text);
        if (Conditions.SupportsOrInvalid(source, values) is { } answer)
            return answer;
        var (wrappedSource, wrapped) = CssParser.ParseComponentValues("(" + text + ")");
        return Conditions.SupportsOrInvalid(wrappedSource, wrapped) == true;
    }

    private const string Source = """
        (function(host) {
            'use strict';
            function escape(value) {
                if (arguments.length === 0) throw new TypeError("Failed to execute 'escape' on 'CSS': 1 argument required, but only 0 present.");
                const string = String(value);
                const length = string.length;
                const first = string.charCodeAt(0);
                let result = '';
                for (let index = 0; index < length; index++) {
                    const unit = string.charCodeAt(index);
                    if (unit === 0) { result += '�'; continue; }
                    if ((unit >= 0x01 && unit <= 0x1F) || unit === 0x7F
                        || (index === 0 && unit >= 0x30 && unit <= 0x39)
                        || (index === 1 && unit >= 0x30 && unit <= 0x39 && first === 0x2D)) {
                        result += '\\' + unit.toString(16) + ' ';
                        continue;
                    }
                    if (index === 0 && length === 1 && unit === 0x2D) { result += '\\' + string.charAt(index); continue; }
                    if (unit >= 0x80 || unit === 0x2D || unit === 0x5F || (unit >= 0x30 && unit <= 0x39) || (unit >= 0x41 && unit <= 0x5A) || (unit >= 0x61 && unit <= 0x7A)) {
                        result += string.charAt(index);
                        continue;
                    }
                    result += '\\' + string.charAt(index);
                }
                return result;
            }

            function supports() {
                if (arguments.length === 0) throw new TypeError("Failed to execute 'supports' on 'CSS': 1 argument required, but only 0 present.");
                if (arguments.length === 1) return host.condition(String(arguments[0]));
                return host.declaration(String(arguments[0]), String(arguments[1]));
            }

            const css = {};
            Object.defineProperty(css, 'escape', { value: escape, writable: true, configurable: true, enumerable: true });
            Object.defineProperty(css, 'supports', { value: supports, writable: true, configurable: true, enumerable: true });
            Object.defineProperty(css, Symbol.toStringTag, { value: 'CSS', configurable: true });
            Object.defineProperty(globalThis, 'CSS', { value: css, writable: true, configurable: true, enumerable: false });
        })(globalThis.__folio_css_host);
        """;
}
