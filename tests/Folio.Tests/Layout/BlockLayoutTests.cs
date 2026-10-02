using System.Globalization;
using Folio.Css;
using Folio.Dom;
using Folio.Html;
using Folio.Layout;
using Folio.Style;
using Folio.Typography;

namespace Folio.Tests.Layout;

public class BlockLayoutTests
{
    private static readonly Lazy<Dictionary<string, CaseFiles.Case>> AllCases = new(() => CaseFiles.Load("Layout", "Block"));

    public static TheoryData<string> Ids => new(AllCases.Value.Keys.Order());

    [Theory]
    [MemberData(nameof(Ids))]
    public void LaysOut(string id)
    {
        var test = AllCases.Value[id];
        var viewport = test.Directives.FirstOrDefault(d => d.StartsWith("viewport ", StringComparison.Ordinal))?.Split(' ');
        var (width, height) = viewport is null ? (800f, 600f) : (float.Parse(viewport[1], CultureInfo.InvariantCulture), float.Parse(viewport[2], CultureInfo.InvariantCulture));

        Assert.Equal(test.Expected, Dump(LayOut(test.Input, width, height)));
    }

    [Fact]
    public void ChildrenLiveInTheLayoutsBuffers()
    {
        var leaf = new Fragment(null, 1, 1, []);
        ChildFragment[] own = [new(1, 2, leaf), new(3, 4, leaf)];

        // Outside a layout, an array is kept as it is and a list is copied.
        var outside = new Fragment(null, 1, 1, own);
        Assert.Equal(own, outside.Children.ToArray());
        Assert.True(outside.Children is [{ X: 1 }, { X: 3 }]);
        Assert.Equal([new ChildFragment(3, 4, leaf)], outside.Children.Slice(1).ToArray());
        Assert.Empty(leaf.Children);

        // During a layout, children go into the arena's shared buffer, one fragment's after another's.
        using (FragmentArena.Open())
        {
            var first = new Fragment(null, 1, 1, own);
            var second = new Fragment(null, 1, 1, new List<ChildFragment>(own));
            Assert.Equal(own, first.Children.ToArray());
            Assert.Equal(own, second.Children.ToArray());
            Assert.True(System.Runtime.CompilerServices.Unsafe.AreSame(
                ref System.Runtime.InteropServices.MemoryMarshal.GetReference(second.Children.AsSpan()),
                ref System.Runtime.CompilerServices.Unsafe.Add(ref System.Runtime.InteropServices.MemoryMarshal.GetReference(first.Children.AsSpan()), 2)));
        }

        // A whole layout's fragments keep their children: the text of both paragraphs, in three runs, is all there.
        var page = LayOut("<p>one</p><p>two <b>three</b></p>");
        Assert.Equal([3, 4, 5], Texts(page));

        static IEnumerable<int> Texts(Fragment fragment) =>
            fragment.Children.SelectMany(c => c.Fragment.Text is { } t ? [t.GlyphEnd - t.GlyphStart] : Texts(c.Fragment));
    }

    [Fact]
    public void FragmentsKeepTheirRareFields()
    {
        // Ruby, SVG and column rule fields live apart from the fragment's own (#196); set or not, they read back.
        var plain = new Fragment(null, 1, 1, []) { Svg = null, ColumnRules = null, RubyOver = float.NegativeInfinity };
        Assert.Equal((float.NegativeInfinity, 0f), (plain.RubyOver, plain.RubyOverhang));
        Assert.Null(plain.Svg);
        Assert.Null(plain.ColumnRules);

        ColumnRule[] rules = [new(1, 2, 3, 4)];
        var column = new Fragment(null, 1, 1, []) { RubyOver = 5, ColumnRules = rules, RubyOverhang = 2, SvgClip = null };
        Assert.Equal((5f, 2f), (column.RubyOver, column.RubyOverhang));
        Assert.Same(rules, column.ColumnRules);
        Assert.Null(column.SvgClip);
    }

