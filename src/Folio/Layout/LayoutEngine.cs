using Folio.Typography;

namespace Folio.Layout;

/// <summary>Lays out a box tree: <c>Layout(box, ConstraintSpace) → Fragment</c> per formatting context (study 06).</summary>
internal static class LayoutEngine
{
    /// <summary>
    /// Lays out the root box in the initial containing block, a viewport-sized rectangle at the canvas origin
    /// (https://www.w3.org/TR/CSS22/visudet.html#containing-block-details). Returns the containing block's fragment.
    /// </summary>
    /// <param name="fonts">The fonts text is measured with; none means text is measured with fallback metrics.</param>
    /// <param name="shaper">The shaper for complex text; none means every run goes through SimpleShaper.</param>
    /// <param name="images">Where images the layout refers to load from; none means they do not load.</param>
    public static Fragment LayoutDocument(Box root, float viewportWidth, float viewportHeight, FontCollection? fonts = null, ITextShaper? shaper = null,
                                          Imaging.ImageLoader? images = null)
    {
        // The tree's children go into a few large buffers rather than an array per fragment (#397).
        using var arena = FragmentArena.Open();
        var context = new LayoutContext(fonts ?? new FontCollection(), shaper) { Images = images };
        var fragment = BlockLayout.Layout(root, new ConstraintSpace(viewportWidth, viewportHeight), context);
        // The root establishes a block formatting context, so its margins are its own.
        var (x, y) = (fragment.MarginLeft, fragment.TopMargins.Resolve());
        List<ChildFragment> children = [new(x, y, fragment)];

        // Positioned boxes with no positioned ancestor, and fixed ones, use the initial containing block (at scroll
        // offset zero, the viewport). Their own fixed descendants arrive here too.
        var pending = new Queue<OutOfFlowBox>(fragment.OutOfFlow.Select(o => o with { StaticX = o.StaticX + x, StaticY = o.StaticY + y }));
        while (pending.TryDequeue(out var o))
        {
            var placed = PositionedLayout.LayoutAbsolute(o.Box, viewportWidth, viewportHeight, o.StaticX, o.StaticY, context);
            children.Add(placed);
            foreach (var inner in placed.Fragment.OutOfFlow)
                pending.Enqueue(inner with { StaticX = inner.StaticX + placed.X, StaticY = inner.StaticY + placed.Y });
        }
        return new Fragment(null, viewportWidth, viewportHeight, children);
    }
}
