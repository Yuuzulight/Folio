using Folio.Painting;
using Folio.Scripting.Dom;
using Folio.Scripting.Jint;
using Folio.Skia;
using Folio.Typography;

namespace Folio.Scripting.Tests;

/// <summary>
/// The canvas element and its 2D context (issues #502, #504 to #510): drawing is checked by reading the bitmap back, so the
/// tests exercise the context, the raster backend and <c>getImageData</c> together.
/// </summary>
public class CanvasTests
{
    /// <summary>The raster backend the canvases draw with; <see cref="SoftwareCanvasTests"/> runs the same tests on Folio's own.</summary>
    protected virtual IRasterBackend CreateRaster() => new SkiaRasterBackend();

    private bool UsesSoftwareRaster => CreateRaster() is SoftwareRasterBackend;

    private static string FontsFolder()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "tests", "fonts")))
                return Path.Combine(dir.FullName, "tests", "fonts");
        throw new DirectoryNotFoundException("tests/fonts");
    }

    private sealed class Page : IDisposable
    {
        public JintScriptEngine Engine { get; } = new();
        public Document Document { get; }

        public Page(CanvasTests owner, string body = "<canvas id='c' width='100' height='60'></canvas>", bool fonts = false, bool alpha = true)
        {
            Document = Document.Parse("<html><body>" + body + "</body></html>", new FolioOptions
            {
                Raster = owner.CreateRaster(),
                Fonts = fonts ? new FontSettings { Source = new FontFolderSource(FontsFolder()) } : FontSettings.Default,
            });
            DomBindings.Install(Engine, Document);
            Engine.Evaluate("var canvas = document.getElementById('c'); var ctx = canvas.getContext('2d', {alpha: " + (alpha ? "true" : "false") + "});");
        }

        public string Js(string script) => Engine.Evaluate(script).AsString()!;

        public double Num(string script) => Engine.Evaluate(script).AsNumber();

        public bool Is(string script) => Engine.Evaluate(script).AsBoolean();

        /// <summary>The pixel at (x, y) as "r,g,b,a".</summary>
        public string Pixel(int x, int y) =>
            Js($"(function(){{ var d = ctx.getImageData({x}, {y}, 1, 1).data; return d[0] + ',' + d[1] + ',' + d[2] + ',' + d[3]; }})()");

        public void Dispose() => Engine.Dispose();
    }

    // ---------------------------------------------------------------- #502: the element, the context, rectangles

    [Fact]
    public void FillRectPaintsTheBitmap()
    {
        using var page = new Page(this);
        page.Js("ctx.fillStyle = 'red'; ctx.fillRect(10, 10, 20, 20); ''");
        Assert.Equal("255,0,0,255", page.Pixel(15, 15));
        Assert.Equal("0,0,0,0", page.Pixel(5, 5));
        Assert.Equal("0,0,0,0", page.Pixel(35, 35));
    }

    [Fact]
    public void ABitmapStartsTransparent() => Assert.Equal("0,0,0,0", new Page(this).Pixel(50, 30));

    [Fact]
    public void AContextWithoutAlphaStartsOpaqueBlack()
    {
        using var page = new Page(this, alpha: false);
        Assert.Equal("0,0,0,255", page.Pixel(50, 30));
        page.Js("ctx.clearRect(0, 0, 100, 60); ''");
        Assert.Equal("0,0,0,0", page.Pixel(50, 30));
    }

    [Fact]
    public void GetContextReturnsTheSameObjectAndNullForOtherKinds()
    {
        using var page = new Page(this);
        Assert.True(page.Is("canvas.getContext('2d') === ctx && ctx.canvas === canvas"));
        Assert.True(page.Is("canvas.getContext('webgl') === null && canvas.getContext('bitmaprenderer') === null"));
    }

    [Fact]
    public void GlobalAlphaScalesFills()
    {
        using var page = new Page(this);
        page.Js("ctx.globalAlpha = 0.5; ctx.fillStyle = '#0000ff'; ctx.fillRect(0, 0, 10, 10); ''");
        var pixel = page.Pixel(5, 5).Split(',').Select(int.Parse).ToArray();
        Assert.Equal((0, 0, 255), (pixel[0], pixel[1], pixel[2]));
        Assert.InRange(pixel[3], 126, 129);
    }

    [Fact]
    public void GlobalAlphaIgnoresValuesOutsideZeroToOne()
    {
        using var page = new Page(this);
        Assert.Equal(1, page.Num("ctx.globalAlpha = 2; ctx.globalAlpha = NaN; ctx.globalAlpha = -1; ctx.globalAlpha"));
        Assert.Equal(0.25, page.Num("ctx.globalAlpha = 0.25; ctx.globalAlpha"));
    }

    [Theory]
    [InlineData("red", "#ff0000")]
    [InlineData("#00f", "#0000ff")]
    [InlineData("rgb(1, 2, 3)", "#010203")]
    [InlineData("rgba(255, 0, 0, 0.5)", "rgba(255, 0, 0, 0.5)")]
    [InlineData("hsl(120, 100%, 25%)", "#008000")]
    [InlineData("transparent", "rgba(0, 0, 0, 0)")]
    public void StyleColorsAreParsedAndSerializedLikeABrowser(string input, string expected)
    {
        using var page = new Page(this);
        Assert.Equal(expected, page.Js($"ctx.fillStyle = '{input}'; ctx.fillStyle"));
        Assert.Equal(expected, page.Js($"ctx.strokeStyle = '{input}'; ctx.strokeStyle"));
    }

    [Fact]
    public void InvalidColorsAreIgnored()
    {
        using var page = new Page(this);
        Assert.Equal("#00ff00", page.Js("ctx.fillStyle = '#0f0'; ctx.fillStyle = 'not a colour'; ctx.fillStyle = '12'; ctx.fillStyle = ''; ctx.fillStyle"));
    }

    [Fact]
    public void SaveAndRestoreKeepTheDrawingState()
    {
        using var page = new Page(this);
        Assert.Equal("#ff0000,3,0.5|#00ff00,1,1|#ff0000,3,0.5", page.Js(@"
            ctx.fillStyle = 'red'; ctx.lineWidth = 3; ctx.globalAlpha = 0.5;
            var before = [ctx.fillStyle, ctx.lineWidth, ctx.globalAlpha];
            ctx.save();
            ctx.fillStyle = 'lime'; ctx.lineWidth = 1; ctx.globalAlpha = 1;
            var inside = [ctx.fillStyle, ctx.lineWidth, ctx.globalAlpha];
            ctx.restore();
            var after = [ctx.fillStyle, ctx.lineWidth, ctx.globalAlpha];
            [before, inside, after].map(function(a) { return a.join(','); }).join('|')"));
        // restore() with nothing saved does nothing
        Assert.Equal("#ff0000", page.Js("ctx.restore(); ctx.restore(); ctx.fillStyle"));
    }

    [Fact]
    public void RestoreBringsBackTheTransformAndTheClip()
    {
        using var page = new Page(this);
        page.Js("ctx.save(); ctx.translate(50, 0); ctx.beginPath(); ctx.rect(0, 0, 10, 10); ctx.clip(); ctx.restore(); ctx.fillStyle = 'red'; ctx.fillRect(0, 0, 100, 60); ''");
        // both are undone: the fill is not shifted and not clipped
        Assert.Equal("255,0,0,255", page.Pixel(5, 5));
        Assert.Equal("255,0,0,255", page.Pixel(90, 50));
    }

    [Fact]
    public void ClearRectErasesPixels()
    {
        using var page = new Page(this);
        page.Js("ctx.fillStyle = 'blue'; ctx.fillRect(0, 0, 100, 60); ctx.clearRect(10, 10, 10, 10); ''");
        Assert.Equal("0,0,0,0", page.Pixel(15, 15));
        Assert.Equal("0,0,255,255", page.Pixel(25, 15));
    }

    [Fact]
    public void ClearRectIgnoresAlphaAndCompositing()
    {
        using var page = new Page(this);
        page.Js("ctx.fillStyle = 'blue'; ctx.fillRect(0, 0, 100, 60); ctx.globalAlpha = 0.1; ctx.globalCompositeOperation = 'destination-over'; ctx.clearRect(10, 10, 10, 10); ''");
        Assert.Equal("0,0,0,0", page.Pixel(15, 15));
    }

    [Fact]
    public void StrokeRectDrawsAnOutline()
    {
        using var page = new Page(this);
        page.Js("ctx.strokeStyle = 'black'; ctx.lineWidth = 4; ctx.strokeRect(20, 20, 40, 20); ''");
        Assert.Equal("0,0,0,255", page.Pixel(40, 20));
        Assert.Equal("0,0,0,0", page.Pixel(40, 30));
    }

    [Fact]
    public void RectanglesWithNonFiniteOrZeroSizesDrawNothing()
    {
        using var page = new Page(this);
        page.Js("ctx.fillRect(NaN, 0, 10, 10); ctx.fillRect(0, 0, Infinity, 10); ctx.fillRect(0, 0, 0, 10); ctx.fillRect(0, 0, 10, 0); ''");
        Assert.Equal("0,0,0,0", page.Pixel(5, 5));
    }

    [Fact]
    public void NegativeSizesFlipTheRectangle()
    {
        using var page = new Page(this);
        page.Js("ctx.fillStyle = 'red'; ctx.fillRect(30, 30, -20, -20); ''");
        Assert.Equal("255,0,0,255", page.Pixel(15, 15));
    }

    [Fact]
    public void FillRectNeedsFourArguments() =>
        Assert.Equal("TypeError", new Page(this).Js("try { ctx.fillRect(1, 2, 3); 'no' } catch (e) { e.name }"));

    [Fact]
    public void SettingWidthOrHeightResetsTheBitmapAndTheState()
    {
        using var page = new Page(this);
        page.Js("ctx.fillStyle = 'red'; ctx.lineWidth = 5; ctx.fillRect(0, 0, 100, 60); canvas.width = 100; ''");
        Assert.Equal("0,0,0,0", page.Pixel(5, 5));
        Assert.Equal(1, page.Num("ctx.lineWidth"));
        Assert.Equal("#000000", page.Js("ctx.fillStyle"));
        page.Js("canvas.height = 20; ''");
        Assert.Equal(20, page.Num("canvas.height"));
        Assert.Equal(20, page.Num("ctx.getImageData(0, 0, 100, 200).height === 200 ? 20 : 0"));
        Assert.Equal("0,0,0,0", page.Pixel(5, 19));
    }

    [Fact]
    public void ChangingTheWidthAttributeResizesTheBitmapToo()
    {
        using var page = new Page(this);
        page.Js("canvas.setAttribute('width', '20'); ctx.fillStyle = 'red'; ctx.fillRect(0, 0, 20, 60); ''");
        Assert.Equal("255,0,0,255", page.Pixel(10, 10));
        Assert.Equal("0,0,0,0", page.Pixel(30, 10));
        Assert.Equal(20, page.Num("canvas.width"));
    }

    [Fact]
    public void OversizedCanvasesGetNoContext()
    {
        using var page = new Page(this, "<canvas id='c' width='20000' height='10'></canvas><canvas id='d' width='10000' height='10000'></canvas>");
        Assert.True(page.Is("canvas.getContext('2d') === null"), "wider than 16,384 pixels");
        Assert.True(page.Is("document.getElementById('d').getContext('2d') === null"), "more than 64 megapixels");
    }

    [Fact]
    public void ACanvasOfSizeZeroHasNoBitmap()
    {
        using var page = new Page(this, "<canvas id='c' width='0' height='10'></canvas>");
        Assert.True(page.Is("canvas.getContext('2d') === null"));
        Assert.Equal("data:,", page.Js("canvas.toDataURL()"));
    }

    [Fact]
    public void ToDataUrlEncodesThePixels()
    {
        Assert.SkipWhen(UsesSoftwareRaster, "The software backend encodes PNG only (#568 follow-up: a JPEG encoder).");
        using var page = new Page(this);
        page.Js("ctx.fillStyle = 'red'; ctx.fillRect(0, 0, 100, 60); ''");
        var url = page.Js("canvas.toDataURL()");
        Assert.StartsWith("data:image/png;base64,", url);
        // the PNG decodes back to the same pixels
        var bytes = Convert.FromBase64String(url["data:image/png;base64,".Length..]);
        var decoded = Folio.Imaging.ImageLoader.Decode(bytes);
        Assert.NotNull(decoded);
        Assert.Equal((100, 60), (decoded.Width, decoded.Height));
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, decoded.Pixels[..4]);
        Assert.StartsWith("data:image/jpeg;base64,", page.Js("canvas.toDataURL('image/jpeg', 0.5)"));
        Assert.StartsWith("data:image/png;base64,", page.Js("canvas.toDataURL('image/unknown')"));
    }

    [Fact]
    public void WithoutARasterBackendGetContextReturnsNull()
    {
        using var engine = new JintScriptEngine();
        var document = Document.Parse("<canvas id='c'></canvas>");
        DomBindings.Install(engine, document);
        Assert.True(engine.Evaluate("document.getElementById('c').getContext('2d') === null").AsBoolean());
    }

    // ---------------------------------------------------------------- #504: paths

    [Fact]
    public void FillPaintsAClosedTriangle()
    {
        using var page = new Page(this);
        page.Js("ctx.fillStyle = 'red'; ctx.beginPath(); ctx.moveTo(10, 10); ctx.lineTo(50, 10); ctx.lineTo(10, 50); ctx.fill(); ''");
        Assert.Equal("255,0,0,255", page.Pixel(15, 15));
        Assert.Equal("0,0,0,0", page.Pixel(45, 45));
    }

    [Fact]
    public void ArcFillsACircle()
    {
        using var page = new Page(this);
        page.Js("ctx.fillStyle = 'red'; ctx.beginPath(); ctx.arc(50, 30, 20, 0, Math.PI * 2); ctx.fill(); ''");
        Assert.Equal("255,0,0,255", page.Pixel(50, 30));
        Assert.Equal("255,0,0,255", page.Pixel(66, 30));
        Assert.Equal("0,0,0,0", page.Pixel(72, 30));
        Assert.Equal("0,0,0,0", page.Pixel(50, 54));
    }

    [Fact]
    public void ArcAngleAndDirectionSelectTheRightHalf()
    {
        using var page = new Page(this);
        // clockwise from 0 to PI: the lower half (y grows down)
        page.Js("ctx.fillStyle = 'red'; ctx.beginPath(); ctx.arc(50, 30, 20, 0, Math.PI); ctx.fill(); ''");
        Assert.Equal("255,0,0,255", page.Pixel(50, 40));
        Assert.Equal("0,0,0,0", page.Pixel(50, 20));
        page.Js("ctx.clearRect(0, 0, 100, 60); ctx.beginPath(); ctx.arc(50, 30, 20, 0, Math.PI, true); ctx.fill(); ''");
        Assert.Equal("255,0,0,255", page.Pixel(50, 20));
        Assert.Equal("0,0,0,0", page.Pixel(50, 40));
    }

    [Fact]
    public void ArcSweepsOverTwoPiDrawAFullCircle()
    {
        using var page = new Page(this);
        page.Js("ctx.fillStyle = 'red'; ctx.beginPath(); ctx.arc(50, 30, 20, 1, 1 + 10 * Math.PI); ctx.fill(); ''");
        Assert.Equal("255,0,0,255", page.Pixel(35, 30));
        Assert.Equal("255,0,0,255", page.Pixel(65, 30));
    }

    [Fact]
    public void ANegativeRadiusThrowsIndexSizeError()
    {
        using var page = new Page(this);
        Assert.Equal("IndexSizeError", page.Js("try { ctx.arc(0, 0, -1, 0, 1); 'no' } catch (e) { e.name }"));
        Assert.Equal("IndexSizeError", page.Js("try { ctx.ellipse(0, 0, 1, -1, 0, 0, 1); 'no' } catch (e) { e.name }"));
        Assert.Equal("IndexSizeError", page.Js("try { ctx.arcTo(0, 0, 1, 1, -5); 'no' } catch (e) { e.name }"));
        Assert.Equal("IndexSizeError", page.Js("try { new Path2D().arc(0, 0, -1, 0, 1); 'no' } catch (e) { e.name }"));
        Assert.Equal("fine", page.Js("ctx.arc(0, 0, 0, 0, 1); 'fine'"));
    }

    [Fact]
    public void EllipseHonoursItsRotation()
    {
        using var page = new Page(this);
        // a wide ellipse turned a quarter becomes a tall one
        page.Js("ctx.fillStyle = 'red'; ctx.beginPath(); ctx.ellipse(50, 30, 30, 8, Math.PI / 2, 0, Math.PI * 2); ctx.fill(); ''");
        Assert.Equal("255,0,0,255", page.Pixel(50, 55));
        Assert.Equal("0,0,0,0", page.Pixel(75, 30));
    }

    [Fact]
    public void ArcToRoundsACorner()
    {
        using var page = new Page(this);
        page.Js("ctx.lineWidth = 2; ctx.strokeStyle = 'black'; ctx.beginPath(); ctx.moveTo(10, 10); ctx.arcTo(60, 10, 60, 50, 20); ctx.lineTo(60, 50); ctx.stroke(); ''");
        // straight segment up to the corner's tangent point, then the curve
        Assert.Equal("0,0,0,255", page.Pixel(30, 10));
        // the corner itself (60,10) is cut off by the arc
        Assert.Equal("0,0,0,0", page.Pixel(59, 11));
    }

    [Fact]
    public void QuadraticAndBezierCurvesReachTheirEndPoints()
    {
        using var page = new Page(this);
        page.Js("ctx.lineWidth = 3; ctx.beginPath(); ctx.moveTo(10, 50); ctx.quadraticCurveTo(50, -10, 90, 50); ctx.stroke(); ctx.beginPath(); ctx.moveTo(10, 5); ctx.bezierCurveTo(30, 5, 70, 5, 90, 5); ctx.stroke(); ''");
        // the quadratic passes (18, 39) at a tenth of the way and peaks at (50, 20); the cubic runs flat along y = 5
        Assert.True(int.Parse(page.Pixel(18, 39).Split(',')[3]) > 150);
        Assert.True(int.Parse(page.Pixel(50, 20).Split(',')[3]) > 150);
        Assert.Equal("0,0,0,255", page.Pixel(50, 5));
    }

    [Fact]
    public void RoundRectTakesNumbersPointsAndLists()
    {
        using var page = new Page(this);
        page.Js("ctx.fillStyle = 'red'; ctx.beginPath(); ctx.roundRect(10, 10, 50, 30, 15); ctx.fill(); ''");
        Assert.Equal("255,0,0,255", page.Pixel(35, 25));
        Assert.Equal("0,0,0,0", page.Pixel(11, 11));
        page.Js("ctx.clearRect(0, 0, 100, 60); ctx.beginPath(); ctx.roundRect(10, 10, 50, 30, [20, 0]); ctx.fill(); ''");
        Assert.Equal("0,0,0,0", page.Pixel(11, 11));
        Assert.Equal("255,0,0,255", page.Pixel(58, 11));
        page.Js("ctx.clearRect(0, 0, 100, 60); ctx.beginPath(); ctx.roundRect(10, 10, 50, 30, {x: 20, y: 5}); ctx.fill(); ''");
        Assert.Equal("255,0,0,255", page.Pixel(11, 25));
        Assert.Equal("RangeError", page.Js("try { ctx.roundRect(0, 0, 10, 10, -1); 'no' } catch (e) { e.name }"));
        Assert.Equal("RangeError", page.Js("try { ctx.roundRect(0, 0, 10, 10, [1, 2, 3, 4, 5]); 'no' } catch (e) { e.name }"));
    }

    [Fact]
    public void RectAddsASubpathThatFillsTogetherWithOthers()
    {
        using var page = new Page(this);
        page.Js("ctx.fillStyle = 'red'; ctx.beginPath(); ctx.rect(0, 0, 10, 10); ctx.rect(20, 0, 10, 10); ctx.fill(); ''");
        Assert.Equal("255,0,0,255", page.Pixel(5, 5));
        Assert.Equal("255,0,0,255", page.Pixel(25, 5));
        Assert.Equal("0,0,0,0", page.Pixel(15, 5));
    }

    [Fact]
    public void FillRulesDecideWhatANestedPathCovers()
    {
        using var page = new Page(this);
        const string Draw = "ctx.beginPath(); ctx.rect(0, 0, 60, 60); ctx.rect(15, 15, 30, 30); ";
        page.Js(Draw + "ctx.fillStyle = 'red'; ctx.fill(); ''");
        Assert.Equal("255,0,0,255", page.Pixel(30, 30));
        page.Js("ctx.clearRect(0, 0, 100, 60); " + Draw + "ctx.fill('evenodd'); ''");
        Assert.Equal("0,0,0,0", page.Pixel(30, 30));
        Assert.Equal("255,0,0,255", page.Pixel(5, 5));
    }

    [Fact]
    public void ClipRestrictsLaterDrawing()
    {
        using var page = new Page(this);
        page.Js("ctx.beginPath(); ctx.arc(50, 30, 10, 0, Math.PI * 2); ctx.clip(); ctx.fillStyle = 'red'; ctx.fillRect(0, 0, 100, 60); ''");
        Assert.Equal("255,0,0,255", page.Pixel(50, 30));
        Assert.Equal("0,0,0,0", page.Pixel(10, 10));
    }

    [Fact]
    public void ClipIntersectsWithTheClipBefore()
    {
        using var page = new Page(this);
        page.Js("ctx.beginPath(); ctx.rect(0, 0, 50, 60); ctx.clip(); ctx.beginPath(); ctx.rect(25, 0, 50, 60); ctx.clip(); ctx.fillStyle = 'red'; ctx.fillRect(0, 0, 100, 60); ''");
        Assert.Equal("0,0,0,0", page.Pixel(10, 30));
        Assert.Equal("255,0,0,255", page.Pixel(40, 30));
        Assert.Equal("0,0,0,0", page.Pixel(60, 30));
    }

    [Fact]
    public void IsPointInPathUsesTheFillRule()
    {
        using var page = new Page(this);
        page.Js("ctx.beginPath(); ctx.rect(0, 0, 60, 60); ctx.rect(15, 15, 30, 30); ''");
        Assert.True(page.Is("ctx.isPointInPath(30, 30)"));
        Assert.False(page.Is("ctx.isPointInPath(30, 30, 'evenodd')"));
        Assert.True(page.Is("ctx.isPointInPath(5, 5, 'evenodd')"));
        Assert.False(page.Is("ctx.isPointInPath(70, 30)"));
        Assert.False(page.Is("ctx.isPointInPath(NaN, 30)"));
    }

    [Fact]
    public void IsPointInPathMapsPath2DByTheCurrentTransform()
    {
        using var page = new Page(this);
        Assert.True(page.Is("var p = new Path2D(); p.rect(0, 0, 10, 10); ctx.translate(50, 0); ctx.isPointInPath(p, 55, 5) && !ctx.isPointInPath(p, 5, 5)"));
    }

    [Fact]
    public void IsPointInStrokeUsesTheLineWidth()
    {
        using var page = new Page(this);
        page.Js("ctx.lineWidth = 10; ctx.beginPath(); ctx.moveTo(0, 30); ctx.lineTo(100, 30); ''");
        Assert.True(page.Is("ctx.isPointInStroke(50, 34)"));
        Assert.False(page.Is("ctx.isPointInStroke(50, 40)"));
    }

    [Fact]
    public void Path2DCanBeBuiltCopiedAndAdded()
    {
        using var page = new Page(this);
        page.Js("var p = new Path2D(); p.rect(0, 0, 10, 10); var q = new Path2D(p); q.addPath(p, {e: 20, f: 0}); ctx.fillStyle = 'red'; ctx.fill(q); ''");
        Assert.Equal("255,0,0,255", page.Pixel(5, 5));
        Assert.Equal("255,0,0,255", page.Pixel(25, 5));
        Assert.Equal("0,0,0,0", page.Pixel(15, 5));
        // p itself did not change when q was extended
        page.Js("ctx.clearRect(0, 0, 100, 60); ctx.fill(p); ''");
        Assert.Equal("0,0,0,0", page.Pixel(25, 5));
    }

    [Fact]
    public void Path2DParsesSvgPathData()
    {
        using var page = new Page(this);
        page.Js("ctx.fillStyle = 'red'; ctx.fill(new Path2D('M10 10 H 50 V 40 H 10 Z')); ''");
        Assert.Equal("255,0,0,255", page.Pixel(30, 25));
        Assert.Equal("0,0,0,0", page.Pixel(60, 25));
        // data with an error draws up to it
        page.Js("ctx.clearRect(0, 0, 100, 60); ctx.fill(new Path2D('M10 10 L 50 10 L 50 40 L nonsense')); ''");
        Assert.Equal("255,0,0,255", page.Pixel(40, 15));
        // data that is not a path at all is an empty path
        page.Js("ctx.clearRect(0, 0, 100, 60); ctx.fill(new Path2D('not a path')); ''");
        Assert.Equal("0,0,0,0", page.Pixel(40, 15));
    }

    [Fact]
    public void Path2DIsDrawnUnderTheTransformInForce()
    {
        using var page = new Page(this);
        page.Js("var p = new Path2D(); p.rect(0, 0, 10, 10); ctx.fillStyle = 'red'; ctx.translate(40, 20); ctx.fill(p); ''");
        Assert.Equal("255,0,0,255", page.Pixel(45, 25));
        Assert.Equal("0,0,0,0", page.Pixel(5, 5));
    }

    [Fact]
    public void ThePathIsFixedInCanvasCoordinatesWhenItIsBuilt()
    {
        using var page = new Page(this);
        // The rectangle is built under translate(10, 10); later transforms do not move it.
        page.Js("ctx.translate(10, 10); ctx.beginPath(); ctx.rect(0, 0, 10, 10); ctx.translate(40, 0); ctx.fillStyle = 'red'; ctx.fill(); ''");
        Assert.Equal("255,0,0,255", page.Pixel(15, 15));
        Assert.Equal("0,0,0,0", page.Pixel(55, 15));
    }

    // ---------------------------------------------------------------- #505: line styles

    [Fact]
    public void LineWidthCapsAndJoinsShapeStrokes()
    {
        using var page = new Page(this);
        page.Js("ctx.lineWidth = 10; ctx.lineCap = 'butt'; ctx.beginPath(); ctx.moveTo(20, 15); ctx.lineTo(60, 15); ctx.stroke(); ''");
        Assert.Equal("0,0,0,255", page.Pixel(30, 12));
        Assert.Equal("0,0,0,0", page.Pixel(17, 15));
        page.Js("ctx.clearRect(0, 0, 100, 60); ctx.lineCap = 'square'; ctx.beginPath(); ctx.moveTo(20, 15); ctx.lineTo(60, 15); ctx.stroke(); ''");
        Assert.Equal("0,0,0,255", page.Pixel(17, 15));
        page.Js("ctx.clearRect(0, 0, 100, 60); ctx.lineCap = 'round'; ctx.beginPath(); ctx.moveTo(20, 15); ctx.lineTo(60, 15); ctx.stroke(); ''");
        Assert.Equal("0,0,0,255", page.Pixel(17, 15));
        Assert.Equal("0,0,0,0", page.Pixel(15, 10));
    }

    [Fact]
    public void LineJoinAndMiterLimitChangeACorner()
    {
        using var page = new Page(this);
        const string Corner = "ctx.lineWidth = 10; ctx.beginPath(); ctx.moveTo(10, 50); ctx.lineTo(30, 20); ctx.lineTo(50, 50); ctx.stroke(); ''";
        page.Js("ctx.lineJoin = 'miter'; ctx.miterLimit = 10; " + Corner);
        var mitred = page.Pixel(30, 12);
        page.Js("ctx.clearRect(0, 0, 100, 60); ctx.lineJoin = 'bevel'; " + Corner);
        var bevelled = page.Pixel(30, 12);
        page.Js("ctx.clearRect(0, 0, 100, 60); ctx.lineJoin = 'miter'; ctx.miterLimit = 1; " + Corner);
        var limited = page.Pixel(30, 12);
        Assert.True(int.Parse(mitred.Split(',')[3]) > 200, mitred);
        Assert.Equal("0,0,0,0", bevelled);
        Assert.Equal("0,0,0,0", limited);
    }

    [Fact]
    public void LineStylePropertiesIgnoreInvalidValues()
    {
        using var page = new Page(this);
        Assert.Equal("1|butt|miter|10|0", page.Js(@"
            ctx.lineWidth = 0; ctx.lineWidth = -3; ctx.lineWidth = NaN; ctx.lineWidth = Infinity;
            ctx.lineCap = 'pointy'; ctx.lineJoin = 'sharp'; ctx.miterLimit = 0; ctx.miterLimit = -1; ctx.lineDashOffset = NaN;
            [ctx.lineWidth, ctx.lineCap, ctx.lineJoin, ctx.miterLimit, ctx.lineDashOffset].join('|')"));
    }

    [Fact]
    public void DashesAlternateDrawingAndGaps()
    {
        using var page = new Page(this);
        page.Js("ctx.lineWidth = 4; ctx.setLineDash([10, 10]); ctx.beginPath(); ctx.moveTo(0, 30); ctx.lineTo(100, 30); ctx.stroke(); ''");
        Assert.Equal("0,0,0,255", page.Pixel(5, 30));
        Assert.Equal("0,0,0,0", page.Pixel(15, 30));
        Assert.Equal("0,0,0,255", page.Pixel(25, 30));
        // the offset shifts the pattern
        page.Js("ctx.clearRect(0, 0, 100, 60); ctx.lineDashOffset = 10; ctx.beginPath(); ctx.moveTo(0, 30); ctx.lineTo(100, 30); ctx.stroke(); ''");
        Assert.Equal("0,0,0,0", page.Pixel(5, 30));
        Assert.Equal("0,0,0,255", page.Pixel(15, 30));
    }

    [Fact]
    public void AnOddDashListIsRepeatedAndInvalidOnesAreIgnored()
    {
        using var page = new Page(this);
        Assert.Equal("5,10,15,5,10,15", page.Js("ctx.setLineDash([5, 10, 15]); ctx.getLineDash().join(',')"));
        Assert.Equal("5,10,15,5,10,15", page.Js("ctx.setLineDash([5, -1]); ctx.setLineDash([5, NaN]); ctx.getLineDash().join(',')"));
        Assert.Equal("", page.Js("ctx.setLineDash([]); ctx.getLineDash().join(',')"));
    }

    [Fact]
    public void TheDashStateIsSavedAndRestored()
    {
        using var page = new Page(this);
        Assert.Equal("2,2|", page.Js("ctx.setLineDash([2, 2]); ctx.save(); ctx.setLineDash([]); var a = ctx.getLineDash().join(','); ctx.restore(); ctx.getLineDash().join(',') + '|' + a"));
    }

    // ---------------------------------------------------------------- #506: transforms and compositing

    [Fact]
    public void TranslateScaleAndRotateMoveWhatIsDrawn()
    {
        using var page = new Page(this);
        page.Js("ctx.fillStyle = 'red'; ctx.translate(50, 10); ctx.scale(2, 2); ctx.fillRect(0, 0, 10, 10); ''");
        Assert.Equal("255,0,0,255", page.Pixel(60, 20));
        Assert.Equal("0,0,0,0", page.Pixel(75, 20));
        page.Js("ctx.setTransform(1, 0, 0, 1, 0, 0); ctx.clearRect(0, 0, 100, 60); ctx.translate(50, 30); ctx.rotate(Math.PI / 2); ctx.fillRect(0, 0, 20, 10); ''");
        // a 20x10 rectangle turned a quarter: it now hangs below the origin, 10 wide and 20 tall, to the left
        Assert.Equal("255,0,0,255", page.Pixel(45, 45));
        Assert.Equal("0,0,0,0", page.Pixel(65, 33));
    }

    [Fact]
    public void TransformMultipliesAndSetTransformReplaces()
    {
        using var page = new Page(this);
        Assert.Equal("1,0,0,1,30,0", page.Js("ctx.translate(10, 0); ctx.transform(1, 0, 0, 1, 20, 0); var m = ctx.getTransform(); [m.a, m.b, m.c, m.d, m.e, m.f].join(',')"));
        Assert.Equal("2,0,0,3,4,5", page.Js("ctx.setTransform(2, 0, 0, 3, 4, 5); var m2 = ctx.getTransform(); [m2.a, m2.b, m2.c, m2.d, m2.e, m2.f].join(',')"));
        Assert.Equal("1,0,0,1,0,0", page.Js("ctx.resetTransform(); var m3 = ctx.getTransform(); [m3.a, m3.b, m3.c, m3.d, m3.e, m3.f].join(',')"));
        Assert.Equal("3,0,0,4,7,8", page.Js("ctx.setTransform({a: 3, d: 4, e: 7, f: 8}); var m4 = ctx.getTransform(); [m4.a, m4.b, m4.c, m4.d, m4.e, m4.f].join(',')"));
    }

    [Fact]
    public void DomMatrixSupportsCanvasInitAndMatrixOperations()
    {
        using var page = new Page(this);
        Assert.Equal("2,0,0,3,10,14,true", page.Js("var m = new DOMMatrix([2,0,0,3,4,5]).translate(3, 3); var n = m.multiply(new DOMMatrix().scale(1, 1)); [n.a,n.b,n.c,n.d,n.e,n.f,n.is2D].join(',')"));
        Assert.Equal("1,0,0,1,-7,-8", page.Js("var i = new DOMMatrix([1,0,0,1,7,8]).inverse(); [i.a,i.b,i.c,i.d,i.e,i.f].join(',')"));
        Assert.Equal("2,0,0,3,4,5", page.Js("ctx.setTransform(new DOMMatrix([2,0,0,3,4,5])); var p = ctx.getTransform(); [p.a,p.b,p.c,p.d,p.e,p.f].join(',')"));
        Assert.Equal("3,4,7,8", page.Js("var q = new DOMMatrix({m11:3,m22:4,m41:7,m42:8}); [q.a,q.d,q.e,q.f].join(',')"));
        Assert.Equal("InvalidStateError", page.Js("try { new DOMMatrix([0,0,0,0,0,0]).inverse(); } catch (e) { e.name }"));
    }

    [Fact]
    public void NonFiniteTransformsAreIgnored()
    {
        using var page = new Page(this);
        Assert.Equal("1,0,0,1,5,0", page.Js("ctx.translate(5, 0); ctx.translate(NaN, 3); ctx.scale(Infinity, 1); ctx.rotate(NaN); ctx.transform(1, 0, 0, 1, NaN, 0); ctx.setTransform(1, 0, 0, 1, Infinity, 0); var m = ctx.getTransform(); [m.a, m.b, m.c, m.d, m.e, m.f].join(',')"));
    }

    [Fact]
    public void AnUninvertibleTransformDrawsNothing()
    {
        using var page = new Page(this);
        page.Js("ctx.fillStyle = 'red'; ctx.scale(0, 1); ctx.fillRect(0, 0, 100, 60); ''");
        Assert.Equal("0,0,0,0", page.Pixel(5, 5));
    }

    [Fact]
    public void StrokesFollowANonUniformScale()
    {
        using var page = new Page(this);
        // a 2-wide pen under scale(10, 1) is 20 pixels thick across a vertical line and 2 along it
        page.Js("ctx.lineWidth = 2; ctx.scale(10, 1); ctx.beginPath(); ctx.moveTo(5, 0); ctx.lineTo(5, 60); ctx.stroke(); ''");
        Assert.Equal("0,0,0,255", page.Pixel(41, 30));
        Assert.Equal("0,0,0,255", page.Pixel(59, 30));
        Assert.Equal("0,0,0,0", page.Pixel(35, 30));
    }

    [Theory]
    [InlineData("source-over", "255,0,0,255")]
    [InlineData("destination-over", "0,0,255,255")]
    [InlineData("source-in", "255,0,0,255")]
    [InlineData("destination-in", "0,0,255,255")]
    [InlineData("source-out", "0,0,0,0")]
    [InlineData("destination-out", "0,0,0,0")]
    [InlineData("xor", "0,0,0,0")]
    [InlineData("copy", "255,0,0,255")]
    [InlineData("source-atop", "255,0,0,255")]
    [InlineData("destination-atop", "0,0,255,255")]
    public void CompositeOperationsCombineRedOverBlueInsideTheShape(string operation, string expected)
    {
        using var page = new Page(this);
        page.Js($"ctx.fillStyle = 'blue'; ctx.fillRect(0, 0, 100, 60); ctx.globalCompositeOperation = '{operation}'; ctx.fillStyle = 'red'; ctx.fillRect(20, 20, 20, 20); ''");
        Assert.Equal(expected, page.Pixel(30, 30));
    }

    [Theory]
    [InlineData("source-in", "0,0,0,0")]
    [InlineData("destination-in", "0,0,0,0")]
    [InlineData("source-out", "0,0,0,0")]
    [InlineData("copy", "0,0,0,0")]
    [InlineData("destination-over", "0,0,255,255")]
    [InlineData("source-over", "0,0,255,255")]
    public void SomeOperationsReachOutsideTheShape(string operation, string expected)
    {
        using var page = new Page(this);
        page.Js($"ctx.fillStyle = 'blue'; ctx.fillRect(0, 0, 100, 60); ctx.globalCompositeOperation = '{operation}'; ctx.fillStyle = 'red'; ctx.fillRect(20, 20, 20, 20); ''");
        Assert.Equal(expected, page.Pixel(80, 50));
    }

    [Fact]
    public void BlendModesMixColours()
    {
        using var page = new Page(this);
        page.Js("ctx.fillStyle = '#ff8000'; ctx.fillRect(0, 0, 100, 60); ctx.globalCompositeOperation = 'multiply'; ctx.fillStyle = '#808080'; ctx.fillRect(0, 0, 50, 60); ''");
        var multiplied = page.Pixel(10, 10).Split(',').Select(int.Parse).ToArray();
        Assert.InRange(multiplied[0], 126, 129);
        Assert.InRange(multiplied[1], 63, 65);
        page.Js("ctx.globalCompositeOperation = 'lighter'; ctx.fillStyle = '#400000'; ctx.fillRect(60, 0, 10, 10); ''");
        Assert.Equal("255,128,0,255", page.Pixel(65, 5));
    }

    [Fact]
    public void AnInvalidCompositeOperationIsIgnored()
    {
        using var page = new Page(this);
        Assert.Equal("xor", page.Js("ctx.globalCompositeOperation = 'xor'; ctx.globalCompositeOperation = 'bogus'; ctx.globalCompositeOperation"));
    }

    // ---------------------------------------------------------------- #507: images and pixels

    [Fact]
    public void PutImageDataAndGetImageDataRoundTripExactlyForOpaquePixels()
    {
        using var page = new Page(this);
        Assert.Equal("10,20,30,255|40,50,60,255", page.Js(@"
            var img = ctx.createImageData(2, 1);
            img.data.set([10, 20, 30, 255, 40, 50, 60, 255]);
            ctx.putImageData(img, 5, 5);
            var out = ctx.getImageData(5, 5, 2, 1).data;
            [out[0] + ',' + out[1] + ',' + out[2] + ',' + out[3], out[4] + ',' + out[5] + ',' + out[6] + ',' + out[7]].join('|')"));
    }

    [Fact]
    public void PartiallyTransparentPixelsKeepTheirAlphaThroughTheRoundTrip()
    {
        using var page = new Page(this);
        Assert.True(page.Is(@"
            var img = ctx.createImageData(1, 1);
            img.data.set([200, 100, 50, 128]);
            ctx.putImageData(img, 0, 0);
            var out = ctx.getImageData(0, 0, 1, 1).data;
            out[3] === 128 && Math.abs(out[0] - 200) <= 2 && Math.abs(out[1] - 100) <= 2 && Math.abs(out[2] - 50) <= 2"));
    }

    [Fact]
    public void PutImageDataIgnoresTransformsAlphaAndCompositing()
    {
        using var page = new Page(this);
        page.Js("ctx.fillStyle = 'blue'; ctx.fillRect(0, 0, 100, 60); ctx.translate(50, 50); ctx.globalAlpha = 0.1; ctx.globalCompositeOperation = 'destination-over'; var i = ctx.createImageData(1, 1); i.data.set([255, 0, 0, 255]); ctx.putImageData(i, 3, 3); ''");
        Assert.Equal("255,0,0,255", page.Pixel(3, 3));
        // putImageData replaces pixels, transparent ones included
        page.Js("var t = ctx.createImageData(1, 1); ctx.putImageData(t, 3, 3); ''");
        Assert.Equal("0,0,0,0", page.Pixel(3, 3));
    }

    [Fact]
    public void PutImageDataWithADirtyRectangleCopiesOnlyThatPart()
    {
        using var page = new Page(this);
        page.Js(@"var img = ctx.createImageData(4, 1); for (var i = 0; i < 4; i++) img.data.set([i + 1, 0, 0, 255], i * 4);
                  ctx.putImageData(img, 10, 10, 1, 0, 2, 1); ''");
        Assert.Equal("0,0,0,0", page.Pixel(10, 10));
        Assert.Equal("2,0,0,255", page.Pixel(11, 10));
        Assert.Equal("3,0,0,255", page.Pixel(12, 10));
        Assert.Equal("0,0,0,0", page.Pixel(13, 10));
        Assert.Equal("0,0,0,0", page.Pixel(10, 10));
    }

    [Fact]
    public void GetImageDataOutsideTheBitmapIsTransparent()
    {
        using var page = new Page(this);
        page.Js("ctx.fillStyle = 'red'; ctx.fillRect(0, 0, 100, 60); ''");
        Assert.Equal("0,0,0,0|255,0,0,255", page.Js("var d = ctx.getImageData(-1, 0, 2, 1).data; [d.slice(0, 4).join(','), d.slice(4, 8).join(',')].join('|')"));
        Assert.Equal(400, page.Num("ctx.getImageData(200, 200, 10, 10).data.length"));
    }

    [Fact]
    public void GetImageDataReportsItsSizeAndClampedArray()
    {
        using var page = new Page(this);
        Assert.Equal("3x2:24", page.Js("var d = ctx.getImageData(0, 0, 3, 2); d.width + 'x' + d.height + ':' + d.data.length"));
        Assert.True(page.Is("ctx.getImageData(0, 0, 1, 1).data instanceof Uint8ClampedArray"));
        Assert.Equal(255, page.Num("var c = ctx.createImageData(1, 1); c.data[0] = 999; c.data[0]"));
        // a negative size reads the rectangle on the other side of the origin
        Assert.Equal("2x1", page.Js("var n = ctx.getImageData(10, 0, -2, 1); n.width + 'x' + n.height"));
    }

    [Fact]
    public void ImageDataSizesAreChecked()
    {
        using var page = new Page(this);
        Assert.Equal("IndexSizeError", page.Js("try { ctx.getImageData(0, 0, 0, 5); 'no' } catch (e) { e.name }"));
        Assert.Equal("IndexSizeError", page.Js("try { ctx.createImageData(0, 5); 'no' } catch (e) { e.name }"));
        Assert.Equal("IndexSizeError", page.Js("try { ctx.getImageData(0, 0, NaN, 5); 'no' } catch (e) { e.name }"));
        Assert.Equal("TypeError", page.Js("try { ctx.putImageData({}, 0, 0); 'no' } catch (e) { e.name }"));
        Assert.Equal("2x3", page.Js("var a = ctx.createImageData(2, 3); var b = ctx.createImageData(a); b.width + 'x' + b.height"));
    }

    [Fact]
    public void TheImageDataConstructorTakesSizesOrData()
    {
        using var page = new Page(this);
        Assert.Equal("2x3:24", page.Js("var a = new ImageData(2, 3); a.width + 'x' + a.height + ':' + a.data.length"));
        Assert.Equal("1x2:8", page.Js("var b = new ImageData(new Uint8ClampedArray(8), 1); b.width + 'x' + b.height + ':' + b.data.length"));
        Assert.Equal("IndexSizeError", page.Js("try { new ImageData(new Uint8ClampedArray(8), 3); 'no' } catch (e) { e.name }"));
        Assert.Equal("InvalidStateError", page.Js("try { new ImageData(new Uint8ClampedArray(7), 1); 'no' } catch (e) { e.name }"));
        Assert.Equal("IndexSizeError", page.Js("try { new ImageData(0, 3); 'no' } catch (e) { e.name }"));
        page.Js("var data = new Uint8ClampedArray([1, 2, 3, 255]); var c = new ImageData(data, 1); ctx.putImageData(c, 0, 0); ''");
        Assert.Equal("1,2,3,255", page.Pixel(0, 0));
    }

    [Fact]
    public void DrawImageFromAnotherCanvasScalesAndPositions()
    {
        using var page = new Page(this, "<canvas id='c' width='100' height='60'></canvas><canvas id='s' width='10' height='10'></canvas>");
        page.Js("var s = document.getElementById('s').getContext('2d'); s.fillStyle = 'red'; s.fillRect(0, 0, 10, 10); ctx.drawImage(document.getElementById('s'), 20, 20, 20, 20); ''");
        Assert.Equal("255,0,0,255", page.Pixel(30, 30));
        Assert.Equal("0,0,0,0", page.Pixel(41, 30));
        Assert.Equal("0,0,0,0", page.Pixel(30, 41));
    }

    [Fact]
    public void DrawImageAcceptsVideoFramesWithMetadataDimensions()
    {
        using var engine = new JintScriptEngine();
        var document = Document.Parse("<canvas id='c' width='40' height='30'></canvas><video id='v' data-width='8' data-height='6'></video>", new FolioOptions
        {
            Capabilities = new FeatureCapabilities { Media = true },
            Raster = CreateRaster(),
        });
        DomBindings.Install(engine, document);
        Assert.True(engine.Evaluate("const c = document.getElementById('c'); const v = document.getElementById('v'); const ctx = c.getContext('2d'); ctx.drawImage(v, 0, 0); v.videoWidth === 8 && v.videoHeight === 6").AsBoolean());
    }

    [Fact]
    public void DrawImageWithASourceRectangleCopiesThatPart()
    {
        using var page = new Page(this, "<canvas id='c' width='100' height='60'></canvas><canvas id='s' width='20' height='10'></canvas>");
        page.Js("var s = document.getElementById('s').getContext('2d'); s.fillStyle = 'red'; s.fillRect(0, 0, 10, 10); s.fillStyle = 'blue'; s.fillRect(10, 0, 10, 10);" +
                "ctx.drawImage(document.getElementById('s'), 10, 0, 10, 10, 30, 30, 10, 10); ''");
        Assert.Equal("0,0,255,255", page.Pixel(35, 35));
        Assert.Equal("0,0,0,0", page.Pixel(25, 35));
        // a source rectangle past the image is clipped, and the destination shrinks with it
        page.Js("ctx.clearRect(0, 0, 100, 60); ctx.drawImage(document.getElementById('s'), 10, 0, 20, 10, 0, 0, 20, 10); ''");
        Assert.Equal("0,0,255,255", page.Pixel(5, 5));
        Assert.Equal("0,0,0,0", page.Pixel(15, 5));
    }

    [Fact]
    public void DrawImageFromAnImgElementWaitsForItsPixels()
    {
        // a 1x1 red PNG
        const string Red = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGP4z8DwHwAFAAH/q842iQAAAABJRU5ErkJggg==";
        using var page = new Page(this, $"<canvas id='c' width='100' height='60'></canvas><img id='i' src='{Red}'><img id='none'>");
        page.Js("ctx.drawImage(document.getElementById('i'), 10, 10, 20, 20); ''");
        Assert.Equal("255,0,0,255", page.Pixel(20, 20));
        // an image with nothing to show draws nothing
        page.Js("ctx.clearRect(0, 0, 100, 60); ctx.drawImage(document.getElementById('none'), 10, 10, 20, 20); ''");
        Assert.Equal("0,0,0,0", page.Pixel(20, 20));
    }

    [Fact]
    public void DrawImageRejectsOtherSourcesAndBadArgumentCounts()
    {
        using var page = new Page(this);
        Assert.Equal("TypeError", page.Js("try { ctx.drawImage({}, 0, 0); 'no' } catch (e) { e.name }"));
        Assert.Equal("TypeError", page.Js("try { ctx.drawImage(canvas, 0, 0, 1); 'no' } catch (e) { e.name }"));
        Assert.Equal("TypeError", page.Js("try { ctx.drawImage(canvas); 'no' } catch (e) { e.name }"));
    }

    [Fact]
    public void ImageSmoothingOffKeepsPixelsSquare()
    {
        using var page = new Page(this, "<canvas id='c' width='100' height='60'></canvas><canvas id='s' width='2' height='1'></canvas>");
        page.Js("var s = document.getElementById('s').getContext('2d'); s.fillStyle = 'red'; s.fillRect(0, 0, 1, 1); s.fillStyle = 'blue'; s.fillRect(1, 0, 1, 1);" +
                "ctx.imageSmoothingEnabled = false; ctx.drawImage(document.getElementById('s'), 0, 0, 40, 20); ''");
        Assert.Equal("255,0,0,255", page.Pixel(19, 10));
        Assert.Equal("0,0,255,255", page.Pixel(20, 10));
    }

    [Fact]
    public void HostileImageArgumentsNeverThrowOutOfTheirLimits()
    {
        using var page = new Page(this);
        page.Js(@"
            var big = 1e300;
            ctx.drawImage(canvas, 0, 0, big, big, 0, 0, big, big);
            ctx.drawImage(canvas, -big, -big, 10, 10, 0, 0, 10, 10);
            ctx.drawImage(canvas, NaN, 0, 10, 10, 0, 0, 10, 10);
            ctx.putImageData(ctx.createImageData(1, 1), big, -big);
            ctx.putImageData(ctx.createImageData(1, 1), 0, 0, NaN, 0, 1, 1);
            ctx.putImageData(ctx.createImageData(1, 1), 0, 0, -5, -5, big, big);
            ''");
        Assert.Equal("IndexSizeError", page.Js("try { ctx.getImageData(0, 0, big, big); 'no' } catch (e) { e.name }"));
        Assert.Equal("IndexSizeError", page.Js("try { ctx.getImageData(0, 0, 1e9, 1e9); 'no' } catch (e) { e.name }"));
    }

    // ---------------------------------------------------------------- #508: gradients and patterns

    [Fact]
    public void LinearGradientsInterpolateAlongTheLine()
    {
        using var page = new Page(this);
        page.Js("var g = ctx.createLinearGradient(0, 0, 100, 0); g.addColorStop(0, 'red'); g.addColorStop(1, 'blue'); ctx.fillStyle = g; ctx.fillRect(0, 0, 100, 60); ''");
        var left = page.Pixel(2, 30).Split(',').Select(int.Parse).ToArray();
        var middle = page.Pixel(50, 30).Split(',').Select(int.Parse).ToArray();
        var right = page.Pixel(97, 30).Split(',').Select(int.Parse).ToArray();
        Assert.True(left[0] > 240 && left[2] < 15);
        Assert.InRange(middle[0], 120, 135);
        Assert.InRange(middle[2], 120, 135);
        Assert.True(right[2] > 240 && right[0] < 15);
    }

    [Fact]
    public void GradientsPadBeyondTheirEndsAndReadBackAsObjects()
    {
        using var page = new Page(this);
        page.Js("var g = ctx.createLinearGradient(40, 0, 60, 0); g.addColorStop(0, 'red'); g.addColorStop(1, 'blue'); ctx.fillStyle = g; ctx.fillRect(0, 0, 100, 60); ''");
        Assert.Equal("255,0,0,255", page.Pixel(5, 30));
        Assert.Equal("0,0,255,255", page.Pixel(95, 30));
        Assert.True(page.Is("ctx.fillStyle === g"));
    }

    [Fact]
    public void ColorStopsAreCheckedAndOrdered()
    {
        using var page = new Page(this);
        Assert.Equal("IndexSizeError", page.Js("var g = ctx.createLinearGradient(0, 0, 1, 0); try { g.addColorStop(1.5, 'red'); 'no' } catch (e) { e.name }"));
        Assert.Equal("IndexSizeError", page.Js("try { g.addColorStop(NaN, 'red'); 'no' } catch (e) { e.name }"));
        Assert.Equal("SyntaxError", page.Js("try { g.addColorStop(0.5, 'nope'); 'no' } catch (e) { e.name }"));
        // stops given out of order still sort by offset, and equal offsets keep their order (a hard edge)
        page.Js("var h = ctx.createLinearGradient(0, 0, 100, 0); h.addColorStop(1, 'blue'); h.addColorStop(0.5, 'red'); h.addColorStop(0.5, 'lime'); h.addColorStop(0, 'red'); ctx.fillStyle = h; ctx.fillRect(0, 0, 100, 60); ''");
        Assert.Equal("255,0,0,255", page.Pixel(2, 30));
        var hardEdge = page.Pixel(52, 30).Split(',').Select(int.Parse).ToArray();
        Assert.True(hardEdge[1] > 230 && hardEdge[0] < 5, string.Join(",", hardEdge));
    }

    [Fact]
    public void RadialGradientsRadiateFromTheirCentre()
    {
        using var page = new Page(this);
        page.Js("var g = ctx.createRadialGradient(50, 30, 0, 50, 30, 30); g.addColorStop(0, 'white'); g.addColorStop(1, 'black'); ctx.fillStyle = g; ctx.fillRect(0, 0, 100, 60); ''");
        var centre = page.Pixel(50, 30).Split(',').Select(int.Parse).ToArray();
        var edge = page.Pixel(50, 57).Split(',').Select(int.Parse).ToArray();
        Assert.True(centre[0] > 240, "white at the centre");
        Assert.True(edge[0] < 40, "black near the outer circle");
        Assert.Equal("IndexSizeError", page.Js("try { ctx.createRadialGradient(0, 0, -1, 0, 0, 1); 'no' } catch (e) { e.name }"));
    }

    [Fact]
    public void ConicGradientsSweepAroundTheirCentre()
    {
        using var page = new Page(this);
        page.Js("var g = ctx.createConicGradient(0, 50, 30); g.addColorStop(0, 'red'); g.addColorStop(0.5, 'red'); g.addColorStop(0.5, 'blue'); g.addColorStop(1, 'blue'); ctx.fillStyle = g; ctx.fillRect(0, 0, 100, 60); ''");
        // angles run clockwise from 3 o'clock: just below the centre is a quarter turn in, in the red half
        Assert.Equal("255,0,0,255", page.Pixel(50, 50));
        Assert.Equal("0,0,255,255", page.Pixel(50, 8));
    }

    [Fact]
    public void GradientsAreDrawnUnderTheTransformInForceWhenFilling()
    {
        using var page = new Page(this);
        page.Js("var g = ctx.createLinearGradient(0, 0, 10, 0); g.addColorStop(0, 'red'); g.addColorStop(1, 'blue'); ctx.fillStyle = g; ctx.translate(50, 0); ctx.fillRect(0, 0, 10, 60); ''");
        var start = page.Pixel(51, 30).Split(',').Select(int.Parse).ToArray();
        var end = page.Pixel(58, 30).Split(',').Select(int.Parse).ToArray();
        Assert.True(start[0] > end[0] && end[2] > start[2]);
    }

    [Fact]
    public void GradientsScaleWithGlobalAlpha()
    {
        using var page = new Page(this);
        page.Js("var g = ctx.createLinearGradient(0, 0, 100, 0); g.addColorStop(0, 'red'); g.addColorStop(1, 'red'); ctx.fillStyle = g; ctx.globalAlpha = 0.5; ctx.fillRect(0, 0, 100, 60); ''");
        var alpha = int.Parse(page.Pixel(50, 30).Split(',')[3]);
        Assert.InRange(alpha, 126, 129);
    }

    [Fact]
    public void PatternsRepeatAnImageAcrossTheFill()
    {
        using var page = new Page(this, "<canvas id='c' width='100' height='60'></canvas><canvas id='s' width='10' height='10'></canvas>");
        page.Js("var s = document.getElementById('s').getContext('2d'); s.fillStyle = 'red'; s.fillRect(0, 0, 5, 10); s.fillStyle = 'blue'; s.fillRect(5, 0, 5, 10);" +
                "ctx.fillStyle = ctx.createPattern(document.getElementById('s'), 'repeat'); ctx.fillRect(0, 0, 100, 60); ''");
        Assert.Equal("255,0,0,255", page.Pixel(2, 3));
        Assert.Equal("0,0,255,255", page.Pixel(7, 3));
        Assert.Equal("255,0,0,255", page.Pixel(62, 33));
        Assert.Equal("0,0,255,255", page.Pixel(67, 33));
    }

    [Fact]
    public void PatternRepetitionModesRestrictTheTiling()
    {
        using var page = new Page(this, "<canvas id='c' width='100' height='60'></canvas><canvas id='s' width='10' height='10'></canvas>");
        page.Js("var s = document.getElementById('s').getContext('2d'); s.fillStyle = 'red'; s.fillRect(0, 0, 10, 10);" +
                "ctx.fillStyle = ctx.createPattern(document.getElementById('s'), 'repeat-x'); ctx.fillRect(0, 0, 100, 60); ''");
        Assert.Equal("255,0,0,255", page.Pixel(55, 5));
        Assert.Equal("0,0,0,0", page.Pixel(55, 15));
        page.Js("ctx.clearRect(0, 0, 100, 60); ctx.fillStyle = ctx.createPattern(document.getElementById('s'), 'no-repeat'); ctx.fillRect(0, 0, 100, 60); ''");
        Assert.Equal("255,0,0,255", page.Pixel(5, 5));
        Assert.Equal("0,0,0,0", page.Pixel(15, 5));
        Assert.Equal("SyntaxError", page.Js("try { ctx.createPattern(document.getElementById('s'), 'tile'); 'no' } catch (e) { e.name }"));
        Assert.True(page.Is("ctx.createPattern(document.getElementById('s'), null) !== null && ctx.createPattern(document.getElementById('s'), '') !== null"));
    }

    [Fact]
    public void ThePatternTransformMovesAndScalesTheTiles()
    {
        using var page = new Page(this, "<canvas id='c' width='100' height='60'></canvas><canvas id='s' width='10' height='10'></canvas>");
        page.Js("var s = document.getElementById('s').getContext('2d'); s.fillStyle = 'red'; s.fillRect(0, 0, 5, 10); s.fillStyle = 'blue'; s.fillRect(5, 0, 5, 10);" +
                "var p = ctx.createPattern(document.getElementById('s'), 'repeat'); p.setTransform({a: 2, d: 2}); ctx.fillStyle = p; ctx.fillRect(0, 0, 100, 60); ''");
        Assert.Equal("255,0,0,255", page.Pixel(8, 3));
        Assert.Equal("0,0,255,255", page.Pixel(12, 3));
    }

    [Fact]
    public void StrokesCanUseGradientsAndPatterns()
    {
        using var page = new Page(this);
        page.Js("var g = ctx.createLinearGradient(0, 0, 100, 0); g.addColorStop(0, 'red'); g.addColorStop(1, 'blue'); ctx.strokeStyle = g; ctx.lineWidth = 10; ctx.beginPath(); ctx.moveTo(0, 30); ctx.lineTo(100, 30); ctx.stroke(); ''");
        Assert.True(int.Parse(page.Pixel(5, 30).Split(',')[0]) > 200);
        Assert.True(int.Parse(page.Pixel(95, 30).Split(',')[2]) > 200);
    }

    // ---------------------------------------------------------------- #509: shadows and filters

    [Fact]
    public void ShadowsAreDrawnUnderTheShapeAtTheirOffset()
    {
        using var page = new Page(this);
        page.Js("ctx.shadowColor = 'rgb(0, 0, 255)'; ctx.shadowOffsetX = 20; ctx.shadowOffsetY = 10; ctx.fillStyle = 'red'; ctx.fillRect(10, 10, 20, 20); ''");
        Assert.Equal("255,0,0,255", page.Pixel(15, 15));
        Assert.Equal("0,0,255,255", page.Pixel(45, 35));
        Assert.Equal("0,0,0,0", page.Pixel(45, 10));
    }

    [Fact]
    public void ShadowBlurSpreadsTheShadow()
    {
        using var page = new Page(this);
        page.Js("ctx.shadowColor = 'black'; ctx.shadowBlur = 20; ctx.fillStyle = 'red'; ctx.fillRect(40, 20, 20, 20); ''");
        // soft: a pixel outside the shape is partly shaded, and the shade fades with distance
        var near = int.Parse(page.Pixel(38, 30).Split(',')[3]);
        var far = int.Parse(page.Pixel(25, 30).Split(',')[3]);
        Assert.True(near > 20 && near < 255, $"near: {near}");
        Assert.True(far < near, $"far: {far} near: {near}");
    }

    [Fact]
    public void ShadowsIgnoreTheTransformForTheirOffset()
    {
        using var page = new Page(this);
        page.Js("ctx.shadowColor = 'blue'; ctx.shadowOffsetX = 20; ctx.scale(3, 3); ctx.fillStyle = 'red'; ctx.fillRect(2, 2, 5, 5); ''");
        // the shape is at 6..21 and its shadow 20 pixels (not 60) to the right
        Assert.Equal("0,0,255,255", page.Pixel(35, 12));
        Assert.Equal("0,0,0,0", page.Pixel(60, 12));
    }

    [Fact]
    public void ATransparentShadowColorDrawsNoShadow()
    {
        using var page = new Page(this);
        page.Js("ctx.shadowOffsetX = 20; ctx.shadowBlur = 5; ctx.fillStyle = 'red'; ctx.fillRect(10, 10, 20, 20); ''");
        Assert.Equal("0,0,0,0", page.Pixel(40, 20));
    }

    [Fact]
    public void ShadowPropertiesIgnoreInvalidValuesAndSerializeTheColor()
    {
        using var page = new Page(this);
        Assert.Equal("0|0|0|rgba(0, 0, 0, 0)", page.Js("ctx.shadowBlur = -1; ctx.shadowBlur = NaN; ctx.shadowOffsetX = Infinity; ctx.shadowOffsetY = NaN; ctx.shadowColor = 'nope'; [ctx.shadowBlur, ctx.shadowOffsetX, ctx.shadowOffsetY, ctx.shadowColor].join('|')"));
        Assert.Equal("#ff0000", page.Js("ctx.shadowColor = 'red'; ctx.shadowColor"));
    }

    [Fact]
    public void ShadowsFollowGlobalAlphaAndCompositing()
    {
        using var page = new Page(this);
        page.Js("ctx.fillStyle = 'white'; ctx.fillRect(0, 0, 100, 60); ctx.shadowColor = 'black'; ctx.shadowOffsetX = 30; ctx.globalAlpha = 0.5; ctx.fillStyle = 'red'; ctx.fillRect(10, 10, 20, 20); ''");
        var shadow = page.Pixel(50, 20).Split(',').Select(int.Parse).ToArray();
        Assert.InRange(shadow[0], 126, 129);
    }

    [Fact]
    public void FilterBlursWhatIsDrawn()
    {
        using var page = new Page(this);
        page.Js("ctx.filter = 'blur(4px)'; ctx.fillStyle = 'red'; ctx.fillRect(40, 20, 20, 20); ''");
        var outside = int.Parse(page.Pixel(37, 30).Split(',')[3]);
        var inside = int.Parse(page.Pixel(50, 30).Split(',')[3]);
        Assert.True(outside > 5 && outside < 200, $"outside: {outside}");
        Assert.True(inside > 200, $"inside: {inside}");
    }

    [Fact]
    public void FilterColourFunctionsChangeColours()
    {
        using var page = new Page(this);
        page.Js("ctx.filter = 'grayscale(1)'; ctx.fillStyle = 'red'; ctx.fillRect(0, 0, 10, 10); ''");
        var gray = page.Pixel(5, 5).Split(',').Select(int.Parse).ToArray();
        Assert.True(Math.Abs(gray[0] - gray[1]) < 3 && Math.Abs(gray[1] - gray[2]) < 3, string.Join(",", gray));
        page.Js("ctx.filter = 'invert(1)'; ctx.fillStyle = '#ff0000'; ctx.fillRect(20, 0, 10, 10); ''");
        Assert.Equal("0,255,255,255", page.Pixel(25, 5));
    }

    [Fact]
    public void FilterIsParsedLikeTheCssPropertyAndInvalidValuesAreIgnored()
    {
        using var page = new Page(this);
        Assert.Equal("blur(2px) brightness(1.5)", page.Js("ctx.filter = 'blur(2px) brightness(1.5)'; ctx.filter"));
        Assert.Equal("blur(2px) brightness(1.5)", page.Js("ctx.filter = 'blur(red)'; ctx.filter = 'wobble(3)'; ctx.filter = ''; ctx.filter"));
        Assert.Equal("none", page.Js("ctx.filter = 'none'; ctx.filter"));
        Assert.Equal("blur(1px)", page.Js("ctx.save(); ctx.filter = 'blur(1px)'; var f = ctx.filter; ctx.restore(); f"));
        Assert.Equal("none", page.Js("ctx.filter"));
    }

    [Fact]
    public void DropShadowFilterDrawsAShadow()
    {
        using var page = new Page(this);
        page.Js("ctx.filter = 'drop-shadow(20px 0px 0px blue)'; ctx.fillStyle = 'red'; ctx.fillRect(10, 10, 20, 20); ''");
        Assert.Equal("255,0,0,255", page.Pixel(15, 15));
        Assert.Equal("0,0,255,255", page.Pixel(45, 15));
    }

    // ---------------------------------------------------------------- #510: text

    [Fact]
    public void FillTextDrawsGlyphs()
    {
        using var page = new Page(this, fonts: true);
        page.Js("ctx.font = '40px sans-serif'; ctx.fillStyle = 'black'; ctx.fillText('MMM', 5, 45); ''");
        var covered = Enumerable.Range(5, 70).Count(x => Enumerable.Range(10, 40).Any(y => int.Parse(page.Pixel(x, y).Split(',')[3]) > 128));
        Assert.True(covered > 30, $"columns with ink: {covered}");
    }

    [Fact]
    public void MeasureTextGivesAdvanceWidths()
    {
        using var page = new Page(this, fonts: true);
        page.Js("ctx.font = '20px sans-serif'; ''");
        var one = page.Num("ctx.measureText('M').width");
        var three = page.Num("ctx.measureText('MMM').width");
        Assert.True(one > 5, $"one: {one}");
        Assert.Equal(one * 3, three, 1);
        Assert.Equal(0, page.Num("ctx.measureText('').width"));
        page.Js("ctx.font = '40px sans-serif'; ''");
        Assert.Equal(one * 6, page.Num("ctx.measureText('M').width * 3"), 1);
    }

    [Fact]
    public void FontAcceptsTheCssShorthandAndIgnoresInvalidValues()
    {
        using var page = new Page(this, fonts: true);
        Assert.Equal("bold 12px serif", page.Js("ctx.font = 'bold 12px serif'; ctx.font"));
        Assert.Equal("bold 12px serif", page.Js("ctx.font = 'nonsense'; ctx.font = '12px; color: red'; ctx.font = ''; ctx.font"));
        Assert.Equal("10px sans-serif", page.Js("canvas.width = 100; ctx.font"));
    }

    [Fact]
    public void TextAlignMovesTheLineAroundTheAnchor()
    {
        using var page = new Page(this, fonts: true);
        Func<int> Ink = () => Enumerable.Range(0, 100).Count(x => Enumerable.Range(0, 60).Any(y => int.Parse(page.Pixel(x, y).Split(',')[3]) > 128));
        page.Js("ctx.font = '20px sans-serif'; ctx.textAlign = 'left'; ctx.fillText('MM', 50, 40); ''");
        Assert.Equal("0,0,0,0", page.Pixel(30, 30));
        Assert.True(Ink() > 5);
        page.Js("ctx.clearRect(0, 0, 100, 60); ctx.textAlign = 'right'; ctx.fillText('MM', 50, 40); ''");
        Assert.Equal("0,0,0,0", page.Pixel(70, 30));
        Assert.Equal("right", page.Js("ctx.textAlign"));
        Assert.Equal("right", page.Js("ctx.textAlign = 'wrong'; ctx.textAlign"));
    }

    [Fact]
    public void MaxWidthSqueezesWideText()
    {
        using var page = new Page(this, fonts: true);
        page.Js("ctx.font = '30px sans-serif'; ctx.fillText('MMMMMMMMMM', 0, 40, 40); ''");
        var rightmost = Enumerable.Range(0, 100).Last(x => Enumerable.Range(0, 60).Any(y => int.Parse(page.Pixel(x, y).Split(',')[3]) > 40));
        Assert.True(rightmost <= 42, $"rightmost ink at {rightmost}");
    }

    [Fact]
    public void StrokeTextOutlinesGlyphs()
    {
        using var page = new Page(this, fonts: true);
        page.Js("ctx.font = \"50px 'Source Sans 3'\"; ctx.lineWidth = 2; ctx.strokeStyle = 'black'; ctx.strokeText('O', 10, 50); ''");
        var ink = Enumerable.Range(0, 60).Count(x => Enumerable.Range(0, 60).Any(y => int.Parse(page.Pixel(x, y).Split(',')[3]) > 100));
        Assert.True(ink > 10);
        // an outline leaves the middle of the O empty
        var cx = (int)page.Num("10 + ctx.measureText('O').width / 2");
        Assert.Equal("0", page.Pixel(cx, 30).Split(',')[3]);
    }

    [Fact]
    public void TextBaselineChangesWhereTheLineSits()
    {
        using var page = new Page(this, fonts: true);
        int Top()
        {
            for (var y = 0; y < 60; y++)
                for (var x = 0; x < 100; x++)
                    if (int.Parse(page.Pixel(x, y).Split(',')[3]) > 128)
                        return y;
            return -1;
        }
        page.Js("ctx.font = '20px sans-serif'; ctx.textBaseline = 'alphabetic'; ctx.fillText('M', 10, 30); ''");
        var alphabetic = Top();
        page.Js("ctx.clearRect(0, 0, 100, 60); ctx.textBaseline = 'top'; ctx.fillText('M', 10, 30); ''");
        var top = Top();
        Assert.True(top >= 28 && top > alphabetic, $"top {top} alphabetic {alphabetic}");
    }

    [Fact]
    public void LetterAndWordSpacingWidenText()
    {
        using var page = new Page(this, fonts: true);
        page.Js("ctx.font = '20px sans-serif'; ''");
        var plain = page.Num("ctx.measureText('a b').width");
        Assert.Equal(plain + 6, page.Num("ctx.letterSpacing = '2px'; ctx.measureText('a b').width"), 1);
        Assert.Equal(plain + 6 + 10, page.Num("ctx.wordSpacing = '10px'; ctx.measureText('a b').width"), 1);
        Assert.Equal("2px", page.Js("ctx.letterSpacing = 'wide'; ctx.letterSpacing"));
        Assert.Equal("10px", page.Js("ctx.wordSpacing = '1 2'; ctx.wordSpacing"));
        // spacing is part of the state
        Assert.Equal("0px", page.Js("canvas.width = 100; ctx.letterSpacing"));
    }

    [Fact]
    public void RightToLeftTextIsShapedAndOrderedByTheBidiAlgorithm()
    {
        using var page = new Page(this, fonts: true);
        // Arabic and Hebrew draw, and measure the same as the same text in an inline box laid out by the engine
        foreach (var (family, text) in new[] { ("Noto Sans Arabic", "سلام"), ("Noto Sans Hebrew", "שלום") })
        {
            page.Js($"ctx.font = \"30px '{family}'\"; ctx.clearRect(0, 0, 100, 60); ctx.fillText('{text}', 10, 45); ''");
            var ink = Enumerable.Range(0, 100).Count(x => Enumerable.Range(0, 60).Any(y => int.Parse(page.Pixel(x, y).Split(',')[3]) > 100));
            Assert.True(ink > 15, $"{family}: {ink}");
        }
    }

    [Theory]
    [InlineData("Folio Box", "Hello, world 123")]
    [InlineData("Source Sans 3", "Fast waves, AVAVA fi")]
    [InlineData("Noto Sans Arabic", "سلام عليكم")]
    [InlineData("Noto Sans Hebrew", "שלום עולם")]
    [InlineData("Noto Sans JP", "こんにちは世界")]
    public void MeasureTextAgreesWithInlineLayoutForTheSameText(string family, string text)
    {
        Assert.SkipWhen(UsesSoftwareRaster && family == "Noto Sans Arabic", "The software backend still lacks Arabic mark positioning parity (#561).");
        using var page = new Page(this, $"<canvas id='c' width='100' height='60'></canvas><div style=\"position:absolute;top:0;left:0;white-space:nowrap\"><span id='t' style=\"font:30px '{family}'\">{text}</span></div>", fonts: true);
        page.Document.Paint(800, 600, 1, new HarfBuzzShaper());
        var span = page.Document.Node.Descendants.OfType<Folio.Dom.ElementNode>().Single(e => e.LocalName == "span");
        var inline = page.Document.GetElementBounds(span)!.Value.Width;
        var measured = page.Num($"ctx.font = \"30px '{family}'\"; ctx.measureText('{text}').width");
        Assert.True(measured > 10);
        Assert.InRange(measured, inline - 0.05, inline + 0.05);
    }

    [Fact]
    public void WithoutFontsTextIsMeasuredAndDrawsNothing()
    {
        using var page = new Page(this);
        page.Js("ctx.font = '20px sans-serif'; ctx.fillText('MMM', 5, 30); ''");
        Assert.Equal("0,0,0,0", page.Pixel(10, 20));
        Assert.True(page.Num("ctx.measureText('MMM').width") > 0);
    }

    // ---------------------------------------------------------------- the display list shows what was drawn

    [Fact]
    public void ThePageShowsTheCanvasContent()
    {
        using var page = new Page(this, "<canvas id='c' width='40' height='30' style='display:block'></canvas>");
        page.Js("ctx.fillStyle = 'red'; ctx.fillRect(0, 0, 40, 30); ''");
        var (list, _) = page.Document.Paint(200, 100);
        Assert.Contains(list.Items, item => item.Kind == Folio.Painting.DisplayItemKind.Image && item.Image is { Width: 40, Height: 30 });
    }

    [Fact]
    public void LaterDrawingChangesWhatThePageShows()
    {
        using var page = new Page(this, "<canvas id='c' width='4' height='4' style='display:block'></canvas>");
        page.Js("ctx.fillStyle = 'red'; ctx.fillRect(0, 0, 4, 4); ''");
        var first = page.Document.Paint(100, 100).List.Items.Single(item => item.Kind == Folio.Painting.DisplayItemKind.Image).Image!;
        page.Js("ctx.fillStyle = 'blue'; ctx.fillRect(0, 0, 4, 4); ''");
        var second = page.Document.Paint(100, 100).List.Items.Single(item => item.Kind == Folio.Painting.DisplayItemKind.Image).Image!;
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, first.Pixels.Span[..4].ToArray());
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, second.Pixels.Span[..4].ToArray());
    }
}
