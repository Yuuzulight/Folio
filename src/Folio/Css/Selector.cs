using System.Text;
using Folio.Dom;

namespace Folio.Css;

internal enum Combinator
{
    None,
    Descendant,
    Child,
    NextSibling,
    SubsequentSibling,
}

internal enum PseudoElement
{
    None,
    Before,
    After,
    Marker,
    Selection,
    Placeholder,
    FirstLine,
    FirstLetter,

    /// <summary>An unrecognised <c>::-webkit-</c> pseudo-element: valid, never matches (Selectors 4, Appendix B).</summary>
    Unknown,
}

internal enum PseudoClass
{
    Root,
    Scope,
    Empty,
    FirstChild,
    LastChild,
    OnlyChild,
    FirstOfType,
    LastOfType,
    OnlyOfType,
    Link,
    AnyLink,
    Visited,
    Target,
    Hover,
    Active,
    Focus,
    FocusVisible,
    FocusWithin,
    Checked,
    Disabled,
    Enabled,
    Defined,
}

internal enum AttributeOperator
{
    Exists,
    Equals,
    Includes,
    DashMatch,
    Prefix,
    Suffix,
    Substring,
}

internal enum AttributeCase
{
    Default,
    Insensitive,
    Sensitive,
}

/// <summary>https://www.w3.org/TR/selectors-4/#specificity-rules</summary>
internal readonly record struct Specificity(int A, int B, int C) : IComparable<Specificity>
{
    public static Specificity operator +(Specificity x, Specificity y) => new(x.A + y.A, x.B + y.B, x.C + y.C);

    public int CompareTo(Specificity other) =>
        A != other.A ? A.CompareTo(other.A) : B != other.B ? B.CompareTo(other.B) : C.CompareTo(other.C);

    public static Specificity Max(IEnumerable<Specificity> values) =>
        values.Aggregate(default(Specificity), (max, s) => s.CompareTo(max) > 0 ? s : max);

    public override string ToString() => $"({A},{B},{C})";
}

internal abstract class SimpleSelector
{
    public abstract Specificity Specificity { get; }
}

/// <param name="Lower">The ASCII-lowercased name, which matches HTML elements (https://www.w3.org/TR/selectors-4/#case-sensitive).</param>
internal sealed class TypeSelector(string name, Atom exact, Atom lower) : SimpleSelector
{
    public string Name { get; } = name;
    public Atom Exact { get; } = exact;
    public Atom Lower { get; } = lower;
    public override Specificity Specificity => new(0, 0, 1);
    public override string ToString() => Name;
}

internal sealed class UniversalSelector : SimpleSelector
{
    public override Specificity Specificity => default;
    public override string ToString() => "*";
}

internal sealed class IdSelector(string name, Atom atom) : SimpleSelector
{
    public string Name { get; } = name;
    public Atom Atom { get; } = atom;
    public override Specificity Specificity => new(1, 0, 0);
    public override string ToString() => "#" + Name;
}

internal sealed class ClassSelector(string name, Atom atom) : SimpleSelector
{
    public string Name { get; } = name;
    public Atom Atom { get; } = atom;
    public override Specificity Specificity => new(0, 1, 0);
    public override string ToString() => "." + Name;
}

internal sealed class AttributeSelector(string name, AttributeOperator op, string value, AttributeCase @case) : SimpleSelector
{
    public string Name { get; } = name;
    public AttributeOperator Operator { get; } = op;
    public string Value { get; } = value;
    public AttributeCase Case { get; } = @case;
    public override Specificity Specificity => new(0, 1, 0);

    public override string ToString()
    {
        if (Operator == AttributeOperator.Exists)
            return $"[{Name}]";
        var op = Operator switch
        {
            AttributeOperator.Equals => "=",
            AttributeOperator.Includes => "~=",
            AttributeOperator.DashMatch => "|=",
            AttributeOperator.Prefix => "^=",
            AttributeOperator.Suffix => "$=",
            _ => "*=",
        };
        var flag = Case switch { AttributeCase.Insensitive => " i", AttributeCase.Sensitive => " s", _ => "" };
        return $"[{Name}{op}\"{Value}\"{flag}]";
    }
}

internal sealed class PseudoClassSelector(PseudoClass kind) : SimpleSelector
{
    public PseudoClass Kind { get; } = kind;
    public override Specificity Specificity => new(0, 1, 0);

    public override string ToString() => ":" + Kind switch
    {
        PseudoClass.FirstChild => "first-child",
        PseudoClass.LastChild => "last-child",
        PseudoClass.OnlyChild => "only-child",
        PseudoClass.FirstOfType => "first-of-type",
        PseudoClass.LastOfType => "last-of-type",
        PseudoClass.OnlyOfType => "only-of-type",
        PseudoClass.AnyLink => "any-link",
        PseudoClass.FocusVisible => "focus-visible",
        PseudoClass.FocusWithin => "focus-within",
        _ => Kind.ToString().ToLowerInvariant(),
    };
}

