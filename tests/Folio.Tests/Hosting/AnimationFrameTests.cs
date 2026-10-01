using Folio.Painting;

namespace Folio.Tests.Hosting;

public class AnimationFrameTests
{
    private const string Fade =
        "<!DOCTYPE html><style>@keyframes f { from { opacity: 0; transform: translateX(0) } to { opacity: 1; transform: translateX(100px) } }" +
        " div { height: 20px; background: red; animation: f 1s linear infinite }</style><div></div>";

    private static float Opacity(DisplayList list) => list.Items.First(i => i.Kind == DisplayItemKind.PushLayer).Opacity;

    [Fact]
    public void APaintOnlyFrameRestylesWithoutLayingOutAgain()
    {
        using var document = Document.Parse(Fade);
        var (first, _) = document.Paint(800, 600, animationTime: 0.25);
        Assert.Equal(1, document.LayoutCount);
        Assert.Equal(0.25f, Opacity(first), 3);

        var frame = document.PaintFrame(0.5);

        Assert.NotNull(frame);
        Assert.Equal(0.5f, Opacity(frame), 3);
        Assert.Equal(1, document.LayoutCount);
    }

    [Fact]
    public void AFrameMatchesAFullPaintAtTheSameTime()
    {
        using var document = Document.Parse(Fade);
        document.Paint(800, 600, animationTime: 0);
        var frame = document.PaintFrame(0.7)!;

        using var fresh = Document.Parse(Fade);
        var (full, _) = fresh.Paint(800, 600, animationTime: 0.7);

        Assert.Equal(full.Items, frame.Items);
    }

    [Fact]
    public void AnAnimationThatChangesLayoutNeedsAFullPaint()
    {
        using var document = Document.Parse("<style>@keyframes m { to { margin-top: 40px } } div { animation: m 1s }</style><div>x</div>");
        document.Paint(800, 600, animationTime: 0);

        Assert.Null(document.PaintFrame(0.5));
    }

    [Fact]
    public void AnimationsRunUntilTheyEnd()
    {
        using var document = Document.Parse("<style>@keyframes f { to { opacity: 0 } } #a { animation: f 1s 0.5s } #b { animation: f 1s paused }</style><div id=a></div><div id=b></div>");
        document.Paint(800, 600, animationTime: 0);

        Assert.True(document.AnimationsRunning(0));
        Assert.True(document.AnimationsRunning(1.4));
        Assert.False(document.AnimationsRunning(1.5)); // the paused one never changes
    }

    [Fact]
    public void GeneratedContentAnimates()
    {
        const string html = "<!DOCTYPE html><style>@keyframes f { from { opacity: 0 } to { opacity: 1 } }" +
            " div::after { content: ''; display: block; height: 10px; background: red; animation: f 1s linear forwards }</style><div></div>";
        using var settled = Document.Parse(html);
        var (end, _) = settled.Paint(800, 600);
        Assert.DoesNotContain(end.Items, i => i.Kind == DisplayItemKind.PushLayer); // opacity 1: no layer

        using var document = Document.Parse(html);
        var (start, _) = document.Paint(800, 600, animationTime: 0.25);
        Assert.Equal(0.25f, Opacity(start), 3);
        var frame = document.PaintFrame(0.75);
        Assert.NotNull(frame);
        Assert.Equal(0.75f, Opacity(frame), 3);
        Assert.Equal(1, document.LayoutCount);
    }

    [Fact]
    public void NoAnimationsNoFrames()
    {
        using var document = Document.Parse("<p>still</p>");
        document.Paint(800, 600);

        Assert.False(document.AnimationsRunning(0));
    }
}
