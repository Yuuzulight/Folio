namespace Folio.Tests.Hosting;

public class ArtifactClassifierTests
{
    private const string Report = """
        <!DOCTYPE html><html><head><style>
        :root { --accent: #2563eb; }
        body { font: 16px/1.5 system-ui, sans-serif; -webkit-font-smoothing: antialiased; }
        a { color: var(--accent); text-decoration: none; transition: color .2s; cursor: pointer; }
        .card { display: grid; grid-template-columns: repeat(auto-fit, minmax(200px, 1fr)); gap: 16px; }
        .card:hover { transform: translateY(-2px); }
        ::-webkit-scrollbar { width: 8px; }
        @media print { .card { box-shadow: 0 0 0 red; } }
        </style></head><body><h1>Report</h1><p class="card" style="padding: 8px">Text <a href="https://example.com/">link</a>
        <img src="data:image/png;base64,iVBORw0KGgo=" alt="chart"></body></html>
        """;

    [Fact]
    public void AStaticReportIsStatic()
    {
        // Interaction-only declarations, hover rules, vendor selectors, print rules, no-op resets and outbound links
        // all leave it static.
        var (kind, reasons) = ArtifactClassifier.Classify(Report);

        Assert.Empty(reasons);
        Assert.Equal(ArtifactKind.Static, kind);
    }

    [Theory]
    [InlineData("<p>x<script>document.title = 'y'</script>")]
    [InlineData("<p>x<script type=module>import './a.js'</script>")]
    [InlineData("<button onclick=\"go()\">Go</button>")]
    [InlineData("<a href=\" javascript:void(0)\">x</a>")]
    public void ScriptsMakeItScripted(string html)
    {
        Assert.Equal(ArtifactKind.Scripted, ArtifactClassifier.Classify(html).Kind);
    }

    [Theory]
    [InlineData("<script type=application/json>{}</script><script type=text/template><p></script>")]
    [InlineData("<a href=\"https://example.com/\">outbound links are fine</a>")]
    public void DataBlocksAndLinksAreStatic(string html)
    {
        Assert.Equal(ArtifactKind.Static, ArtifactClassifier.Classify(html).Kind);
    }

    [Theory]
    [InlineData("<link rel=stylesheet href=\"https://cdn.example.com/a.css\">", "loads a stylesheet these options do not allow")]
    [InlineData("<img src=\"photo.png\">", "loads an image these options do not allow")]
    [InlineData("<img srcset=\"data:image/png;base64,AA== 1x, big.png 2x\">", "loads an image these options do not allow")]
    [InlineData("<style>@import url(\"https://fonts.example.com/css\");</style>", "loads a stylesheet these options do not allow")]
    [InlineData("<div style=\"background: url(bg.jpg)\"></div>", "loads an image these options do not allow")]
    [InlineData("<img src=\"data:image/gif;base64,R0lGOD==\">", "uses an image format Folio does not decode: image/gif")]
    [InlineData("<svg width=10 height=10><circle r=5 /></svg>", "uses inline SVG")]
    [InlineData("<canvas></canvas>", "uses <canvas>")]
    [InlineData("<iframe srcdoc=x></iframe>", "uses <iframe>")]
    [InlineData("<div popover>menu</div>", "uses popovers")]
    [InlineData("<style>.a { background: linear-gradient(red, blue) }</style>", "uses CSS gradients")]
    [InlineData("<style>.a { --bg: radial-gradient(red, blue) }</style>", "uses CSS gradients")]
    [InlineData("<style>.a { -webkit-background-clip: text }</style>", "uses the CSS property -webkit-background-clip")]
    [InlineData("<style>.a { background-clip: text }</style>", "uses background-clip: text")]
    [InlineData("<style>.a { mix-blend-mode: multiply }</style>", "uses the CSS property mix-blend-mode")]
    [InlineData("<style>.a { display: grid; width: 12 }</style>", "uses a CSS value Folio does not support: width: 12")]
    [InlineData("<style>input:required { color: red }</style>", "uses a CSS selector Folio does not support: input:required")]
    [InlineData("<style>@font-face { font-family: X; src: url(data:font/woff2;base64,AA==) }</style>", "uses web fonts (@font-face)")]
    [InlineData("<style>@container (min-width: 1px) { .a { color: red } }</style>", "uses @container")]
    [InlineData("<style>@media (max-width: 600px) { .a { filter: url(#glow) } }</style>", "uses a CSS value Folio does not support: filter: url(#glow)")]
    [InlineData("<style>.a { & .b { clip-path: circle() } }</style>", "uses the CSS property clip-path")]
    public void UnsupportedContentNeedsTheBrowser(string html, string reason)
    {
        var (kind, reasons) = ArtifactClassifier.Classify(html);

        Assert.Equal(ArtifactKind.NeedsBrowser, kind);
        Assert.Contains(reasons, r => r.Contains(reason, StringComparison.Ordinal));
    }

    [Fact]
    public void RoutingOutWinsOverScripting()
    {
        var (kind, reasons) = ArtifactClassifier.Classify("<script src=\"https://cdn.example.com/lib.js\"></script>");

        Assert.Equal(ArtifactKind.NeedsBrowser, kind);
        Assert.Equal(["loads a script these options do not allow: https://cdn.example.com/lib.js", "runs scripts: a script element"], reasons);
    }

    [Fact]
    public void ALimitHitNeedsTheBrowser()
    {
        var options = new FolioOptions { Limits = ResourceLimits.Default with { MaxNestingDepth = 4 } };

        Assert.Equal(ArtifactKind.NeedsBrowser, ArtifactClassifier.Classify("<div><div><div><div><div><div>deep", options).Kind);
    }
}
