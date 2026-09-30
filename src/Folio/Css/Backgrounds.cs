namespace Folio.Css;

/// <summary>An <c>&lt;image&gt;</c>: none, a URL, or a gradient (kept as text until gradients are painted in M2).</summary>
internal abstract record ImageValue;

/// <summary>A single specified image (list-style-image).</summary>
internal sealed record ImageSpecified(ImageValue Image) : CssValue;

internal sealed record NoImage : ImageValue
{
    public static NoImage Instance { get; } = new();
    public override string ToString() => "none";
}

internal sealed record UrlImage(string Url) : ImageValue
{
    public override string ToString() => $"url(\"{Url}\")";
}

internal sealed record GradientImage(string Function, string Text) : ImageValue
{
    public override string ToString() => Text;
}

internal enum BackgroundRepeat
{
    Repeat,
    Space,
    Round,
    NoRepeat,
}

internal readonly record struct RepeatStyle(BackgroundRepeat X, BackgroundRepeat Y)
{
    public override string ToString() => (X, Y) switch
    {
        (BackgroundRepeat.Repeat, BackgroundRepeat.NoRepeat) => "repeat-x",
        (BackgroundRepeat.NoRepeat, BackgroundRepeat.Repeat) => "repeat-y",
        _ when X == Y => Name(X),
        _ => $"{Name(X)} {Name(Y)}",
    };

    private static string Name(BackgroundRepeat r) => r == BackgroundRepeat.NoRepeat ? "no-repeat" : r.ToString().ToLowerInvariant();
}

internal enum BackgroundAttachment
{
    Scroll,
    Fixed,
    Local,
}

internal enum BackgroundBox
{
    BorderBox,
    PaddingBox,
    ContentBox,
    Text,
}

internal enum BackgroundSizeKind
{
    Explicit,
    Cover,
    Contain,
}

/// <summary>One <c>&lt;bg-position&gt;</c>: each axis an offset from the start (left/top) or end (right/bottom) edge.</summary>
internal readonly record struct PositionSpecified(bool XFromEnd, CssValue X, bool YFromEnd, CssValue Y);

/// <summary>One <c>&lt;bg-size&gt;</c>; a null width or height is auto.</summary>
internal readonly record struct SizeSpecified(BackgroundSizeKind Kind, CssValue? Width, CssValue? Height);

/// <summary>A comma-separated list of per-layer values of one background longhand.</summary>
internal sealed record LayerListValue<T>(IReadOnlyList<T> Items) : CssValue;

/// <summary>Parsers for the background properties (https://www.w3.org/TR/css-backgrounds-3/#backgrounds).</summary>
internal static class BackgroundParsing
{
    public static ImageValue? Image(ValueReader r)
    {
        if (r.Keyword("none") is not null)
            return NoImage.Instance;
        if (r.Url() is { } url)
            return new UrlImage(url);
        foreach (var name in (string[])["linear-gradient", "radial-gradient", "conic-gradient", "repeating-linear-gradient", "repeating-radial-gradient", "repeating-conic-gradient"])
        {
            var mark = r.Mark;
            if (r.FunctionText(name) is { } text)
                return new GradientImage(name, text);
            r.Reset(mark);
        }
        return null;
    }

    // https://www.w3.org/TR/css-backgrounds-3/#typedef-bg-position
    public static PositionSpecified? Position(ValueReader r)
    {
        var items = new List<object>(); // keyword strings or length-percentage values
        var mark = r.Mark;
        while (items.Count < 4)
        {
            if (r.Keyword("left", "right", "top", "bottom", "center") is { } keyword)
                items.Add(keyword);
            else if (r.LengthPercentage() is { } lp)
                items.Add(lp);
            else
                break;
        }
        var result = Interpret(items);
        if (result is null)
            r.Reset(mark);
        return result;
    }

    private static readonly CssValue Start = new PercentageValue(0);
    private static readonly CssValue Center = new PercentageValue(50);

