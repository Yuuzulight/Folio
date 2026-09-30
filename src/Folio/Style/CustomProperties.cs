using System.Collections.Immutable;
using System.Text;
using Folio.Css;

namespace Folio.Style;

/// <summary>
/// A registration from <c>@property</c> (https://www.w3.org/TR/css-properties-values-api-1/#at-property-rule); the
/// initial value is already computed for the syntax.
/// </summary>
internal sealed record RegisteredProperty(bool Inherits, string? InitialValue, PropertySyntax Syntax);

/// <summary>
/// Custom properties and <c>var()</c> (https://www.w3.org/TR/css-variables-1/): values are token text, inherited
/// through a persistent map that elements without their own custom declarations share with their parent.
/// </summary>
internal static class CustomProperties
{
    /// <summary>A declared custom property value: raw text, or a CSS-wide keyword.</summary>
    public readonly record struct Declared(string? Text, CssWideKeyword? Keyword);

    public static ImmutableDictionary<string, string> Compute(
        ImmutableDictionary<string, string> parent,
        IReadOnlyDictionary<string, Declared> declared,
        IReadOnlyDictionary<string, RegisteredProperty> registered)
    {
        var map = parent;
        foreach (var (name, registration) in registered)
        {
            // A registered property always has a value: its initial one where nothing is inherited.
            if (!registration.Inherits)
                map = registration.InitialValue is { } initial ? map.SetItem(name, initial) : map.Remove(name);
            else if (registration.InitialValue is { } initial && !map.ContainsKey(name))
                map = map.SetItem(name, initial);
        }
        if (declared.Count == 0)
            return map;

        // Declared values first (keywords resolved), then var() inside them, with cycle detection.
        var raw = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var (name, value) in declared)
        {
            raw[name] = value.Keyword switch
            {
                null => value.Text,
                CssWideKeyword.Inherit => parent.GetValueOrDefault(name),
                CssWideKeyword.Initial => registered.GetValueOrDefault(name)?.InitialValue,
                _ => registered.GetValueOrDefault(name) is { Inherits: false } r ? r.InitialValue : parent.GetValueOrDefault(name),
            };
        }

        var resolved = new Dictionary<string, string?>(StringComparer.Ordinal);
        var stack = new List<string>();
        var inCycle = new HashSet<string>(StringComparer.Ordinal);

        string? Resolve(string name)
        {
            if (resolved.TryGetValue(name, out var done))
                return done;
            if (!raw.TryGetValue(name, out var text))
                return map.GetValueOrDefault(name);
            var at = stack.IndexOf(name);
            if (at >= 0)
            {
                // Every property in the cycle is invalid at computed-value time.
                foreach (var member in stack.Skip(at))
                    inCycle.Add(member);
                return null;
            }
            stack.Add(name);
            var value = text is null ? null : text.Contains("var(", StringComparison.OrdinalIgnoreCase) ? Substitute(text, Resolve) : text;
            stack.RemoveAt(stack.Count - 1);
            if (inCycle.Contains(name))
                value = null;
            resolved[name] = value;
            return value;
        }

