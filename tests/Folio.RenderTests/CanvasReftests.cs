using Folio.Scripting;
using Folio.Scripting.Dom;
using Folio.Scripting.Jint;
using Folio.Skia;

namespace Folio.RenderTests;

/// <summary>
/// Canvas 2D drawing against the same shapes as SVG (issues #504 to #510): the script draws on a canvas, an inline SVG
/// holds the equivalent elements, and the two pages must render alike.
/// </summary>
public class CanvasReftests
{
    private const string Page = "<!DOCTYPE html><html><body style='margin:0;background:white'>{0}</body></html>";

    private static FolioOptions Options { get; } = new()
    {
        Fonts = TestRenderer.Options.Fonts,
        UserStyleSheet = "*, *::before, *::after { font-family: 'Source Sans 3' !important; }",
        Scripting = ScriptingPolicy.ContentProcess,
        Raster = new SkiaRasterBackend(),
    };

    private static PixelBuffer Render(string body, string? script = null)
    {
        using var document = Document.Parse(string.Format(Page, body), Options);
        document.Paint(200, 120);
        if (script is not null)
        {
            // Keep the script engine alive through the render. Disposing the
            // engine retires the document's canvas surfaces; rendering after
            // that point produced a blank canvas and made every reftest fail.
            using var engine = new JintScriptEngine();
            DomBindings.Install(engine, document);
            engine.Evaluate("var canvas = document.getElementById('c'); var ctx = canvas.getContext('2d');\n" + script);
            using var scriptedResult = HeadlessRenderer.Render(document, new RenderRequest(200, 120));
            return new PixelBuffer(scriptedResult.Width, scriptedResult.Height, scriptedResult.Pixels.ToArray());
        }
        using var result = HeadlessRenderer.Render(document, new RenderRequest(200, 120));
        return new PixelBuffer(result.Width, result.Height, result.Pixels.ToArray());
    }

    private const string CanvasTag = "<canvas id='c' width='200' height='120' style='display:block'></canvas>";

    private static string Svg(string content) => $"<svg width='200' height='120' style='display:block' xmlns='http://www.w3.org/2000/svg'>{content}</svg>";

    public static TheoryData<string> Cases => new(All.Keys);

