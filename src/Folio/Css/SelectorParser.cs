using System.Text.RegularExpressions;
using Folio.Dom;

namespace Folio.Css;

/// <summary>
/// Parses selectors (https://www.w3.org/TR/selectors-4/#grammar) from component values, for the selector set of
/// docs/study/03-css-parsing-and-selectors.md. Anything outside it makes the selector invalid (null), as the spec
/// requires; <c>:is()</c> and <c>:where()</c> drop only their invalid arguments.
/// </summary>
internal static partial class SelectorParser
{
    /// <param name="source">The stylesheet source the component values index into.</param>
    /// <param name="intern">Interns names into the atoms the matched document uses.</param>
    /// <param name="parent">For a nested rule, the parent rule's selectors (https://drafts.csswg.org/css-nesting-1/).</param>
    /// <param name="nested">True for a nested rule: selectors are relative to <paramref name="parent"/>.</param>
    public static SelectorList? Parse(string source, List<ComponentValue> values, Func<string, Atom> intern,
                                      SelectorList? parent = null, bool nested = false)
    {
        var context = new Context(source, intern, parent);
        return context.ParseList(values, forgiving: false, relative: nested);
    }

    private sealed class Context(string source, Func<string, Atom> intern, SelectorList? parent)
    {
        public SelectorList? ParseList(List<ComponentValue> values, bool forgiving, bool relative)
        {
            var selectors = new List<ComplexSelector>();
            foreach (var part in SplitOnCommas(values))
            {
                var complex = ParseComplex(part, relative);
                if (complex is not null)
                    selectors.Add(complex);
                else if (!forgiving)
                    return null;
            }
            return selectors.Count > 0 || forgiving ? new SelectorList(selectors) : null;
        }

        private static List<List<ComponentValue>> SplitOnCommas(List<ComponentValue> values)
        {
            var parts = new List<List<ComponentValue>> { new() };
            foreach (var value in values)
            {
                if (value is PreservedToken { Token.Kind: CssTokenKind.Comma })
                    parts.Add([]);
                else
                    parts[^1].Add(value);
            }
            return parts;
        }

        private ComplexSelector? ParseComplex(List<ComponentValue> values, bool relative)
        {
            var parts = new List<(Combinator, CompoundSelector)>();
            var i = 0;
            var pendingCombinator = Combinator.None;
            while (true)
            {
                var sawWhitespace = SkipWhitespace(values, ref i);
                if (i >= values.Count)
                    break;

                var combinator = CombinatorAt(values, i);
                if (combinator != Combinator.None)
                {
                    if (pendingCombinator != Combinator.None && pendingCombinator != Combinator.Descendant)
                        return null; // two combinators in a row
                    if (parts.Count == 0 && !relative)
                        return null;
                    pendingCombinator = combinator;
                    i++;
                    continue;
                }
                if (sawWhitespace && parts.Count > 0 && pendingCombinator == Combinator.None)
                    pendingCombinator = Combinator.Descendant;

                if (parts.Count > 0 && pendingCombinator == Combinator.None)
                    return null;
                if (parts.Count > 0 && parts[^1].Item2.PseudoElement != PseudoElement.None)
                    return null; // nothing may follow a pseudo-element

                var compound = ParseCompound(values, ref i);
                if (compound is null)
                    return null;
                if (parts.Count == 0 && pendingCombinator != Combinator.None)
                {
                    // A relative selector starting with a combinator: "& > a".
                    parts.Add((Combinator.None, Nesting()));
                }
                parts.Add((parts.Count == 0 ? Combinator.None : pendingCombinator, compound));
                pendingCombinator = Combinator.None;
            }

            if (parts.Count == 0 || pendingCombinator != Combinator.None)
                return null;

            // A nested selector without '&' is relative to the parent as a descendant.
            if (relative && !parts.Any(p => ContainsNesting(p.Item2)))
                parts.Insert(0, (Combinator.None, Nesting()));
            if (relative && parts.Count > 1 && parts[1].Item1 == Combinator.None)
                parts[1] = (Combinator.Descendant, parts[1].Item2);

            return new ComplexSelector(parts);
        }

        private CompoundSelector Nesting() => new([new NestingSelector(parent)], PseudoElement.None);

