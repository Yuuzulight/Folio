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

    private static readonly Dictionary<string, Func<string>> Pages = new()
    {
        ["saturate"] = () => Filtered("saturate(.5)"),
        ["blur"] = () => Filtered("blur(3px)"),
        ["dashboard"] = Dashboard,
        ["text"] = LongText,
        ["fixed-header"] = Header,
        ["overlay"] = Overlay,
        ["fixed-background"] = FixedBackground,
    };

    private static readonly Dictionary<(string, int), DisplayList> Lists = [];

    private static DisplayList ListOf(string page, int height)
    {
        lock (Lists)
        {
            if (Lists.TryGetValue((page, height), out var cached))
                return cached;
            var document = Document.Parse(Pages[page](), Options); // kept: the list is used by every case
            var (list, contentHeight) = document.Paint(800, height, 1, SoftwareRasterBackend.Instance.TextShaper, 0);
            Assert.True(contentHeight > 1300 + height, $"{page} must be taller than the scrolls: {contentHeight}");
            return Lists[(page, height)] = list!;
        }
    }

    // What FolioView does for a scroll: shift when possible, else repaint in full. True when it shifted.
    private static bool Scroll(DisplayList list, SoftwareRasterSurface surface, int from, int to)
    {
        if (RetainedScroll.TryScroll(list, surface, 1, from, to, White))
            return true;
        RetainedScroll.Repaint(list, surface, 1, to, White);
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
        var expected = page is not ("fixed-header" or "blur") && Math.Abs(delta) <= height - height / 8;
        Assert.Equal(expected, shifted);
    }

    [Fact]
    public void ABoxFixedToTheViewportMakesTheListRefuseToShift()
    {
        Assert.True(ListOf("fixed-header", 600).ViewportAnchored);
        Assert.False(ListOf("dashboard", 600).ViewportAnchored);
    }

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
