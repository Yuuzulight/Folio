using System.Text;
using Folio.Painting;

namespace Folio.Tests.Painting;

// Scrolling by shifting the rendered pixels and painting the exposed rows must give exactly the bytes of a full repaint.
public class RetainedScrollTests
{
    private static readonly Paint White = new(new Rgba(1, 1, 1, 1));
    private static readonly int[] Deltas = [1, 3, 17, 100, 599];

    private static readonly FolioOptions Options = new()
    {
        Fonts = new Folio.Typography.FontSettings
        {
            Source = new Folio.Typography.FontFolderSource(Path.Combine(AppContext.BaseDirectory, "fonts")),
            GenericFamilies = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase) { ["sans-serif"] = ["Source Sans 3", "Folio Box"] },
        },
    };

    private static string Dashboard()
    {
        var cards = new StringBuilder();
        for (var i = 0; i < 60; i++)
        {
            cards.Append($"<div class=card><h3>Metric {i + 1}</h3><p class=value>{i * 7919 % 10000:N0}</p><div class=bars>");
            for (var b = 0; b < 4; b++)
                cards.Append($"<i style='height: {20 + (i * 13 + b * 29) % 60}px'></i>");
            cards.Append("</div><span class=badge>live</span></div>");
        }
        return $$"""
            <!DOCTYPE html><style>
            body { margin: 0; padding: 20px; font-family: sans-serif; background: #f4f6fb; }
            .grid { display: grid; grid-template-columns: repeat(4, 1fr); gap: 14px; }
            .card { position: relative; background: #fff; border-radius: 12px; padding: 12px; box-shadow: 0 2px 8px rgba(0,0,0,.08); }
            .value { font-size: 22px; font-weight: 700; margin: 4px 0; }
            .bars { display: flex; align-items: flex-end; gap: 4px; height: 80px; }
            .bars i { flex: 1; background: linear-gradient(#7b7bf0, #5b5bd6); border-radius: 3px 3px 0 0; }
            .badge { position: absolute; top: 10px; right: 10px; padding: 1px 6px; border-radius: 9px; color: #fff; font-size: 11px; background: #16a36a; }
            </style><div class=grid>{{cards}}</div>
            """;
    }

    private static string LongText()
    {
        var text = new StringBuilder("<!DOCTYPE html><body style='margin: 0; padding: 16px; font: 15px/1.4 sans-serif'>");
        for (var i = 0; i < 80; i++)
            text.Append($"<h2>Section {i}</h2><p>Lorem ipsum dolor sit amet, <b>consectetur</b> adipiscing elit, <a href=#>sed do</a> eiusmod tempor incididunt ut labore et dolore magna aliqua. Ut enim ad minim veniam, quis nostrud exercitation ullamco laboris nisi ut aliquip ex ea commodo consequat. {i}</p>");
        return text.ToString();
    }

    private static string Header() => LongText().Replace("<body style='margin: 0; padding: 16px; font: 15px/1.4 sans-serif'>",
        "<body style='margin: 0; padding: 16px; font: 15px/1.4 sans-serif'><div style='position: fixed; top: 0; left: 0; right: 0; height: 40px; background: rgba(20,20,80,.9); color: #fff'>Fixed header</div>" +
        "<div style='position: sticky; top: 40px; background: #eef; padding: 4px'>Sticky bar</div>");

    private static string Overlay()
    {
        var text = new StringBuilder("<!DOCTYPE html><body style='margin: 0; font: 14px sans-serif'>");
        for (var i = 0; i < 90; i++) // translucent boxes that overlap each other, so their edges cross every row
            text.Append($"<div style='height: 37px; margin-bottom: -11px; background: rgba({i * 40 % 255},{i * 90 % 255},{i * 150 % 255},.35); border-radius: 6px; padding-left: {i % 7 * 9}px'>Overlay {i}</div>");
        text.Append("<div style='height: 300px'></div>");
        return text.ToString();
    }

    private static string FixedBackground()
    {
        var text = new StringBuilder("<!DOCTYPE html><body style=\"margin: 0; font: 15px sans-serif; background: linear-gradient(#fde, #def) fixed; background-attachment: fixed\">");
        for (var i = 0; i < 70; i++)
            text.Append($"<p style='margin: 0; padding: 20px'>Paragraph {i} over a fixed background that stays put.</p>");
        return text.ToString();
    }

    // Bars the filter touches: saturate works on one pixel at a time (shifts), blur reaches its neighbours (does not).
    private static string Filtered(string filter)
    {
        var text = new StringBuilder("<!DOCTYPE html><body style='margin: 0; font: 14px sans-serif'>");
        for (var i = 0; i < 70; i++)
            text.Append($"<div style='height: 30px; margin: 9px 20px; width: {200 + i * 7 % 300}px; background: #5b5bd6; border-radius: 8px; filter: {filter}'>Bar {i}</div>");
        return text.ToString();
    }

    private static string Prefixed(string anchored) => LongText().Replace("<body style='margin: 0; padding: 16px; font: 15px/1.4 sans-serif'>",
        "<body style='margin: 0; padding: 16px; font: 15px/1.4 sans-serif'>" + anchored);

    // Ink that reaches far from where its text is: big type, long descenders, emoji, a shadow.
    private static string BigGlyphs()
    {
        var text = new StringBuilder("<!DOCTYPE html><body style='margin: 0; font-family: sans-serif'>");
        for (var i = 0; i < 40; i++)
            text.Append($"<div style='font-size: {60 + i % 5 * 20}px; line-height: 0.8; text-shadow: 0 {i % 3 * 6}px 4px rgba(0,0,0,.5); padding-left: {i * 11 % 90}px'>gypQÅé 😀 j{i}</div>");
        return text.ToString();
    }

    // Everything that is not a plain box or a line of text: transforms, every kind of gradient, images, SVG, shadows, odd sizes.
    private const string Png = "iVBORw0KGgoAAAANSUhEUgAAAAIAAAACCAYAAABytg0kAAAAFklEQVR4nGP8z8Dwn4GBgYGJAQoAHhgCAh6X4CYAAAAASUVORK5CYII=";

    private static string Mixed()
    {
        var text = new StringBuilder("<!DOCTYPE html><body style='margin: 0; padding: 9px; font: 13.3px/1.37 sans-serif; background: linear-gradient(170deg, #fdf, #dfe)'>");
        for (var i = 0; i < 70; i++)
        {
            text.Append($"<div style='display: flex; gap: 7.3px; margin: 5.7px 0; align-items: center'>");
            text.Append($"<div style='width: {41.3 + i % 5}px; height: {23.7 + i % 3}px; transform: rotate({i * 7 % 40 - 20}deg); background: radial-gradient(circle at 30% 40%, #f66, #36f 70%); border-radius: {i % 4 * 3.3}px / 9px'></div>");
            text.Append($"<div style='width: 40.7px; height: 31.1px; background: conic-gradient(from {i * 11}deg, #f90, #09f, #f90); border-radius: 50%'></div>");
            text.Append($"<div style='width: 50px; height: 28px; background: repeating-linear-gradient({i * 9}deg, #333 0 3px, #ccc 3px 7px)'></div>");
            text.Append($"<div style='width: 37px; height: 29px; background: url(data:image/png;base64,{Png}) center / {19 + i % 7}px 13px; image-rendering: auto; box-shadow: 0 {i % 4 + 1}px {i % 6 + 2}px {i % 3}px rgba(0,0,0,.4)'></div>");
            text.Append($"<img src='data:image/png;base64,{Png}' width={17 + i % 9} height=21 style='transform: scale({1 + i % 5 * .13})'>");
            text.Append($"<svg width=44 height=30 viewBox='0 0 44 30'><defs><linearGradient id=g{i}><stop offset=0 stop-color=red /><stop offset=1 stop-color=blue /></linearGradient></defs><path d='M2 28 Q 22 {-5 + i % 9} 42 28 Z' fill='url(#g{i})' stroke=#000 stroke-width=1.3 /></svg>");
            text.Append($"<span style='text-shadow: 0 1px 2px rgba(0,0,0,.5); opacity: .{5 + i % 5}; font-size: {11 + i % 4 * 1.7}px; border: 1.3px dashed #a50; padding: 1.9px 5.1px'>Item {i} gypQ</span>");
            text.Append("</div>");
        }
        return text.Append("<div style='height: 1500px'></div>").ToString();
    }

    // The dashboard under prefers-color-scheme: dark (the options for this page report a dark scheme).
    private static string Dark() => Dashboard().Replace("</style>", "@media (prefers-color-scheme: dark) { body { background: #101018; color: #ddd } .card { background: #1c1c28; box-shadow: 0 2px 8px rgba(0,0,0,.6) } }</style>");

    // overflow: hidden with rounded corners, children that run past the clip and sit on the rows where the viewport edge falls.
    private static string RoundedClips()
    {
        var text = new StringBuilder("<!DOCTYPE html><body style='margin: 0; padding: 0 12px; font: 14px sans-serif'>");
        for (var i = 0; i < 70; i++)
            text.Append($"<div style='overflow: hidden; border-radius: {10 + i % 4 * 5.5}px; height: {37.3 + i % 3}px; margin: 7.7px 0; position: relative; background: #eef'>" +
                $"<div style='position: absolute; left: -20px; right: -20px; top: -9px; bottom: -9px; background: linear-gradient({i * 17}deg, #f66, #36f)'></div>" +
                $"<span style='position: relative; padding: 0 {i % 6 * 8}px'>Clip {i} gypQ</span></div>");
        return text.Append("<div style='height: 1500px'></div>").ToString();
    }

    // Scrollers inside the page, each scrolled by a fractional amount (ScrollTop is set in ListOf).
    private static string NestedScrollers()
    {
        var text = new StringBuilder("<!DOCTYPE html><body style='margin: 0; padding: 10px; font: 13.3px/1.37 sans-serif'>");
        for (var i = 0; i < 12; i++)
        {
            text.Append($"<div id=s{i} style='overflow: auto; height: 143.7px; width: 420px; margin: 11.3px 0; border-radius: 9px; background: #f4f6fb'>");
            for (var j = 0; j < 14; j++)
                text.Append($"<div style='height: 24.6px; margin: 3.1px; background: linear-gradient(90deg, #7b7bf0, #5b5bd6); color: #fff; border-radius: 5px'>Row {i}.{j}</div>");
            text.Append("</div>");
        }
        return text.Append("<div style='height: 1500px'></div>").ToString();
    }

    private static readonly Dictionary<string, Func<string>> Pages = new()
    {
        ["dark"] = Dark, ["rounded-clip"] = RoundedClips, ["nested-scroll"] = NestedScrollers,
        ["mixed"] = Mixed,
        ["fixed-text"] = () => Prefixed("<div style='position: fixed; top: 10px; left: 10px; color: red'>No background here</div>"),
        ["fixed-border"] = () => Prefixed("<div style='position: fixed; top: 50px; left: 300px; width: 80px; height: 30px; border: 3px solid green'></div>"),
        ["fixed-svg"] = () => Prefixed("<svg style='position: fixed; bottom: 20px; right: 20px' width=60 height=40><rect width=60 height=40 fill=teal /></svg>"),
        ["sticky"] = () => Prefixed("<p style='position: sticky; top: 0'>sticky text only</p>"),
        ["fixed-nested"] = () => Prefixed("<div style='overflow: auto; height: 200px; width: 300px; position: relative'><div style='position: fixed; top: 5px'>inside a scroller</div><div style='height: 900px'>x</div></div>"),
        // "emoji" drops the text-shadow on purpose: a blurred shadow can differ by one level between a shift and a repaint
        // (see ShadowBlurErrorIsBoundedToOneLevelAndDoesNotAccumulate), and these cases assert exact bytes, so this page keeps only exact ink.
        // "glyphs" keeps the shadow and is checked against the one-level bound instead.
        ["glyphs"] = BigGlyphs, ["emoji"] = () => BigGlyphs().Replace("text-shadow", "outline-offset"), ["emoji-free-glyphs"] = () => BigGlyphs().Replace("😀", "e"),
        ["saturate"] = () => Filtered("saturate(.5)"),
        ["blur"] = () => Filtered("blur(3px)"),
        ["dashboard"] = Dashboard,
        ["text"] = LongText,
        ["fixed-header"] = Header,
        ["overlay"] = Overlay,
        ["fixed-background"] = FixedBackground,
    };

    private static readonly FolioOptions DarkOptions = new()
    {
        Fonts = Options.Fonts,
        ColorScheme = ColorScheme.Dark,
    };

    private static readonly Dictionary<(string, int, float), DisplayList> Lists = [];

    private static DisplayList ListOf(string page, int height, float scale = 1)
    {
        lock (Lists)
        {
            if (Lists.TryGetValue((page, height, scale), out var cached))
                return cached;
            var document = Document.Parse(Pages[page](), page == "dark" ? DarkOptions : Options); // kept: the list is used by every case
            if (page == "nested-scroll")
            {
                document.Paint(800 / scale, height / scale, scale, SoftwareRasterBackend.Instance.TextShaper, 0); // lay out so the scrollers have something to scroll
                for (var i = 0; i < 12; i++)
                {
                    var scroller = document.GetElementById($"s{i}")!;
                    scroller.ScrollTop = 37.5f + i;
                    Assert.True(scroller.ScrollTop > 0, "the nested scroller must be scrolled");
                }
            }
            var (list, contentHeight) = document.Paint(800 / scale, height / scale, scale, SoftwareRasterBackend.Instance.TextShaper, 0);
            Assert.True(contentHeight * scale > 1300 + height, $"{page} must be taller than the scrolls: {contentHeight}");
            return Lists[(page, height, scale)] = list!;
        }
    }

    // What FolioView does for a scroll: shift when possible, else repaint in full. True when it shifted.
    private static bool Scroll(DisplayList list, SoftwareRasterSurface surface, int from, int to, float scale = 1)
    {
        if (RetainedScroll.TryScroll(list, surface, scale, from, to, White))
            return true;
        RetainedScroll.Repaint(list, surface, scale, to, White);
        return false;
    }

    public static IEnumerable<object[]> Cases() =>
        from page in Pages.Keys from height in new[] { 600, 1000 } from delta in Deltas from sign in new[] { 1, -1 }
        select new object[] { page, height, delta * sign };

    [Theory]
    [MemberData(nameof(Cases))]
    public void AScrollIsByteIdenticalToAFullRepaint(string page, int height, int delta)
    {
        var list = ListOf(page, height);
        const int from = 700;
        using var scrolled = new SoftwareRasterSurface(800, height);
        using var fresh = new SoftwareRasterSurface(800, height);
        RetainedScroll.Repaint(list, scrolled, 1, from, White);
        var shifted = Scroll(list, scrolled, from, from + delta);
        RetainedScroll.Repaint(list, fresh, 1, from + delta, White);

        Assert.True(fresh.PremultipliedPixels.AsSpan().SequenceEqual(scrolled.PremultipliedPixels), $"{page} {height} {delta:+0;-0}: pixels differ");
        var expected = page is not ("fixed-header" or "fixed-text" or "fixed-border" or "fixed-svg" or "sticky" or "fixed-nested" or "blur") && Math.Abs(delta) <= height - height / 8;
        Assert.Equal(expected, shifted);
    }

    [Fact]
    public void ABoxFixedToTheViewportMakesTheListRefuseToShift()
    {
        foreach (var page in new[] { "fixed-header", "fixed-text", "fixed-border", "fixed-svg", "sticky", "fixed-nested" })
            Assert.True(ListOf(page, 600).ViewportAnchored, page);
        Assert.False(ListOf("dashboard", 600).ViewportAnchored);
    }

    private static string Differences(SoftwareRasterSurface a, SoftwareRasterSurface b)
    {
        var (x, y) = (a.PremultipliedPixels, b.PremultipliedPixels);
        var wrong = Enumerable.Range(0, x.Length).Where(i => x[i] != y[i]).ToList();
        return $"{wrong.Count} bytes differ, by at most {wrong.Select(i => Math.Abs(x[i] - y[i])).DefaultIfEmpty(0).Max()}";
    }

    // Windows scales: 100 to 200% in steps of 25 and a few of the others a monitor can be set to.
    public static IEnumerable<object[]> ScaledCases() =>
        from page in new[] { "dashboard", "text", "emoji", "overlay", "mixed", "saturate" } from scale in new[] { 1.1f, 1.25f, 1.5f, 1.75f, 2f, 2.25f, 2.5f }
        from delta in new[] { 1, 3, 17, 101, 333 } from sign in new[] { 1, -1 }
        select new object[] { page, scale, delta * sign };

    // The control scrolls in whole device pixels at any scale; odd offsets and fractional scales must shift and agree with a repaint too.
    // (What the page holds is meant to be hard: curves, rotations, every gradient, images, SVG, colour emoji at the scale's own strikes.)
    [Theory]
    [MemberData(nameof(ScaledCases))]
    public void AScrollAtAnotherScaleIsByteIdenticalToAFullRepaint(string page, float scale, int delta)
    {
        var list = ListOf(page, 600, scale);
        const int from = 701;
        using var scrolled = new SoftwareRasterSurface(800, 600);
        using var fresh = new SoftwareRasterSurface(800, 600);
        RetainedScroll.Repaint(list, scrolled, scale, from, White);
        Assert.True(Scroll(list, scrolled, from, from + delta, scale), $"{page} x{scale} {delta:+0;-0} was repainted, not shifted");
        RetainedScroll.Repaint(list, fresh, scale, from + delta, White);
        Assert.True(fresh.PremultipliedPixels.AsSpan().SequenceEqual(scrolled.PremultipliedPixels), $"{page} x{scale} {delta:+0;-0}: {Differences(fresh, scrolled)}");
    }

    // Drawing with the offset taken off last and drawing with it in the matrix are the same picture to within float noise,
    // and the same bytes for what is not on a rounding tie: this is the equivalence the golden tests rely on at offset 0.
    private static int MaxDifference(SoftwareRasterSurface a, SoftwareRasterSurface b)
    {
        var (x, y) = (a.PremultipliedPixels, b.PremultipliedPixels);
        var most = 0;
        for (var i = 0; i < x.Length; i++)
            most = Math.Max(most, Math.Abs(x[i] - y[i]));
        return most;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(333)]
    public void ScrollingTheCanvasDrawsWhatTranslatingItDraws(int top)
    {
        var list = ListOf("dashboard", 600, 1.25f);
        using var scrolled = new SoftwareRasterSurface(800, 600);
        using var translated = new SoftwareRasterSurface(800, 600);
        RetainedScroll.Repaint(list, scrolled, 1.25f, top, White);
        var canvas = translated.Canvas;
        canvas.Save();
        canvas.FillRoundedRect(new RoundedRect(new RectF(0, 0, 800, 600), default), White);
        canvas.Transform(RetainedScroll.Transform(1.25f, top));
        DisplayListPlayer.Replay(list, canvas, new RectF(0, top / 1.25f, 640, 480));
        canvas.Restore();
        // Float noise in the matrix can move a rounding tie by one level; anything more is a real difference.
        Assert.True(MaxDifference(translated, scrolled) <= 1, $"top {top}: {Differences(translated, scrolled)}");
        if (top == 0)
            Assert.True(translated.PremultipliedPixels.AsSpan().SequenceEqual(scrolled.PremultipliedPixels), Differences(translated, scrolled));
    }

    // The one known inexactness: a blur of 2px or more (a soft shadow) is box passes with running sums, so a shifted row can
    // differ from a repainted one by a level. It must stay that one level, in a single shift and over many, since a row is painted once.
    [Fact]
    public void ShadowBlurErrorIsBoundedToOneLevelAndDoesNotAccumulate()
    {
        const float scale = 1.5f;
        var list = ListOf("glyphs", 600, scale); // 4px text-shadow
        using var scrolled = new SoftwareRasterSurface(800, 600);
        using var fresh = new SoftwareRasterSurface(800, 600);
        foreach (var delta in new[] { 1, 17, 101, -333 })
        {
            RetainedScroll.Repaint(list, scrolled, scale, 701, White);
            Assert.True(Scroll(list, scrolled, 701, 701 + delta, scale));
            RetainedScroll.Repaint(list, fresh, scale, 701 + delta, White);
            Assert.True(MaxDifference(fresh, scrolled) <= 1, $"shadow {delta:+0;-0}: {Differences(fresh, scrolled)}");
        }

        foreach (var (page, pageScale) in new[] { ("glyphs", 1.5f), ("mixed", 1.25f) })
        {
            var many = ListOf(page, 600, pageScale);
            var random = new Random(11);
            var top = 400;
            RetainedScroll.Repaint(many, scrolled, pageScale, top, White);
            for (var i = 0; i < 200; i++)
            {
                var next = Math.Clamp(top + random.Next(-40, 41), 0, 1300);
                if (next == top)
                    continue;
                Assert.True(Scroll(many, scrolled, top, next, pageScale));
                top = next;
            }
            RetainedScroll.Repaint(many, fresh, pageScale, top, White);
            Assert.True(MaxDifference(fresh, scrolled) <= 1, $"{page} x{pageScale}: drifted, {Differences(fresh, scrolled)}");
        }
    }

    // The window moves to another monitor mid-scroll: the old pixels are at the old scale, so the view repaints in full at the new one, then scrolls on.
    [Fact]
    public void AScaleChangeMidScrollRepaintsInFullAndMatches()
    {
        using var surface = new SoftwareRasterSurface(800, 600);
        using var fresh = new SoftwareRasterSurface(800, 600);
        var before = ListOf("dashboard", 600, 1.25f);
        RetainedScroll.Repaint(before, surface, 1.25f, 400, White);
        Assert.True(Scroll(before, surface, 400, 450, 1.25f));
        var after = ListOf("dashboard", 600, 1.75f);
        RetainedScroll.Repaint(after, surface, 1.75f, 450, White); // the list and scale changed: nothing of the old frame is reusable
        RetainedScroll.Repaint(after, fresh, 1.75f, 450, White);
        Assert.True(fresh.PremultipliedPixels.AsSpan().SequenceEqual(surface.PremultipliedPixels), $"after the change: {Differences(fresh, surface)}");
        Assert.True(Scroll(after, surface, 450, 497, 1.75f));
        RetainedScroll.Repaint(after, fresh, 1.75f, 497, White);
        Assert.True(fresh.PremultipliedPixels.AsSpan().SequenceEqual(surface.PremultipliedPixels), $"scrolling on: {Differences(fresh, surface)}");
    }

    public static IEnumerable<object[]> MoreCases() =>
        from page in new[] { "dark", "rounded-clip", "nested-scroll" } from scale in new[] { 1f, 1.25f, 1.5f, 2.25f }
        from delta in new[] { 1, 17, 333, 520 } from sign in new[] { 1, -1 }
        select new object[] { page, scale, delta * sign };

    // Dark scheme, clipped rounded children and nested scrollers at a fractional offset, including offsets close to the shift limit (520 of 600).
    [Theory]
    [MemberData(nameof(MoreCases))]
    public void ScrollsOfMorePagesAreByteIdenticalToAFullRepaint(string page, float scale, int delta) =>
        AScrollAtAnotherScaleIsByteIdenticalToAFullRepaint(page, scale, delta);

    [Fact]
    public void ManySmallScrollsDoNotDrift()
    {
        foreach (var page in new[] { "dashboard", "text", "overlay" })
        {
            var list = ListOf(page, 600);
            var random = new Random(7);
            using var scrolled = new SoftwareRasterSurface(800, 600);
            using var fresh = new SoftwareRasterSurface(800, 600);
            var top = 300;
            RetainedScroll.Repaint(list, scrolled, 1, top, White);
            for (var i = 0; i < 200; i++)
            {
                var next = Math.Clamp(top + random.Next(-40, 41), 0, 1300);
                if (next == top)
                    continue;
                Assert.True(Scroll(list, scrolled, top, next));
                top = next;
            }
            RetainedScroll.Repaint(list, fresh, 1, top, White);
            Assert.True(fresh.PremultipliedPixels.AsSpan().SequenceEqual(scrolled.PremultipliedPixels), $"{page}: pixels drifted");
        }
    }

    [Fact]
    public void ShiftingMovesRowsWithOverlapInBothDirections()
    {
        using var surface = new SoftwareRasterSurface(2, 5);
        var pixels = surface.PremultipliedPixels;
        for (var row = 0; row < 5; row++)
            for (var i = 0; i < 8; i++)
                pixels[row * 8 + i] = (byte)(row + 1);

        surface.ShiftRows(2);
        Assert.Equal([3, 4, 5, 4, 5], Enumerable.Range(0, 5).Select(r => (int)pixels[r * 8]));
        surface.ShiftRows(-1);
        Assert.Equal([3, 3, 4, 5, 4], Enumerable.Range(0, 5).Select(r => (int)pixels[r * 8]));
    }
}