        private static bool ContainsNesting(CompoundSelector compound) =>
            compound.Simples.Any(s => s is NestingSelector
                || (s is LogicalSelector logical && logical.Arguments.Selectors.Any(c => c.Parts.Any(p => ContainsNesting(p.Compound)))));

        private static bool SkipWhitespace(List<ComponentValue> values, ref int i)
        {
            var skipped = false;
            while (i < values.Count && values[i] is PreservedToken { Token.Kind: CssTokenKind.Whitespace })
            {
                i++;
                skipped = true;
            }
            return skipped;
        }

        private static Combinator CombinatorAt(List<ComponentValue> values, int i) => values[i] switch
        {
            PreservedToken t when t.Token.IsDelim('>') => Combinator.Child,
            PreservedToken t when t.Token.IsDelim('+') => Combinator.NextSibling,
            PreservedToken t when t.Token.IsDelim('~') => Combinator.SubsequentSibling,
            _ => Combinator.None,
        };

        private CompoundSelector? ParseCompound(List<ComponentValue> values, ref int i)
        {
            var simples = new List<SimpleSelector>();
            var pseudoElement = PseudoElement.None;

            while (i < values.Count)
            {
                var value = values[i];
                if (value is PreservedToken { Token.Kind: CssTokenKind.Whitespace } || CombinatorAt(values, i) != Combinator.None)
                    break;
                if (pseudoElement != PseudoElement.None)
                    return null; // pseudo-classes after pseudo-elements are outside the M1 set

                switch (value)
                {
                    case PreservedToken { Token.Kind: CssTokenKind.Ident } t when simples.Count == 0:
                        if (Peek(values, i + 1) is PreservedToken next && next.Token.IsDelim('|'))
                            return null; // namespace prefixes are not supported
                        simples.Add(new TypeSelector(t.Token.Value, intern(t.Token.Value), intern(t.Token.Value.ToLowerInvariant())));
                        i++;
                        break;
                    case PreservedToken t when t.Token.IsDelim('*') && simples.Count == 0:
                        if (Peek(values, i + 1) is PreservedToken next2 && next2.Token.IsDelim('|'))
                            return null;
                        simples.Add(new UniversalSelector());
                        i++;
                        break;
                    case PreservedToken t when t.Token.IsDelim('&'):
                        simples.Add(new NestingSelector(parent));
                        i++;
                        break;
                    case PreservedToken { Token.Kind: CssTokenKind.Hash } t:
                        if (!t.Token.IsIdHash)
                            return null;
                        simples.Add(new IdSelector(t.Token.Value, intern(t.Token.Value)));
                        i++;
                        break;
                    case PreservedToken t when t.Token.IsDelim('.'):
                        if (Peek(values, i + 1) is not PreservedToken { Token.Kind: CssTokenKind.Ident } name)
                            return null;
                        simples.Add(new ClassSelector(name.Token.Value, intern(name.Token.Value)));
                        i += 2;
                        break;
                    case SimpleBlock { Open: CssTokenKind.LeftBracket } block:
                        var attribute = ParseAttribute(block.Contents);
                        if (attribute is null)
                            return null;
                        simples.Add(attribute);
                        i++;
                        break;
                    case PreservedToken { Token.Kind: CssTokenKind.Colon }:
                        i++;
                        if (Peek(values, i) is PreservedToken { Token.Kind: CssTokenKind.Colon })
                        {
                            i++;
                            pseudoElement = Peek(values, i) is PreservedToken { Token.Kind: CssTokenKind.Ident } pe
                                ? PseudoElementNamed(pe.Token.Value)
                                : PseudoElement.None;
                            if (pseudoElement == PseudoElement.None)
                                return null;
                            i++;
                            break;
                        }
                        switch (Peek(values, i))
                        {
                            case PreservedToken { Token.Kind: CssTokenKind.Ident } ident:
                                var legacy = ident.Token.Value.ToLowerInvariant() switch
                                {
                                    "before" => PseudoElement.Before,
                                    "after" => PseudoElement.After,
                                    "first-line" => PseudoElement.FirstLine,
                                    "first-letter" => PseudoElement.FirstLetter,
                                    _ => PseudoElement.None,
                                };
                                if (legacy != PseudoElement.None)
                                {
                                    pseudoElement = legacy;
                                }
                                else
                                {
                                    var pseudo = PseudoClassNamed(ident.Token.Value);
                                    if (pseudo is null)
                                        return null;
                                    simples.Add(new PseudoClassSelector(pseudo.Value));
                                }
                                i++;
                                break;
                            case CssFunction function:
                                var functional = ParseFunctional(function);
                                if (functional is null)
                                    return null;
                                simples.Add(functional);
                                i++;
                                break;
                            default:
                                return null;
                        }
                        break;
                    default:
                        return null;
                }
            }

            return simples.Count == 0 && pseudoElement == PseudoElement.None ? null : new CompoundSelector(simples, pseudoElement);
        }