    private static readonly Dictionary<string, (string Script, string Svg)> All = new()
    {
        ["fillRect"] = ("ctx.fillStyle = '#d33'; ctx.fillRect(20, 20, 60, 40);",
            "<rect x='20' y='20' width='60' height='40' fill='#d33'/>"),
        ["strokeRect"] = ("ctx.strokeStyle = '#339'; ctx.lineWidth = 6; ctx.strokeRect(30, 30, 80, 50);",
            "<rect x='30' y='30' width='80' height='50' fill='none' stroke='#339' stroke-width='6'/>"),
        ["circle"] = ("ctx.fillStyle = '#2a7'; ctx.beginPath(); ctx.arc(100, 60, 40, 0, Math.PI * 2); ctx.fill();",
            "<circle cx='100' cy='60' r='40' fill='#2a7'/>"),
        ["ellipse"] = ("ctx.fillStyle = '#a2a'; ctx.beginPath(); ctx.ellipse(100, 60, 70, 30, 0, 0, Math.PI * 2); ctx.fill();",
            "<ellipse cx='100' cy='60' rx='70' ry='30' fill='#a2a'/>"),
        ["rotatedEllipse"] = ("ctx.fillStyle = '#a2a'; ctx.beginPath(); ctx.ellipse(100, 60, 60, 20, Math.PI / 6, 0, Math.PI * 2); ctx.fill();",
            "<ellipse cx='100' cy='60' rx='60' ry='20' fill='#a2a' transform='rotate(30 100 60)'/>"),
        ["roundRect"] = ("ctx.fillStyle = '#c80'; ctx.beginPath(); ctx.roundRect(20, 20, 160, 80, 24); ctx.fill();",
            "<rect x='20' y='20' width='160' height='80' rx='24' ry='24' fill='#c80'/>"),
        ["curves"] = ("ctx.strokeStyle = '#000'; ctx.lineWidth = 3; ctx.beginPath(); ctx.moveTo(10, 100); ctx.quadraticCurveTo(60, 0, 110, 100); ctx.bezierCurveTo(130, 20, 170, 20, 190, 100); ctx.stroke();",
            "<path d='M10 100 Q60 0 110 100 C130 20 170 20 190 100' fill='none' stroke='#000' stroke-width='3'/>"),
        ["miterJoin"] = ("ctx.strokeStyle = '#000'; ctx.lineWidth = 12; ctx.lineJoin = 'miter'; ctx.beginPath(); ctx.moveTo(30, 100); ctx.lineTo(100, 20); ctx.lineTo(170, 100); ctx.stroke();",
            "<path d='M30 100 L100 20 L170 100' fill='none' stroke='#000' stroke-width='12' stroke-linejoin='miter'/>"),
        ["roundJoinAndCap"] = ("ctx.strokeStyle = '#000'; ctx.lineWidth = 14; ctx.lineJoin = 'round'; ctx.lineCap = 'round'; ctx.beginPath(); ctx.moveTo(30, 100); ctx.lineTo(100, 20); ctx.lineTo(170, 100); ctx.stroke();",
            "<path d='M30 100 L100 20 L170 100' fill='none' stroke='#000' stroke-width='14' stroke-linejoin='round' stroke-linecap='round'/>"),
        ["dashes"] = ("ctx.strokeStyle = '#000'; ctx.lineWidth = 6; ctx.setLineDash([14, 8]); ctx.lineDashOffset = 5; ctx.beginPath(); ctx.moveTo(10, 60); ctx.lineTo(190, 60); ctx.stroke();",
            "<path d='M10 60 L190 60' fill='none' stroke='#000' stroke-width='6' stroke-dasharray='14 8' stroke-dashoffset='5'/>"),
        ["evenOdd"] = ("ctx.fillStyle = '#369'; ctx.beginPath(); ctx.rect(20, 20, 160, 80); ctx.rect(60, 40, 80, 40); ctx.fill('evenodd');",
            "<path d='M20 20 H180 V100 H20 Z M60 40 H140 V80 H60 Z' fill='#369' fill-rule='evenodd'/>"),
        ["transforms"] = ("ctx.fillStyle = '#d33'; ctx.translate(100, 60); ctx.rotate(Math.PI / 5); ctx.scale(1.5, 0.75); ctx.fillRect(-40, -30, 80, 60);",
            "<rect x='-40' y='-30' width='80' height='60' fill='#d33' transform='translate(100 60) rotate(36) scale(1.5 0.75)'/>"),
        ["globalAlpha"] = ("ctx.globalAlpha = 0.5; ctx.fillStyle = '#d00'; ctx.fillRect(20, 20, 100, 60); ctx.fillStyle = '#00d'; ctx.fillRect(70, 40, 100, 60);",
            "<rect x='20' y='20' width='100' height='60' fill='#d00' fill-opacity='0.5'/><rect x='70' y='40' width='100' height='60' fill='#00d' fill-opacity='0.5'/>"),
        ["clip"] = ("ctx.beginPath(); ctx.arc(100, 60, 40, 0, Math.PI * 2); ctx.clip(); ctx.fillStyle = '#d33'; ctx.fillRect(0, 0, 200, 60); ctx.fillStyle = '#33d'; ctx.fillRect(0, 60, 200, 60);",
            "<defs><clipPath id='k'><circle cx='100' cy='60' r='40'/></clipPath></defs><g clip-path='url(#k)'><rect width='200' height='60' fill='#d33'/><rect y='60' width='200' height='60' fill='#33d'/></g>"),
        ["linearGradient"] = ("var g = ctx.createLinearGradient(20, 0, 180, 0); g.addColorStop(0, '#f00'); g.addColorStop(0.5, '#0f0'); g.addColorStop(1, '#00f'); ctx.fillStyle = g; ctx.fillRect(20, 20, 160, 80);",
            "<defs><linearGradient id='g' gradientUnits='userSpaceOnUse' x1='20' y1='0' x2='180' y2='0'><stop offset='0' stop-color='#f00'/><stop offset='0.5' stop-color='#0f0'/><stop offset='1' stop-color='#00f'/></linearGradient></defs><rect x='20' y='20' width='160' height='80' fill='url(#g)'/>"),
        ["radialGradient"] = ("var g = ctx.createRadialGradient(100, 60, 5, 100, 60, 50); g.addColorStop(0, '#fff'); g.addColorStop(1, '#036'); ctx.fillStyle = g; ctx.fillRect(0, 0, 200, 120);",
            "<defs><radialGradient id='g' gradientUnits='userSpaceOnUse' cx='100' cy='60' fx='100' fy='60' r='50' fr='5'><stop offset='0' stop-color='#fff'/><stop offset='1' stop-color='#036'/></radialGradient></defs><rect width='200' height='120' fill='url(#g)'/>"),
        ["text"] = ("ctx.fillStyle = '#000'; ctx.font = \"40px 'Source Sans 3'\"; ctx.fillText('Folio 42', 20, 70);",
            "<text x='20' y='70' font-family='Source Sans 3' font-size='40' fill='#000'>Folio 42</text>"),
    };