    private static PositionSpecified? Interpret(List<object> items)
    {
        static bool IsX(object o) => o is "left" or "right";
        static bool IsY(object o) => o is "top" or "bottom";

        switch (items.Count)
        {
            case 1:
                return items[0] switch
                {
                    CssValue v => new PositionSpecified(false, v, false, Center),
                    "center" => new PositionSpecified(false, Center, false, Center),
                    string k when IsX(k) => new PositionSpecified(k == "right", Start, false, Center),
                    string k => new PositionSpecified(false, Center, k == "bottom", Start),
                    _ => null,
                };
            case 2:
                var (a, b) = (items[0], items[1]);
                if (a is string && b is string && (IsY(a) || IsX(b)))
                    (a, b) = (b, a); // "top left"
                if (IsY(a) || IsX(b))
                    return null;
                return new PositionSpecified(a is "right", Axis(a), b is "bottom", Axis(b));
            case 3:
            case 4:
                // Keyword/offset pairs: "right 10px bottom 20px", "left 10px top", "center bottom 5px".
                var pairs = new List<(string Keyword, CssValue? Offset)>();
                for (var i = 0; i < items.Count; i++)
                {
                    if (items[i] is not string keyword)
                        return null;
                    CssValue? offset = null;
                    if (i + 1 < items.Count && items[i + 1] is CssValue v)
                    {
                        if (keyword == "center")
                            return null;
                        offset = v;
                        i++;
                    }
                    pairs.Add((keyword, offset));
                }
                if (pairs.Count != 2)
                    return null;
                var (first, second) = (pairs[0], pairs[1]);
                if (IsY(first.Keyword) || IsX(second.Keyword) || (first.Keyword == "center" && IsX(second.Keyword)))
                    (first, second) = (second, first);
                if (IsY(first.Keyword) || IsX(second.Keyword))
                    return null;
                return new PositionSpecified(first.Keyword == "right", first.Offset ?? Axis(first.Keyword),
                                             second.Keyword == "bottom", second.Offset ?? Axis(second.Keyword));
            default:
                return null;
        }
    }

    private static CssValue Axis(object item) => item switch
    {
        CssValue v => v,
        "center" => Center,
        _ => Start,
    };

    // https://www.w3.org/TR/css-backgrounds-3/#typedef-bg-size
    public static SizeSpecified? Size(ValueReader r)
    {
        if (r.Keyword("cover") is not null)
            return new SizeSpecified(BackgroundSizeKind.Cover, null, null);
        if (r.Keyword("contain") is not null)
            return new SizeSpecified(BackgroundSizeKind.Contain, null, null);
        var width = SizeComponent(r);
        if (width is null)
            return null;
        var height = SizeComponent(r);
        return new SizeSpecified(BackgroundSizeKind.Explicit, width is KeywordValue ? null : width, height is null or KeywordValue ? null : height);
    }

    private static CssValue? SizeComponent(ValueReader r) =>
        r.Keyword("auto") is not null ? new KeywordValue("auto") : r.LengthPercentage(nonNegative: true);

    // https://www.w3.org/TR/css-backgrounds-3/#typedef-repeat-style
    public static RepeatStyle? Repeat(ValueReader r)
    {
        if (r.Keyword("repeat-x") is not null)
            return new RepeatStyle(BackgroundRepeat.Repeat, BackgroundRepeat.NoRepeat);
        if (r.Keyword("repeat-y") is not null)
            return new RepeatStyle(BackgroundRepeat.NoRepeat, BackgroundRepeat.Repeat);
        if (RepeatKeyword(r) is not { } x)
            return null;
        return new RepeatStyle(x, RepeatKeyword(r) ?? x);
    }

    private static BackgroundRepeat? RepeatKeyword(ValueReader r) => r.Keyword("repeat", "space", "round", "no-repeat") switch
    {
        "repeat" => BackgroundRepeat.Repeat,
        "space" => BackgroundRepeat.Space,
        "round" => BackgroundRepeat.Round,
        "no-repeat" => BackgroundRepeat.NoRepeat,
        _ => null,
    };

    public static BackgroundAttachment? Attachment(ValueReader r) => r.Keyword("scroll", "fixed", "local") switch
    {
        "scroll" => BackgroundAttachment.Scroll,
        "fixed" => BackgroundAttachment.Fixed,
        "local" => BackgroundAttachment.Local,
        _ => null,
    };

    public static BackgroundBox? Box(ValueReader r, bool allowText) =>
        (allowText ? r.Keyword("border-box", "padding-box", "content-box", "text") : r.Keyword("border-box", "padding-box", "content-box")) switch
        {
            "border-box" => BackgroundBox.BorderBox,
            "padding-box" => BackgroundBox.PaddingBox,
            "content-box" => BackgroundBox.ContentBox,
            "text" => BackgroundBox.Text,
            _ => null,
        };

