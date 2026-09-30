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
        var (kind, reasons) = ArtifactClassifier.Explain(Report);

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
        Assert.Equal(ArtifactKind.Scripted, ArtifactClassifier.Classify(html));
    }

    [Theory]
    [InlineData("<script type=application/json>{}</script><script type=text/template><p></script>")]
    [InlineData("<a href=\"https://example.com/\">outbound links are fine</a>")]
    public void DataBlocksAndLinksAreStatic(string html)
    {
        Assert.Equal(ArtifactKind.Static, ArtifactClassifier.Classify(html));
    }

    [Theory]
    [InlineData("<link rel=stylesheet href=\"https://cdn.example.com/a.css\">", "stylesheet not loadable")]
    [InlineData("<img src=\"photo.png\">", "image not loadable")]
    [InlineData("<img srcset=\"data:image/png;base64,AA== 1x, big.png 2x\">", "image not loadable")]
    [InlineData("<style>@import url(\"https://fonts.example.com/css\");</style>", "stylesheet not loadable")]
    [InlineData("<div style=\"background: url(bg.jpg)\"></div>", "image not loadable")]
    [InlineData("<img src=\"data:image/gif;base64,R0lGOD==\">", "image format")]
    [InlineData("<svg width=10 height=10><circle r=5 /></svg>", "inline SVG")]
    [InlineData("<canvas></canvas>", "<canvas>")]
    [InlineData("<iframe srcdoc=x></iframe>", "<iframe>")]
    [InlineData("<div popover>menu</div>", "popover")]
    [InlineData("<style>.a { background: linear-gradient(red, blue) }</style>", "CSS gradients")]
    [InlineData("<style>.a { --bg: radial-gradient(red, blue) }</style>", "CSS gradients")]
    [InlineData("<style>.a { -webkit-background-clip: text }</style>", "CSS property: -webkit-background-clip")]
    [InlineData("<style>.a { background-clip: text }</style>", "background-clip: text")]
    [InlineData("<style>.a { mix-blend-mode: multiply }</style>", "CSS property: mix-blend-mode")]
    [InlineData("<style>.a { display: grid; width: 12 }</style>", "CSS value: width")]
    [InlineData("<style>input:required { color: red }</style>", "selector")]
    [InlineData("<style>@font-face { font-family: X; src: url(data:font/woff2;base64,AA==) }</style>", "@font-face")]
    [InlineData("<style>@container (min-width: 1px) { .a { color: red } }</style>", "@container")]
    [InlineData("<style>@media (max-width: 600px) { .a { filter: blur(2px) } }</style>", "CSS property: filter")]
    [InlineData("<style>.a { & .b { box-shadow: 0 1px red } }</style>", "CSS property: box-shadow")]
    public void UnsupportedContentNeedsTheBrowser(string html, string reason)
    {
        var (kind, reasons) = ArtifactClassifier.Explain(html);

        Assert.Equal(ArtifactKind.NeedsBrowser, kind);
        Assert.Contains(reasons, r => r.Contains(reason, StringComparison.Ordinal));
    }

    [Fact]
    public void RoutingOutWinsOverScripting()
    {
        var (kind, reasons) = ArtifactClassifier.Explain("<script src=\"https://cdn.example.com/lib.js\"></script>");

        Assert.Equal(ArtifactKind.NeedsBrowser, kind);
        Assert.Equal(["script not loadable with these options: https://cdn.example.com/lib.js", "script element"], reasons);
    }

    [Fact]
    public void ALimitHitNeedsTheBrowser()
    {
        var options = new FolioOptions { Limits = ResourceLimits.Default with { MaxNestingDepth = 4 } };

        Assert.Equal(ArtifactKind.NeedsBrowser, ArtifactClassifier.Classify("<div><div><div><div><div><div>deep", options));
    }
}