    [Fact]
    public void DeepDocumentsLayOutWithoutOverflowingTheStack()
    {
        var document = new DocumentNode();
        ContainerNode parent = document.AppendChild(document.CreateElement(Namespaces.Html, "html"));
        for (var i = 0; i < 20_000; i++)
            parent = parent.AppendChild(document.CreateElement(Namespaces.Html, "div"));
        StyleResolver.Resolve(document, new MediaContext(800, 600));

        var fragment = LayoutEngine.LayoutDocument(BoxTreeBuilder.Build(document)!, 800, 600, BoxFont.Value);

        Assert.Equal(800, fragment.Children[0].Fragment.Width);
    }

    /// <summary>The test box font for every generic family: every glyph is one em wide, ascent 0.8em, descent 0.2em.</summary>
    internal static readonly Lazy<FontCollection> BoxFont = new(() =>
    {
        var fonts = FontCollection.FromFolder(Path.Combine(AppContext.BaseDirectory, "fonts"));
        foreach (var generic in new[] { "serif", "sans-serif", "monospace", "system-ui", "emoji", "cursive", "fantasy" })
            fonts.GenericFamilies[generic] = ["Folio Box"];
        return fonts;
    });

    internal static Fragment LayOut(string html, float width = 800, float height = 600)
    {
        var document = TreeBuilder.Parse(html);
        StyleResolver.Resolve(document, new MediaContext(width, height), measure: InlineLayout.MeasureWith(BoxFont.Value));
        var images = new Folio.Imaging.ImageLoader(Folio.Resources.ResourceLoader.DataUrlsOnly, null);
        return LayoutEngine.LayoutDocument(BoxTreeBuilder.Build(document, images)!, width, height, BoxFont.Value, images: images);
    }

    // The fragment tree below the initial containing block, border boxes in page coordinates.
    internal static List<string> Dump(Fragment initialContainingBlock)
    {
        var lines = new List<string>();
        foreach (var child in initialContainingBlock.Children)
            DumpFragment(child, 0, 0, 0, lines);
        return lines;
    }

    private static void DumpFragment(ChildFragment placed, float parentX, float parentY, int depth, List<string> lines)
    {
        var (x, y, fragment) = (parentX + placed.X, parentY + placed.Y, placed.Fragment);
        var label = fragment.Kind switch
        {
            FragmentKind.Line => "line",
            FragmentKind.Text => $"text \"{CaseFiles.Escape(Text(fragment))}\"{(fragment.Text!.RightToLeft ? " rtl" : "")}",
            _ => Label(fragment.Box),
        };
        lines.Add($"{new string(' ', depth * 2)}{label} {N(x)},{N(y)} {N(fragment.Width)}x{N(fragment.Height)}");
        foreach (var child in fragment.Children)
            DumpFragment(child, x, y, depth + 1, lines);
    }

    private static string Text(Fragment fragment)
    {
        if (fragment.Text!.Replacement is { } replacement)
            return replacement;
        var (run, text) = (fragment.Text.Run, ((BlockContainerBox)fragment.Box!).Inline!.Text);
        var (start, end) = (run.Clusters[fragment.Text.GlyphStart], run.Clusters[fragment.Text.GlyphEnd - 1] + 1);
        return text[start..end];
    }

    private static string Label(Box? box) => box switch
    {
        { PseudoElement: not PseudoElement.None and var pe } => "::" + string.Concat(pe.ToString().Select((c, i) => i > 0 && char.IsUpper(c) ? "-" + char.ToLowerInvariant(c) : char.ToLowerInvariant(c).ToString())),
        { Node: ElementNode e } => e.LocalName + (e.GetAttribute("id") is { } id ? "#" + id : ""),
        _ => "(anonymous)",
    };

    private static string N(float value) => (Math.Round(value, 2) + 0.0).ToString(CultureInfo.InvariantCulture); // + 0.0 turns -0 into 0
}