    /// <summary>A comma-separated list of one grammar, as each background longhand takes.</summary>
    public static CssValue? List<T>(ValueReader r, Func<ValueReader, T?> item) where T : struct
    {
        var items = new List<T>();
        do
        {
            if (item(r) is not { } value)
                return null;
            items.Add(value);
        }
        while (r.Comma());
        return r.AtEnd ? new LayerListValue<T>(items) : null;
    }

    public static CssValue? ImageList(ValueReader r)
    {
        var items = new List<ImageValue>();
        do
        {
            if (Image(r) is not { } image)
                return null;
            items.Add(image);
        }
        while (r.Comma());
        return r.AtEnd ? new LayerListValue<ImageValue>(items) : null;
    }

    /// <summary>
    /// The <c>background</c> shorthand (https://www.w3.org/TR/css-backgrounds-3/#background): comma-separated layers of
    /// image || position [/ size] || repeat || attachment || box || box, with the colour allowed in the last layer.
    /// </summary>
    public static List<(PropertyId, CssValue)>? Shorthand(ValueReader r)
    {
        var images = new List<ImageValue>();
        var positions = new List<PositionSpecified>();
        var sizes = new List<SizeSpecified>();
        var repeats = new List<RepeatStyle>();
        var attachments = new List<BackgroundAttachment>();
        var origins = new List<BackgroundBox>();
        var clips = new List<BackgroundBox>();
        CssValue? color = null;

        var more = true;
        while (more)
        {
            if (color is not null)
                return null; // the colour may only appear in the last layer
            ImageValue? image = null;
            PositionSpecified? position = null;
            SizeSpecified? size = null;
            RepeatStyle? repeat = null;
            BackgroundAttachment? attachment = null;
            var boxes = new List<BackgroundBox>();
            var any = false;
            while (!r.AtEnd && !r.PeekComma())
            {
                any = true;
                if (image is null && Image(r) is { } i)
                {
                    image = i;
                }
                else if (position is null && Position(r) is { } p)
                {
                    position = p;
                    if (r.Delim('/'))
                    {
                        if (Size(r) is not { } s)
                            return null;
                        size = s;
                    }
                }
                else if (repeat is null && Repeat(r) is { } rep)
                {
                    repeat = rep;
                }
                else if (attachment is null && Attachment(r) is { } att)
                {
                    attachment = att;
                }
                else if (boxes.Count < 2 && Box(r, allowText: true) is { } box)
                {
                    boxes.Add(box);
                }
                else if (color is null && r.ColorSpecified() is { } c)
                {
                    color = c;
                }
                else
                {
                    return null;
                }
            }
            if (!any)
                return null;

            images.Add(image ?? NoImage.Instance);
            positions.Add(position ?? new PositionSpecified(false, Start, false, Start));
            sizes.Add(size ?? new SizeSpecified(BackgroundSizeKind.Explicit, null, null));
            repeats.Add(repeat ?? new RepeatStyle(BackgroundRepeat.Repeat, BackgroundRepeat.Repeat));
            attachments.Add(attachment ?? BackgroundAttachment.Scroll);
            // One box sets both origin and clip; two set origin then clip.
            origins.Add(boxes.Count > 0 ? boxes[0] : BackgroundBox.PaddingBox);
            clips.Add(boxes.Count > 1 ? boxes[1] : boxes.Count == 1 ? boxes[0] : BackgroundBox.BorderBox);
            if (boxes.Count > 0 && boxes[0] == BackgroundBox.Text && boxes.Count == 1)
                return null; // text is a clip value only
            more = r.Comma();
        }

        return
        [
            (PropertyId.BackgroundColor, color ?? new ColorValue(CssColor.Transparent)),
            (PropertyId.BackgroundImage, new LayerListValue<ImageValue>(images)),
            (PropertyId.BackgroundPosition, new LayerListValue<PositionSpecified>(positions)),
            (PropertyId.BackgroundSize, new LayerListValue<SizeSpecified>(sizes)),
            (PropertyId.BackgroundRepeat, new LayerListValue<RepeatStyle>(repeats)),
            (PropertyId.BackgroundAttachment, new LayerListValue<BackgroundAttachment>(attachments)),
            (PropertyId.BackgroundOrigin, new LayerListValue<BackgroundBox>(origins)),
            (PropertyId.BackgroundClip, new LayerListValue<BackgroundBox>(clips)),
        ];
    }
}