/// <summary><c>:nth-child(An+B)</c> and relatives.</summary>
internal sealed class NthSelector(bool ofType, bool fromEnd, int a, int b) : SimpleSelector
{
    public bool OfType { get; } = ofType;
    public bool FromEnd { get; } = fromEnd;
    public int A { get; } = a;
    public int B { get; } = b;
    public override Specificity Specificity => new(0, 1, 0);

    /// <summary>Whether a 1-based index is An+B for some n \u2265 0.</summary>
    public bool Matches(int index)
    {
        if (A == 0)
            return index == B;
        var n = (index - B) / A;
        return n >= 0 && (index - B) % A == 0;
    }

    public override string ToString() =>
        $":nth-{(FromEnd ? "last-" : "")}{(OfType ? "of-type" : "child")}({A}n{(B < 0 ? "" : "+")}{B})";
}

internal sealed class LangSelector(IReadOnlyList<string> ranges) : SimpleSelector
{
    public IReadOnlyList<string> Ranges { get; } = ranges;
    public override Specificity Specificity => new(0, 1, 0);
    public override string ToString() => $":lang({string.Join(", ", Ranges)})";
}

internal sealed class DirSelector(bool rtl) : SimpleSelector
{
    public bool Rtl { get; } = rtl;
    public override Specificity Specificity => new(0, 1, 0);
    public override string ToString() => Rtl ? ":dir(rtl)" : ":dir(ltr)";
}

internal enum LogicalKind
{
    Is,
    Where,
    Not,
}

/// <summary><c>:is()</c>, <c>:where()</c>, <c>:not()</c>: specificity of the most specific argument (zero for :where).</summary>
internal sealed class LogicalSelector(LogicalKind kind, SelectorList arguments) : SimpleSelector
{
    public LogicalKind Kind { get; } = kind;
    public SelectorList Arguments { get; } = arguments;
    public override Specificity Specificity => Kind == LogicalKind.Where ? default : Arguments.MaxSpecificity;
    public override string ToString() => $":{Kind.ToString().ToLowerInvariant()}({Arguments})";
}

/// <summary><c>&amp;</c>: the parent rule's selectors, like <c>:is()</c>; outside nesting, the root.</summary>
internal sealed class NestingSelector(SelectorList? parent) : SimpleSelector
{
    public SelectorList? Parent { get; } = parent;
    public override Specificity Specificity => Parent?.MaxSpecificity ?? new(0, 1, 0);
    public override string ToString() => "&";
}

internal sealed class CompoundSelector(List<SimpleSelector> simples, PseudoElement pseudoElement)
{
    public List<SimpleSelector> Simples { get; } = simples;
    public PseudoElement PseudoElement { get; } = pseudoElement;

    public override string ToString()
    {
        var text = new StringBuilder();
        foreach (var simple in Simples)
            text.Append(simple);
        if (PseudoElement != PseudoElement.None)
            text.Append("::").Append(PseudoElement == PseudoElement.Unknown ? "-webkit-unknown" : PseudoElementName(PseudoElement));
        return text.Length == 0 ? "*" : text.ToString();
    }

    private static string PseudoElementName(PseudoElement pe) => pe switch
    {
        PseudoElement.FirstLine => "first-line",
        PseudoElement.FirstLetter => "first-letter",
        _ => pe.ToString().ToLowerInvariant(),
    };
}

/// <summary>Compounds from left to right; each carries the combinator that joins it to the one before.</summary>
internal sealed class ComplexSelector(List<(Combinator Combinator, CompoundSelector Compound)> parts)
{
    public List<(Combinator Combinator, CompoundSelector Compound)> Parts { get; } = parts;

    public CompoundSelector Rightmost => Parts[^1].Compound;

    public PseudoElement PseudoElement => Rightmost.PseudoElement;

    public Specificity Specificity { get; } = parts.Aggregate(default(Specificity), (sum, part) =>
        part.Compound.Simples.Aggregate(sum, (s, simple) => s + simple.Specificity)
        + (part.Compound.PseudoElement == PseudoElement.None ? default : new Specificity(0, 0, 1)));

    public override string ToString()
    {
        var text = new StringBuilder();
        foreach (var (combinator, compound) in Parts)
        {
            text.Append(combinator switch
            {
                Combinator.Descendant => " ",
                Combinator.Child => " > ",
                Combinator.NextSibling => " + ",
                Combinator.SubsequentSibling => " ~ ",
                _ => "",
            });
            text.Append(compound);
        }
        return text.ToString();
    }
}

internal sealed class SelectorList(List<ComplexSelector> selectors)
{
    public List<ComplexSelector> Selectors { get; } = selectors;

    public Specificity MaxSpecificity => Specificity.Max(Selectors.Select(s => s.Specificity));

    public override string ToString() => string.Join(", ", Selectors);
}
