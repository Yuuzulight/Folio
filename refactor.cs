using System;
using System.IO;
using System.Text.RegularExpressions;

class Program
{
    static void Main()
    {
        string text = File.ReadAllText("src/Folio/Painting/DisplayListBuilder.cs");

        // 1. Fix Build method to call PaintOrderWalker
        text = text.Replace("var order = TreeOrder(root.Box!);", "var order = PaintOrderWalker.TreeOrder(root.Box!);");
        text = text.Replace("Collect(rootContext, rootContext, rootBox, root.Children, order);", "PaintOrderWalker.Collect(rootContext, rootContext, rootBox, root.Children, order);");
        text = text.Replace("Collect(rootContext, rootContext, new PaintBox(initialContainingBlock, 0, 0, null, Scale: deviceScale) { Around = atViewport },\r\n            initialContainingBlock.Children.Slice(1), order);", "PaintOrderWalker.Collect(rootContext, rootContext, new PaintBox(initialContainingBlock, 0, 0, null, Scale: deviceScale) { Around = atViewport },\r\n            initialContainingBlock.Children.Slice(1), order);");
        text = text.Replace("Collect(rootContext, rootContext, new PaintBox(initialContainingBlock, 0, 0, null, Scale: deviceScale) { Around = atViewport },\n            initialContainingBlock.Children.Slice(1), order);", "PaintOrderWalker.Collect(rootContext, rootContext, new PaintBox(initialContainingBlock, 0, 0, null, Scale: deviceScale) { Around = atViewport },\n            initialContainingBlock.Children.Slice(1), order);");
        
        text = text.Replace("emitter.Emit(rootContext);", "PaintOrderWalker.Walk(rootContext, emitter);");

        // 2. Rewrite Emitter
        int emitterStart = text.IndexOf("    private sealed class Emitter");
        int emitStart = text.IndexOf("        public void Emit(Context context)", emitterStart);
        int emitEnd = text.IndexOf("        // A box's transform", emitStart);

        string beforeEmitter = text.Substring(0, emitStart);
        string afterEmitter = text.Substring(emitEnd);

        string newEmitter = @"
        private record struct ContextState(
            bool Grouped, 
            bool InnerFilter, 
            int SvgFiltersCount, 
            Style.Mask? Mask, 
            bool Layered, 
            Fragment? ClipMask, 
            Vector2 ClipOrigin, 
            DisplayItem? ClipPath, 
            Matrix4x4? Transform, 
            int PreviousFloor, 
            PaintBox? Owner
        );
        private readonly Stack<ContextState> _states = new();

        public bool BeginContext(Context context)
        {
            var owner = context.Real ? context.Owner : null;
            // A list that references SVG filter elements is a layer per filter, laid out with the box; its opacity() stays a filter.
            var references = owner is not null && owner.Box.Style.Effects.Filter.HasReference;
            var svgFilters = references && owner!.Fragment.SvgFilters is { } chain ? SvgFilterPrimitives.Of(chain) : null;
            var (filters, filterOpacity) = owner is null || references ? (null, 1) : FilterPrimitives.ForLayer(owner.Box.Style.Effects.Filter, owner.Box.Style.Inherited.Color);
            var opacity = (owner is not null && owner.Box.Style.Box.Opacity < 1 ? owner.Box.Style.Box.Opacity : 1) * filterOpacity;
            var backdrop = owner is null ? null : FilterPrimitives.Of(owner.Box.Style.Effects.BackdropFilter, owner.Box.Style.Inherited.Color);
            var blend = owner is null ? BlendMode.Normal : Blend(owner.Box.Style.Effects.MixBlendMode);
            var mask = owner is not null && owner.Box.Style.Mask.IsMasked ? owner.Box.Style.Mask : null;
            var layered = opacity < 1 || filters is not null || svgFilters is not null || backdrop is not null || blend != BlendMode.Normal || context.Isolated || mask is not null;
            // A mask applies after the filter and before opacity, so with both the filter gets a layer of its own inside.
            var innerFilter = mask is not null && filters is not null;
            var transform = owner is not null && owner.Box.IsTransformed ? Transform(owner) : (Matrix4x4?)null;
            // A transform that cannot be inverted flattens the box to nothing: it and its content are not displayed
            // (https://www.w3.org/TR/css-transforms-1/#transform-function-lists).
            if (transform is { } singular && Determinant2D(singular) == 0)
                return false;
            // A url() reference to an SVG clipPath clips with its path when it is one plain shape; any other region is a
            // mask drawn over the box in a layer of its own. A reference to anything else clips nothing.
            // ponytail: a backdrop filter under such a mask sees only that layer, not what is behind the box.
            var svgClip = owner?.Fragment.SvgClip;
            var clipOrigin = owner is null ? default : new Vector2(BorderBox(owner).Rect.X, BorderBox(owner).Rect.Y);
            var clipPath = owner is null || owner.Box.Style.Effects.ClipPath.IsNone ? (DisplayItem?)null
                : owner.Box.Style.Effects.ClipPath.Url is null ? ClipPathItem(owner, owner.Box.Style.Effects.ClipPath)
                : svgClip is null ? null : SvgPainter.ClipItem(svgClip, clipOrigin);
            var clipMask = clipPath is null && owner?.Box.Style.Effects.ClipPath.Url is not null ? svgClip : null;
            var grouped = layered || transform is not null || clipPath is not null || clipMask is not null;
            var floor = _floor;
            // The clips outside stay open under the group; the transform, the clip path and the layer apply to the box and
            // all it holds. The clip path is in the box's coordinates and clips what the layer composites.
            // The layer filters it, then applies opacity (https://drafts.csswg.org/filter-effects-1/#placement) and blends
            // it; a backdrop filter is clipped to the border box (https://drafts.csswg.org/filter-effects-2/#backdrop-filter-operation).
            if (grouped)
            {
                SetClip(owner!.Clip);
                if (transform is { } matrix)
                    list.Items.Add(matrix is { M14: 0, M24: 0, M44: 1 }
                        ? new DisplayItem(DisplayItemKind.PushTransform, Transform: new Matrix3x2(matrix.M11, matrix.M12, matrix.M21, matrix.M22, matrix.M41, matrix.M42))
                        : new DisplayItem(DisplayItemKind.PushTransform, Projection: matrix));
                if (clipPath is { } clip)
                    list.Items.Add(clip);
                else if (clipMask is not null)
                    list.Items.Add(new DisplayItem(DisplayItemKind.PushLayer));
                if (layered)
                    list.Items.Add(new DisplayItem(DisplayItemKind.PushLayer, backdrop is null ? default : BorderBox(owner), Opacity: opacity,
                        Filters: innerFilter ? null : filters, Backdrop: backdrop, Blend: blend));
                if (innerFilter)
                    list.Items.Add(new DisplayItem(DisplayItemKind.PushLayer, Filters: filters));
                if (svgFilters is not null)
                {
                    // The filters are in the border box's coordinates: the layers start there, the content goes back.
                    list.Items.Add(new DisplayItem(DisplayItemKind.PushTransform, Transform: Matrix3x2.CreateTranslation(clipOrigin)));
                    for (var i = svgFilters.Count - 1; i >= 0; i--)
                        list.Items.Add(new DisplayItem(DisplayItemKind.PushLayer, Filters: svgFilters[i]));
                    list.Items.Add(new DisplayItem(DisplayItemKind.PushTransform, Transform: Matrix3x2.CreateTranslation(-clipOrigin)));
                }
                _floor = _open.Count;
            }

            _states.Push(new ContextState(grouped, innerFilter, svgFilters?.Count ?? 0, mask, layered, clipMask, clipOrigin, clipPath, transform, floor, owner));
            return true;
        }

        public void VisitBackground(PaintBox box, List<PaintBox> text) => PaintBackground(box, text);
        public void VisitCollapsedBorders(PaintBox table, List<PaintBox> cells) => PaintCollapsedBorders(table, cells); // We will rename PaintCollapsedBorders' signature
        public void VisitText(PaintBox text) => PaintText(text);
        public void VisitOutline(PaintBox box) => PaintOutline(box);

        public void EndContext(Context context)
        {
            var state = _states.Pop();
            if (state.Grouped)
            {
                PopTo(_floor);
                if (state.InnerFilter)
                    list.Items.Add(new DisplayItem(DisplayItemKind.Pop));
                for (var i = 0; i < state.SvgFiltersCount + 2 && state.SvgFiltersCount > 0; i++) // Actually original code was: for (var i = 0; svgFilters is not null && i < svgFilters.Count + 2; i++)
                    list.Items.Add(new DisplayItem(DisplayItemKind.Pop));
                if (state.Mask is not null)
                    PaintMask(state.Owner!, state.Mask);
                if (state.Layered)
                    list.Items.Add(new DisplayItem(DisplayItemKind.Pop));
                if (state.ClipMask is not null)
                    SvgPainter.PaintClipMask(state.ClipMask, state.ClipOrigin, list.Items);
                if (state.ClipPath is not null || state.ClipMask is not null)
                    list.Items.Add(new DisplayItem(DisplayItemKind.Pop));
                if (state.Transform is not null)
                    list.Items.Add(new DisplayItem(DisplayItemKind.Pop));
                _floor = state.PreviousFloor;
            }
        }
";
        text = beforeEmitter + newEmitter + "\n" + afterEmitter;

        // Add IPaintVisitor interface to Emitter class definition
        text = text.Replace("private sealed class Emitter(DisplayList list, Box? canvasBox, Imaging.ImageLoader? images)", "private sealed class Emitter(DisplayList list, Box? canvasBox, Imaging.ImageLoader? images) : IPaintVisitor");

        // We need to fix `PaintCollapsedBorders(PaintBox table, Context context)` to `PaintCollapsedBorders(PaintBox table, List<PaintBox> cells)`
        text = text.Replace("private void PaintCollapsedBorders(PaintBox table, Context context)", "private void PaintCollapsedBorders(PaintBox table, List<PaintBox> cells)");
        text = text.Replace("foreach (var cell in context.CollapsedCells[table])", "foreach (var cell in cells)");

        File.WriteAllText("src/Folio/Painting/DisplayListBuilder.cs", text);
    }
}