        foreach (var name in raw.Keys)
        {
            var value = Resolve(name);
            map = value is null ? map.Remove(name) : map.SetItem(name, value);
        }
        return map;
    }

    /// <summary>
    /// Computes the element's registered custom properties for their syntax, once its font size and colour are known
    /// (https://www.w3.org/TR/css-properties-values-api-1/#calculation-of-computed-values). A value that does not
    /// match is invalid at computed-value time: the property is then inherited or initial, as if unset.
    /// </summary>
    public static ImmutableDictionary<string, string> ApplySyntax(ImmutableDictionary<string, string> map, ImmutableDictionary<string, string> parent,
                                                                   IReadOnlyDictionary<string, RegisteredProperty> registered, ComputeContext context)
    {
        foreach (var (name, registration) in registered)
        {
            if (registration.Syntax.IsUniversal)
                continue;
            var unset = registration.Inherits ? parent.GetValueOrDefault(name) ?? registration.InitialValue : registration.InitialValue;
            if (!map.TryGetValue(name, out var value))
            {
                if (unset is not null)
                    map = map.SetItem(name, unset);
                continue;
            }
            // Inherited and initial values are computed already.
            if (value == registration.InitialValue || (registration.Inherits && value == parent.GetValueOrDefault(name)))
                continue;
            var computed = registration.Syntax.Compute(value, context) ?? unset;
            map = computed is null ? map.Remove(name) : map.SetItem(name, computed);
        }
        return map;
    }

    /// <summary>
    /// Replaces every <c>var()</c> in <paramref name="text"/>; null when a reference has neither a value nor a
    /// fallback. Substituted text is fenced with empty comments so tokens never merge across the boundary.
    /// </summary>
    public static string? Substitute(string text, Func<string, string?> lookup)
    {
        var (source, values) = CssParser.ParseComponentValues(text);
        var result = new StringBuilder();
        return Append(source, values, lookup, result) ? result.ToString() : null;
    }

    private static bool Append(string source, List<ComponentValue> values, Func<string, string?> lookup, StringBuilder result)
    {
        foreach (var value in values)
        {
            switch (value)
            {
                case PreservedToken { Token.Kind: CssTokenKind.Whitespace }:
                    result.Append(' ');
                    break;
                case PreservedToken token:
                    result.Append("/**/").Append(source, token.Start, token.End - token.Start);
                    break;
                case CssFunction var when var.Name.Equals("var", StringComparison.OrdinalIgnoreCase):
                    var args = var.Arguments;
                    var nameIndex = args.FindIndex(a => a is not PreservedToken { Token.Kind: CssTokenKind.Whitespace });
                    if (nameIndex < 0 || args[nameIndex] is not PreservedToken { Token.Kind: CssTokenKind.Ident } name
                        || !name.Token.Value.StartsWith("--", StringComparison.Ordinal))
                        return false;
                    var comma = args.FindIndex(nameIndex + 1, a => a is PreservedToken { Token.Kind: CssTokenKind.Comma });
                    if (comma < 0 && args.Skip(nameIndex + 1).Any(a => a is not PreservedToken { Token.Kind: CssTokenKind.Whitespace }))
                        return false;
                    if (lookup(name.Token.Value) is { } substituted)
                        result.Append("/**/").Append(substituted).Append("/**/");
                    else if (comma < 0 || !Append(source, args[(comma + 1)..], lookup, result))
                        return false;
                    break;
                case CssFunction function:
                    result.Append("/**/").Append(function.Name).Append('(');
                    if (!Append(source, function.Arguments, lookup, result))
                        return false;
                    result.Append(')');
                    break;
                case SimpleBlock block:
                    var (open, close) = block.Open switch
                    {
                        CssTokenKind.LeftBrace => ('{', '}'),
                        CssTokenKind.LeftBracket => ('[', ']'),
                        _ => ('(', ')'),
                    };
                    result.Append(open);
                    if (!Append(source, block.Contents, lookup, result))
                        return false;
                    result.Append(close);
                    break;
            }
        }
        return true;
    }

    /// <summary>
    /// Substitutes and parses a longhand's pending value; null when it is invalid at computed-value time
    /// (the property then behaves as unset).
    /// </summary>
    public static CssValue? Resolve(UnparsedValue value, Property property, ImmutableDictionary<string, string> custom)
    {
        var text = Substitute(value.Text, name => custom.GetValueOrDefault(name));
        if (text is null)
            return null;
        var (source, values) = CssParser.ParseComponentValues(text);
        var parsed = Properties.Parse(source, new Declaration(value.Shorthand ?? property.Name, values, false));
        return parsed?.FirstOrDefault(p => p.Id == property.Id).Value;
    }
}
