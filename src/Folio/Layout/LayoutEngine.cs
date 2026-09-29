namespace Folio.Layout;

/// <summary>Lays out a box tree: <c>Layout(box, ConstraintSpace) → Fragment</c> per formatting context (study 06).</summary>
internal static class LayoutEngine
{
    /// <summary>
    /// Lays out the root box in the initial containing block, a viewport-sized rectangle at the canvas origin
    /// (https://www.w3.org/TR/CSS22/visudet.html#containing-block-details). Returns the containing block's fragment.
    /// </summary>
    public static Fragment LayoutDocument(Box root, float viewportWidth, float viewportHeight)
    {
        var fragment = BlockLayout.Layout(root, new ConstraintSpace(viewportWidth, viewportHeight));
        // The root establishes a block formatting context, so its margins are its own.
        return new Fragment(null, viewportWidth, viewportHeight, [new(fragment.MarginLeft, fragment.TopMargins.Resolve(), fragment)]);
    }
}