        private static ComponentValue? Peek(List<ComponentValue> values, int i) => i < values.Count ? values[i] : null;

        private static PseudoElement PseudoElementNamed(string name)
        {
            var lower = name.ToLowerInvariant();
            return lower switch
            {
                "before" => PseudoElement.Before,
                "after" => PseudoElement.After,
                "marker" => PseudoElement.Marker,
                "selection" => PseudoElement.Selection,
                "placeholder" => PseudoElement.Placeholder,
                "first-line" => PseudoElement.FirstLine,
                "first-letter" => PseudoElement.FirstLetter,
                _ when lower.StartsWith("-webkit-", StringComparison.Ordinal) => PseudoElement.Unknown,
                _ => PseudoElement.None,
            };
        }

        private static PseudoClass? PseudoClassNamed(string name) => name.ToLowerInvariant() switch
        {
            "root" => PseudoClass.Root,
            "scope" => PseudoClass.Scope,
            "empty" => PseudoClass.Empty,
            "first-child" => PseudoClass.FirstChild,
            "last-child" => PseudoClass.LastChild,
            "only-child" => PseudoClass.OnlyChild,
            "first-of-type" => PseudoClass.FirstOfType,
            "last-of-type" => PseudoClass.LastOfType,
            "only-of-type" => PseudoClass.OnlyOfType,
            "link" => PseudoClass.Link,
            "any-link" => PseudoClass.AnyLink,
            "visited" => PseudoClass.Visited,
            "target" => PseudoClass.Target,
            "hover" => PseudoClass.Hover,
            "active" => PseudoClass.Active,
            "focus" => PseudoClass.Focus,
            "focus-visible" => PseudoClass.FocusVisible,
            "focus-within" => PseudoClass.FocusWithin,
            "checked" => PseudoClass.Checked,
            "disabled" => PseudoClass.Disabled,
            "enabled" => PseudoClass.Enabled,
            "defined" => PseudoClass.Defined,
            _ => null,
        };

        private SimpleSelector? ParseFunctional(CssFunction function)
        {
            switch (function.Name.ToLowerInvariant())
            {
                case "is":
                case "where":
                    var forgiving = ParseList(function.Arguments, forgiving: true, relative: false);
                    return forgiving is null || HasPseudoElement(forgiving)
                        ? null
                        : new LogicalSelector(function.Name.Equals("is", StringComparison.OrdinalIgnoreCase) ? LogicalKind.Is : LogicalKind.Where, forgiving);
                case "not":
                    var list = ParseList(function.Arguments, forgiving: false, relative: false);
                    return list is null || HasPseudoElement(list) ? null : new LogicalSelector(LogicalKind.Not, list);
                case "nth-child":
                    return ParseNth(function, ofType: false, fromEnd: false);
                case "nth-last-child":
                    return ParseNth(function, ofType: false, fromEnd: true);
                case "nth-of-type":
                    return ParseNth(function, ofType: true, fromEnd: false);
                case "nth-last-of-type":
                    return ParseNth(function, ofType: true, fromEnd: true);
                case "lang":
                    var ranges = SplitOnCommas(function.Arguments)
                        .Select(part => part.Where(v => v is not PreservedToken { Token.Kind: CssTokenKind.Whitespace }).ToList())
                        .Select(part => part is [PreservedToken { Token.Kind: CssTokenKind.Ident or CssTokenKind.String } t] ? t.Token.Value : null)
                        .ToList();
                    return ranges.Count == 0 || ranges.Contains(null) ? null : new LangSelector(ranges!);
                case "dir":
                    var args = function.Arguments.Where(v => v is not PreservedToken { Token.Kind: CssTokenKind.Whitespace }).ToList();
                    return args is [PreservedToken { Token.Kind: CssTokenKind.Ident } dir]
                        ? dir.Token.IsIdent("rtl") ? new DirSelector(true) : dir.Token.IsIdent("ltr") ? new DirSelector(false) : null
                        : null;
                default:
                    return null; // includes :has(), which arrives in M2
            }
        }

