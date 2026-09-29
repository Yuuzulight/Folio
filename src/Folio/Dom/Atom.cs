namespace Folio.Dom;

/// <summary>
/// An interned name (tag, attribute, class, id): equality is an int compare. Positive values come from the
/// process-wide <see cref="AtomTable"/>, negative ones from the owning document's overflow table.
/// </summary>
internal readonly record struct Atom(int Value)
{
    public static Atom None => default;

    public bool IsNone => Value == 0;
}

/// <summary>
/// Process-wide, append-only, thread-safe and bounded (docs/study/02-dom.md). When it is full, new strings
/// are interned per document instead (<see cref="DocumentNode.Intern"/>), so memory stays bounded by the documents alive.
/// </summary>
internal sealed class AtomTable(int capacity)
{
    public const int DefaultCapacity = 1 << 20;

    public static AtomTable Shared { get; } = new(DefaultCapacity);

    private readonly Dictionary<string, Atom> _ids = new(StringComparer.Ordinal);
    // ponytail: one lock for reads and writes; lock-free reads if parallel parsing contends on it.
    private readonly Lock _lock = new();
    private string[] _texts = new string[64];
    private int _count = 1; // 0 is Atom.None

    public int Count => Volatile.Read(ref _count) - 1;

    /// <returns>False when the string is new and the table is full.</returns>
    public bool TryIntern(string text, out Atom atom)
    {
        lock (_lock)
        {
            if (_ids.TryGetValue(text, out atom))
                return true;
            if (_count > capacity)
                return false;

            if (_count == _texts.Length)
            {
                var grown = new string[_texts.Length * 2];
                _texts.CopyTo(grown, 0);
                Volatile.Write(ref _texts, grown);
            }
            _texts[_count] = text;
            atom = new Atom(_count);
            _ids.Add(text, atom);
            Volatile.Write(ref _count, _count + 1);
            return true;
        }
    }

    /// <returns>The atom for an already interned string, or <see cref="Atom.None"/>. Never adds.</returns>
    public Atom Find(string text)
    {
        lock (_lock)
            return _ids.GetValueOrDefault(text);
    }

    public string Text(Atom atom) => Volatile.Read(ref _texts)[atom.Value];
}

/// <summary>Well-known namespace URIs, interned first so they always have shared atoms.</summary>
internal static class Namespaces
{
    public const string HtmlUri = "http://www.w3.org/1999/xhtml";
    public const string SvgUri = "http://www.w3.org/2000/svg";
    public const string MathMLUri = "http://www.w3.org/1998/Math/MathML";
    public const string XLinkUri = "http://www.w3.org/1999/xlink";
    public const string XmlUri = "http://www.w3.org/XML/1998/namespace";
    public const string XmlnsUri = "http://www.w3.org/2000/xmlns/";

    public static Atom Html { get; } = Intern(HtmlUri);
    public static Atom Svg { get; } = Intern(SvgUri);
    public static Atom MathML { get; } = Intern(MathMLUri);
    public static Atom XLink { get; } = Intern(XLinkUri);
    public static Atom Xml { get; } = Intern(XmlUri);
    public static Atom Xmlns { get; } = Intern(XmlnsUri);

    private static Atom Intern(string uri) =>
        AtomTable.Shared.TryIntern(uri, out var atom) ? atom : throw new InvalidOperationException("Atom table full at startup.");
}