    // ---- references that are HTML and CSS rather than SVG: blend modes, conic gradients, shadows and filters

    private const string Stage = "<div style='position:relative;width:200px;height:120px;isolation:isolate'>{0}</div>";

    private static string Box(int x, int y, int width, int height, string style) =>
        $"<div style='position:absolute;left:{x}px;top:{y}px;width:{width}px;height:{height}px;{style}'></div>";

    private static readonly string[] BlendModes =
    [
        "multiply", "screen", "overlay", "darken", "lighten", "color-dodge", "color-burn", "hard-light", "soft-light",
        "difference", "exclusion", "hue", "saturation", "color", "luminosity",
    ];

    private static readonly Dictionary<string, (string Script, string Html)> Css = BuildCss();

    private static Dictionary<string, (string, string)> BuildCss()
    {
        var cases = new Dictionary<string, (string, string)>();
        foreach (var mode in BlendModes)
        {
            cases["blend-" + mode] = (
                $"ctx.fillStyle = '#d9803a'; ctx.fillRect(20, 20, 100, 70); ctx.globalCompositeOperation = '{mode}'; ctx.fillStyle = '#3a8ed9'; ctx.fillRect(60, 40, 100, 70);",
                string.Format(Stage, Box(20, 20, 100, 70, "background:#d9803a") + Box(60, 40, 100, 70, $"background:#3a8ed9;mix-blend-mode:{mode}")));
        }
        cases["conicGradient"] = (
            "var g = ctx.createConicGradient(0, 100, 60); g.addColorStop(0, '#f00'); g.addColorStop(0.5, '#00f'); g.addColorStop(1, '#f00'); ctx.fillStyle = g; ctx.fillRect(0, 0, 200, 120);",
            string.Format(Stage, Box(0, 0, 200, 120, "background:conic-gradient(from 90deg at 100px 60px, #f00, #00f, #f00)")));
        cases["shadow"] = (
            "ctx.shadowColor = 'rgba(0, 0, 0, 0.5)'; ctx.shadowBlur = 16; ctx.shadowOffsetX = 10; ctx.shadowOffsetY = 12; ctx.fillStyle = '#4a9'; ctx.fillRect(40, 20, 80, 50);",
            string.Format(Stage, Box(40, 20, 80, 50, "background:#4a9;box-shadow:10px 12px 16px rgba(0,0,0,0.5)")));
        cases["hardShadow"] = (
            "ctx.shadowColor = '#c33'; ctx.shadowOffsetX = -14; ctx.shadowOffsetY = 9; ctx.fillStyle = '#39c'; ctx.fillRect(70, 20, 80, 50);",
            string.Format(Stage, Box(70, 20, 80, 50, "background:#39c;box-shadow:-14px 9px 0 #c33")));
        cases["filterBlur"] = (
            "ctx.filter = 'blur(4px)'; ctx.fillStyle = '#c63'; ctx.fillRect(50, 30, 100, 50);",
            string.Format(Stage, Box(50, 30, 100, 50, "background:#c63;filter:blur(4px)")));
        cases["filterColour"] = (
            "ctx.filter = 'grayscale(0.6) contrast(1.4) hue-rotate(40deg)'; ctx.fillStyle = '#c63'; ctx.fillRect(50, 30, 100, 50);",
            string.Format(Stage, Box(50, 30, 100, 50, "background:#c63;filter:grayscale(0.6) contrast(1.4) hue-rotate(40deg)")));
        cases["filterDropShadow"] = (
            "ctx.filter = 'drop-shadow(8px 8px 4px rgba(0, 0, 0, 0.6))'; ctx.fillStyle = '#39c'; ctx.fillRect(40, 20, 90, 50);",
            string.Format(Stage, Box(40, 20, 90, 50, "background:#39c;filter:drop-shadow(8px 8px 4px rgba(0,0,0,0.6))")));
        return cases;
    }