        private static bool HasPseudoElement(SelectorList list) => list.Selectors.Any(s => s.Parts.Any(p => p.Compound.PseudoElement != PseudoElement.None));

        // https://www.w3.org/TR/css-syntax-3/#anb-microsyntax, read from the argument text with whitespace kept.
        private NthSelector? ParseNth(CssFunction function, bool ofType, bool fromEnd)
        {
            var text = string.Concat(function.Arguments.Select(v => v is PreservedToken { Token.Kind: CssTokenKind.Whitespace }
                ? " "
                : source[v.Start..v.End])).Trim();
            var lower = text.ToLowerInvariant();
            if (lower == "odd")
                return new NthSelector(ofType, fromEnd, 2, 1);
            if (lower == "even")
                return new NthSelector(ofType, fromEnd, 2, 0);
            if (IntegerPattern().IsMatch(text))
                return new NthSelector(ofType, fromEnd, 0, int.Parse(text, System.Globalization.CultureInfo.InvariantCulture));
            var match = AnPlusBPattern().Match(lower);
            if (!match.Success)
                return null;
            var aText = match.Groups[1].Value;
            var a = aText switch { "" or "+" => 1, "-" => -1, _ => int.Parse(aText, System.Globalization.CultureInfo.InvariantCulture) };
            var b = match.Groups[3].Success
                ? int.Parse(match.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture) * (match.Groups[2].Value == "-" ? -1 : 1)
                : 0;
            return new NthSelector(ofType, fromEnd, a, b);
        }

        private AttributeSelector? ParseAttribute(List<ComponentValue> contents)
        {
            var parts = contents.Where(v => v is not PreservedToken { Token.Kind: CssTokenKind.Whitespace }).ToList();
            if (parts is not [PreservedToken { Token.Kind: CssTokenKind.Ident } name, ..])
                return null;
            if (parts.Count == 1)
                return new AttributeSelector(name.Token.Value, AttributeOperator.Exists, "", AttributeCase.Default);

            var i = 1;
            AttributeOperator op;
            if (parts[i] is PreservedToken eq && eq.Token.IsDelim('='))
            {
                op = AttributeOperator.Equals;
                i++;
            }
            else if (parts.Count > i + 1 && parts[i] is PreservedToken first && parts[i + 1] is PreservedToken second
                     && second.Token.IsDelim('=') && first.End == second.Start && first.Token.Kind == CssTokenKind.Delim)
            {
                op = first.Token.Value switch
                {
                    "~" => AttributeOperator.Includes,
                    "|" => AttributeOperator.DashMatch,
                    "^" => AttributeOperator.Prefix,
                    "$" => AttributeOperator.Suffix,
                    "*" => AttributeOperator.Substring,
                    _ => (AttributeOperator)(-1),
                };
                if ((int)op < 0)
                    return null;
                i += 2;
            }
            else
            {
                return null;
            }

            if (i >= parts.Count || parts[i] is not PreservedToken { Token.Kind: CssTokenKind.Ident or CssTokenKind.String } value)
                return null;
            i++;
            var @case = AttributeCase.Default;
            if (i < parts.Count)
            {
                if (parts[i] is PreservedToken flag && flag.Token.IsIdent("i"))
                    @case = AttributeCase.Insensitive;
                else if (parts[i] is PreservedToken flag2 && flag2.Token.IsIdent("s"))
                    @case = AttributeCase.Sensitive;
                else
                    return null;
                i++;
            }
            return i == parts.Count ? new AttributeSelector(name.Token.Value, op, value.Token.Value, @case) : null;
        }
    }

    [GeneratedRegex(@"^[+-]?\d+$")]
    private static partial Regex IntegerPattern();

    // [+-]?[digits]n, then an optional [+-] <digits> with whitespace allowed only around the sign.
    [GeneratedRegex(@"^([+-]?\d*)n(?:\s*([+-])\s*(\d+))?$")]
    private static partial Regex AnPlusBPattern();
}
