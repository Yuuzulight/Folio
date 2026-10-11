using Folio.Dom;

namespace Folio.RenderTests;

/// <summary>
/// The safety net for incremental updates (docs/study/14-invalidation.md): after any DOM mutation, <c>Update()</c> must
/// leave the document drawing exactly what a fresh render of the same DOM draws. Each conformance page gets a seeded
/// run of random style changes to random elements; after every one the display list is compared with a fresh
/// document's, which had the same changes applied before its first paint.
/// </summary>
public class IncrementalEquivalenceTests
{
    // Layout-affecting ones on purpose: paint-only changes never reach a layout cache.
    private static readonly string[] Changes =
    [
        "width:123px", "height:41px", "min-height:60px", "margin-top:17px", "margin-left:9px", "padding:9px",
        "border:3px solid red", "float:left", "float:right", "clear:both", "font-size:23px", "line-height:2",
        "text-align:right", "display:inline-block", "position:relative;top:5px", "position:absolute;left:4px;top:8px",
        "overflow:hidden;height:30px", "white-space:nowrap", "letter-spacing:2px", "color:red",
    ];

    private const int StepsPerPage = 6;

    public static TheoryData<string> Pages =>
        Directory.Exists(Conformance.PublicRoot) ? new(Directory.GetFiles(Conformance.PublicRoot, "index.html", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(Conformance.PublicRoot, Path.GetDirectoryName(f)!).Replace('\\', '/')).Order()) : new("(none)");

    private static List<ElementNode> Elements(Document document)
    {
        var all = new List<ElementNode>();
        for (Node? node = document.Node; node is not null; node = node.NextInTree(document.Node))
        {
            if (node is ElementNode { LocalName: not ("html" or "head" or "style" or "script" or "title" or "meta" or "link") } element)
                all.Add(element);
        }
        return all;
    }

    private static void Apply(Document document, int index, string change)
    {
        var element = Elements(document)[index];
        var style = element.GetAttribute("style");
        element.SetAttribute("style", string.IsNullOrEmpty(style) ? change : $"{style.TrimEnd().TrimEnd(';')};{change}");
    }


    // Display items hold arrays and lists, which record equality compares by reference: compare them by what they hold.
    private static bool Same<T>(T a, T b) => Describe(a, 0) == Describe(b, 0);

    private static string Describe(object? value, int depth)
    {
        switch (value)
        {
            case null: return "null";
            case string text: return text;
            case float f: return f.ToString("R");
            case double d: return d.ToString("R");
        }
        var type = value.GetType();
        if (type.IsPrimitive || type.IsEnum)
            return value.ToString()!;
        if (value is System.Collections.IEnumerable items)
        {
            var list = items.Cast<object?>().ToList();
            return $"[{list.Count}:{string.Join(",", list.Take(400).Select(x => Describe(x, depth + 1)))}]";
        }
        if (depth > 8 || type.Namespace is null || !type.Namespace.StartsWith("Folio", StringComparison.Ordinal))
            return type.Name;
        var members = type.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Where(p => p.GetIndexParameters().Length == 0).Select(p => $"{p.Name}={Describe(p.GetValue(value), depth + 1)}");
        return $"{type.Name}{{{string.Join(",", members)}}}";
    }

    // The few characters around where the two descriptions first differ.
    private static string Diff<T>(List<T> got, List<T> want, int index)
    {
        if (index < 0 || index >= got.Count || index >= want.Count)
            return "(different counts)";
        var (g, w) = (Describe(got[index], 0), Describe(want[index], 0));
        var at = Enumerable.Range(0, Math.Min(g.Length, w.Length)).FirstOrDefault(i => g[i] != w[i], 0);
        var from = Math.Max(0, at - 120);
        return $"kind {g[..Math.Min(40, g.Length)]}; update ...{g[from..Math.Min(g.Length, at + 40)]} / fresh ...{w[from..Math.Min(w.Length, at + 40)]}";
    }

    [Theory]
    [MemberData(nameof(Pages))]
    public void UpdateDrawsWhatAFreshRenderDraws(string page)
    {
        Assert.SkipWhen(page == "(none)", "The conformance corpus is empty.");

        var html = File.ReadAllText(Path.Combine(Conformance.PublicRoot, page, "index.html"));
        using var document = Document.Parse(html);
        document.Paint(800, 600);
        using (var again = Document.Parse(html))
            Assert.True(Same(again.Paint(800, 600).Item1.Items, document.DisplayList!.Items), $"{page}: two fresh renders of the same page differ (the comparison, not Update, is at fault).");
        var count = Elements(document).Count;
        var random = new Random(page.GetHashCode() & 0x7fffffff ^ 1234);
        var applied = new List<(int Index, string Change)>();

        for (var step = 0; step < StepsPerPage && count > 0; step++)
        {
            var (index, change) = (random.Next(count), Changes[random.Next(Changes.Length)]);
            applied.Add((index, change));
            Apply(document, index, change);
            document.Update();

            using var fresh = Document.Parse(html);
            foreach (var (i, c) in applied)
                Apply(fresh, i, c);
            var (expected, _) = fresh.Paint(800, 600);
            var (want, got) = (expected.Items.ToList(), document.DisplayList!.Items.ToList());
            var first = Enumerable.Range(0, Math.Min(want.Count, got.Count)).FirstOrDefault(i => !Same(want[i], got[i]), -1);
            Assert.True(first == -1 && want.Count == got.Count,
                $"{page}: after {string.Join(" / ", applied.Select(a => $"#{a.Index} {a.Change}"))} the update drew something else than a fresh render "
                + $"({got.Count} items, fresh {want.Count}); first difference at {first}: {Diff(got, want, first)}");
        }
    }
}
