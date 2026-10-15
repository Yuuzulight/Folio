using Folio.Css;
using Folio.Dom;
using Folio.Style;

namespace Folio.Scripting.Dom;

/// <summary>Live CSS declaration block for an element's inline style attribute.</summary>
internal sealed class LiveStyleDeclaration(ElementNode element)
{
    public ElementNode Element { get; } = element;

    public List<Entry> Entries()
    {
        var text = Element.GetAttribute("style") ?? "";
        var (source, block) = CssParser.ParseBlockContents(text);
        var entries = new List<Entry>();
        foreach (var declaration in block.Declarations)
        {
            var value = declaration.Value.Count == 0 ? "" : source[declaration.Value[0].Start..declaration.Value[^1].End].Trim();
            var index = entries.FindIndex(e => e.Name.Equals(declaration.Name, StringComparison.OrdinalIgnoreCase));
            var entry = new Entry(declaration.Name, value, declaration.Important);
            if (index >= 0)
                entries[index] = entry;
            else
                entries.Add(entry);
        }
        return entries;
    }

    public string CssText => string.Join(" ", Entries().Select(e => $"{e.Name}: {e.Value}{(e.Important ? " !important" : "")};"));

    public string GetValue(string name)
    {
        name = NormalizeName(name);
        var entries = Entries();
        var direct = entries.FirstOrDefault(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (direct.Name is not null)
            return direct.Value;
        foreach (var shorthand in new[] { "margin", "padding" })
        {
            if (!name.StartsWith(shorthand + "-", StringComparison.Ordinal) || entries.FirstOrDefault(e => e.Name == shorthand) is not { Name: not null } source)
                continue;
            var parts = source.Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length is < 1 or > 4)
                continue;
            var values = parts.Length switch
            {
                1 => new[] { parts[0], parts[0], parts[0], parts[0] },
                2 => new[] { parts[0], parts[1], parts[0], parts[1] },
                3 => new[] { parts[0], parts[1], parts[2], parts[1] },
                _ => parts,
            };
            var suffix = name[(shorthand.Length + 1)..];
            return suffix switch { "top" => values[0], "right" => values[1], "bottom" => values[2], "left" => values[3], _ => "" };
        }
        return "";
    }

    public bool Set(string name, string value, string priority)
    {
        name = NormalizeName(name);
        priority = priority.Trim();
        if (name.Length == 0 || (priority.Length != 0 && !priority.Equals("important", StringComparison.OrdinalIgnoreCase)))
            return false;
        if (!IsValid(name, value, priority))
            return false;

        var entries = Entries();
        var index = entries.FindIndex(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        var entry = new Entry(name, value.Trim(), priority.Length > 0);
        if (index >= 0)
            entries[index] = entry;
        else
            entries.Add(entry);
        Write(entries);
        return true;
    }

    public string Remove(string name)
    {
        name = NormalizeName(name);
        var entries = Entries();
        var index = entries.FindIndex(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
            return "";
        var old = entries[index];
        entries.RemoveAt(index);
        Write(entries);
        return old.Value;
    }

    public void Replace(string cssText)
    {
        // Parse first so a malformed declaration block does not destroy the existing value.
        var (source, block) = CssParser.ParseBlockContents(cssText ?? "");
        var entries = new List<Entry>();
        foreach (var declaration in block.Declarations)
        {
            var value = declaration.Value.Count == 0 ? "" : source[declaration.Value[0].Start..declaration.Value[^1].End].Trim();
            if (IsValid(declaration.Name, value, declaration.Important ? "important" : ""))
                entries.Add(new(declaration.Name, value, declaration.Important));
        }
        Write(entries);
    }

    private void Write(IEnumerable<Entry> entries) =>
        Element.SetAttribute("style", string.Join(" ", entries.Select(e => $"{e.Name}: {e.Value}{(e.Important ? " !important" : "")};")));

    internal static string NormalizeName(string name)
    {
        if (name.Equals("cssFloat", StringComparison.Ordinal))
            return "float";
        if (name.StartsWith("--", StringComparison.Ordinal))
            return name;
        if (name.Length == 0)
            return name;
        var chars = new List<char>(name.Length + 4);
        foreach (var c in name)
        {
            if (char.IsUpper(c))
            {
                chars.Add('-');
                chars.Add(char.ToLowerInvariant(c));
            }
            else
                chars.Add(c);
        }
        return new string([.. chars]);
    }

    private static bool IsValid(string name, string value, string priority)
    {
        if (name.StartsWith("--", StringComparison.Ordinal))
            return value.Trim().Length > 0;
        var (source, block) = CssParser.ParseBlockContents($"{name}: {value}{(priority.Length > 0 ? " !important" : "")};");
        return block.Declarations.Count > 0 && CascadeData.Parse(source, block.Declarations).Count > 0;
    }

    public readonly record struct Entry(string Name, string Value, bool Important);
}