    public static TheoryData<string> CssCases => new(Css.Keys);

    [Theory]
    [MemberData(nameof(CssCases))]
    public void CanvasDrawingMatchesTheSameThingInCss(string name)
    {
        var (script, html) = Css[name];
        var outputBase = Path.Combine(RenderOutput.Root, "canvas-reftests", name);
        RenderOutput.Clear(outputBase);
        var expected = Render(html);
        var actual = Render(CanvasTag, script);
        var result = ImageComparer.Compare(expected, actual, new Tolerance(24, 0.01));
        if (!result.Passed)
            RenderOutput.Write(outputBase, actual, expected, result.Diff);
        Assert.True(result.Passed, $"{name}: {result.DifferingRatio * 100:F2}% of pixels differ");
    }

    [Fact]
    public void ACanvasPatternMatchesAnSvgPattern()
    {
        const string Script = "var s = document.getElementById('s').getContext('2d'); s.fillStyle = '#d33'; s.fillRect(0, 0, 5, 10); s.fillStyle = '#33d'; s.fillRect(5, 0, 5, 10);"
            + "ctx.fillStyle = ctx.createPattern(document.getElementById('s'), 'repeat'); ctx.fillRect(15, 15, 170, 90);";
        var svg = Svg("<defs><pattern id='p' width='10' height='10' patternUnits='userSpaceOnUse'><rect width='5' height='10' fill='#d33'/><rect x='5' width='5' height='10' fill='#33d'/></pattern></defs><rect x='15' y='15' width='170' height='90' fill='url(#p)'/>");
        var expected = Render(svg);
        var actual = Render(CanvasTag + "<canvas id='s' width='10' height='10' style='display:none'></canvas>", Script);
        var result = ImageComparer.Compare(expected, actual, new Tolerance(24, 0.01));
        if (!result.Passed)
            RenderOutput.Write(Path.Combine(RenderOutput.Root, "canvas-reftests", "pattern"), actual, expected, result.Diff);
        Assert.True(result.Passed, $"pattern: {result.DifferingRatio * 100:F2}% of pixels differ");
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void CanvasDrawingMatchesTheSameShapesInSvg(string name)
    {
        var (script, svg) = All[name];
        var outputBase = Path.Combine(RenderOutput.Root, "canvas-reftests", name);
        RenderOutput.Clear(outputBase);
        var expected = Render(Svg(svg));
        var actual = Render(CanvasTag, script);
        var result = ImageComparer.Compare(expected, actual, new Tolerance(24, 0.01));
        if (!result.Passed)
            RenderOutput.Write(outputBase, actual, expected, result.Diff);
        Assert.True(result.Passed, $"{name}: {result.DifferingRatio * 100:F2}% of pixels differ");
    }
}
