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
    [InlineData("<form><input type=checkbox><input type=radio><input type=range><select><option>A</option></select><textarea></textarea><button>Send</button></form>")]
    [InlineData("<details open><summary>Details</summary><p>More info</p></details>")]
    [InlineData("<style>input:focus-visible { outline: 2px solid blue; } input:checked { accent-color: red; }</style><input type=checkbox>")]
    [InlineData("<div style=\"overflow: auto; width: 100px; height: 100px\"><div style=\"width: 200px; height: 200px\">Scroll</div></div>")]
    public void InteractiveStaticElementsAreStatic(string html)
    {
        var (kind, reasons) = ArtifactClassifier.Classify(html);
        Assert.Empty(reasons);
        Assert.Equal(ArtifactKind.Static, kind);
    }



    [Theory]
    [InlineData("<link rel=stylesheet href=\"https://cdn.example.com/a.css\">", "loads a stylesheet these options do not allow")]
    [InlineData("<img src=\"photo.png\">", "loads an image these options do not allow")]
    [InlineData("<img srcset=\"data:image/png;base64,AA== 1x, big.png 2x\">", "loads an image these options do not allow")]
    [InlineData("<style>@import url(\"https://fonts.example.com/css\");</style>", "loads a stylesheet these options do not allow")]
    [InlineData("<div style=\"background: url(bg.jpg)\"></div>", "loads an image these options do not allow")]
    [InlineData("<img src=\"data:image/gif;base64,R0lGOD==\">", "uses an image format Folio does not decode: image/gif")]
    [InlineData("<svg><defs><pattern id=p /></defs><rect fill=\"url(#p)\" /></svg>", "uses SVG <pattern>")]
    [InlineData("<svg><filter id=f><feImage href=\"#r\" /></filter><rect id=r filter=\"url(#f)\" /></svg>", "uses an SVG <feImage> of an element")]
    [InlineData("<svg><foreignObject><p>x</p></foreignObject></svg>", "uses SVG <foreignObject>")]
    [InlineData("<svg><rect><animate attributeName=x /></rect></svg>", "uses SVG <animate>")]
    [InlineData("<svg><text rotate=\"10\">a</text></svg>", "uses the SVG text attribute rotate")]
    [InlineData("<svg><g stroke=\"red\"><text>a</text></g></svg>", "uses stroked SVG text")]
    [InlineData("<svg><path vector-effect=\"non-scaling-stroke\" /></svg>", "uses vector-effect")]
    [InlineData("<iframe srcdoc=x></iframe>", "uses <iframe>")]
    [InlineData("<div popover>menu</div>", "uses popovers")]
    [InlineData("<style>.a { background: linear-gradient(red, blue) }</style>", "uses CSS gradients")]
    [InlineData("<style>.a { --bg: radial-gradient(red, blue) }</style>", "uses CSS gradients")]
    [InlineData("<style>.a { -webkit-background-clip: text }</style>", "uses the CSS property -webkit-background-clip")]
    [InlineData("<style>.a { background-clip: text }</style>", "uses background-clip: text")]
    [InlineData("<style>.a { shape-outside: circle() }</style>", "uses the CSS property shape-outside")]
    [InlineData("<style>.a { display: grid; width: 12 }</style>", "uses a CSS value Folio does not support: width: 12")]
    [InlineData("<style>input:required { color: red }</style>", "uses a CSS selector Folio does not support: input:required")]
    [InlineData("<style>@font-face { font-family: X; src: url(data:font/woff2;base64,AA==) }</style>", "uses web fonts (@font-face)")]
    [InlineData("<style>@container (min-width: 1px) { .a { color: red } }</style>", "uses @container")]
    [InlineData("<style>.a { & .b { offset-path: ray(45deg) } }</style>", "uses the CSS property offset-path")]
    public void UnsupportedContentNeedsTheBrowser(string html, string reason)
    {
        var (kind, reasons) = ArtifactClassifier.Classify(html);

        Assert.Equal(ArtifactKind.NeedsBrowser, kind);
        Assert.Contains(reasons, r => r.Contains(reason, StringComparison.Ordinal));
    }

    [Fact]
    public void CanvasMarkupIsSupportedByM5()
    {
        Assert.Equal(ArtifactKind.Static, ArtifactClassifier.Classify("<canvas width='20' height='20'></canvas>").Kind);
    }

    [Theory]
    [InlineData("<canvas id=c></canvas><script>c.getContext('webgl2')</script>", "WebGL", "WebGL")]
    [InlineData("<canvas id=c></canvas><script>c.getContext('experimental-webgl')</script>", "WebGL", "WebGL")]
    [InlineData("<video src='movie.mp4'></video>", "<video>", "media")]
    [InlineData("<script>new Worker('worker.js')</script>", "workers", "workers")]
    [InlineData("<script>WebAssembly.instantiate(bytes)</script>", "WebAssembly", "WebAssembly")]
    [InlineData("<iframe src='child.html'></iframe>", "<iframe>", "iframes")]
    [InlineData("<script>navigator.mediaDevices.getUserMedia({})</script>", "WebRTC", "WebRTC")]
    [InlineData("<script>navigator.serviceWorker.register('sw.js')</script>", "service workers", "service workers")]
    public void StageSixFeaturesRequireAnExplicitHostCapability(string html, string feature, string capability)
    {
        var result = ArtifactClassifier.Classify(html);

        Assert.Equal(ArtifactKind.NeedsBrowser, result.Kind);
        Assert.Contains(result.Reasons, reason => reason.Contains(feature, StringComparison.Ordinal)
            && reason.Contains(capability, StringComparison.Ordinal));
    }

    [Fact]
    public void GrantedStageSixCapabilitiesAreNotRejectedByTheClassifier()
    {
        var options = new FolioOptions
        {
            Capabilities = new FeatureCapabilities
            {
                WebGl = true, Media = true, Workers = true, WebAssembly = true,
                Iframes = true, WebRtc = true, ServiceWorkers = true
            }
        };

        var result = ArtifactClassifier.Classify("<video></video><iframe></iframe><script>new Worker('w'); WebAssembly.instantiate(x); new RTCPeerConnection(); navigator.serviceWorker.register('s');</script>", options);

        Assert.DoesNotContain(result.Reasons, reason => reason.Contains("capability", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("<p>Icon <svg width=16 height=16 viewBox=\"0 0 24 24\" fill=\"none\" stroke=\"currentColor\" stroke-width=2><title>t</title>"
        + "<path d=\"M3 12h18\" /><circle cx=12 cy=12 r=9 /></svg></p>")]
    [InlineData("<svg viewBox=\"0 0 100 50\"><defs><style>.bar { fill: #6366f1 }</style></defs><g><rect class=bar width=10 height=40 />"
        + "<text x=5 y=48 text-anchor=middle dominant-baseline=hanging font-size=8>Q1</text></g></svg>")]
    [InlineData("<?xml version=\"1.0\"?><svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 10 10\"><rect width=\"5\" height=\"5\" /></svg>")]
    [InlineData("<svg><defs><linearGradient id=g x2=\"0\" y2=\"1\"><stop offset=0 stop-color=red /><stop offset=1 style=\"stop-color: blue\" /></linearGradient>"
        + "<radialGradient id=h href=\"#g\" /></defs><style>circle { stroke: url(#h) }</style><rect fill=\"url(#g)\" /><circle r=5 /></svg>")]
    [InlineData("<svg><clipPath id=c clipPathUnits=objectBoundingBox><circle cx=.5 cy=.5 r=.5 clip-rule=evenodd /></clipPath><rect width=5 height=5 clip-path=\"url(#c)\" /></svg>")]
    [InlineData("<style>.a { clip-path: url(#c) }</style><svg width=0 height=0><clipPath id=c><circle r=5 /></clipPath></svg><div class=a></div>")]
    [InlineData("<svg><filter id=f><feGaussianBlur stdDeviation=2 result=b /><feMerge><feMergeNode in=b /><feMergeNode in=SourceGraphic /></feMerge></filter><rect width=5 height=5 filter=\"url(#f)\" /></svg>")]
    [InlineData("<style>.a { filter: url(#f) drop-shadow(0 2px 4px black) }</style><svg width=0 height=0><filter id=f><feGaussianBlur stdDeviation=2 /></filter></svg><div class=a></div>")]
    [InlineData("<svg><filter id=f><feTile /><feConvolveMatrix kernelMatrix=\"1 0 0 0 1 0 0 0 1\" /><feDiffuseLighting><feDistantLight /></feDiffuseLighting>"
        + "<feSpecularLighting><fePointLight /></feSpecularLighting><feDiffuseLighting><feSpotLight /></feDiffuseLighting>"
        + "<feImage href=\"data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAQAAAACCAYAAAB/qH1jAAAAEklEQVR4nGP4z8DwH4SRIKoAAAslD/HAvA0nAAAAAElFTkSuQmCC\" /></filter><rect width=5 height=5 filter=\"url(#f)\" /></svg>")]
    [InlineData("<svg><mask id=m maskUnits=userSpaceOnUse style=\"mask-type: alpha\"><rect width=5 height=5 fill=white /></mask><rect width=5 height=5 mask=\"url(#m)\" /></svg>")]
    [InlineData("<svg><defs><symbol id=i viewBox=\"0 0 24 24\"><path d=\"M0 0h24\" /></symbol></defs><use href=\"#i\" width=16 height=16 fill=red /></svg>")]
    [InlineData("<svg><marker id=a orient=auto-start-reverse markerWidth=6 markerHeight=6 refX=3 refY=3><path d=\"M0 0L6 3L0 6z\" /></marker>"
        + "<line x2=50 stroke=black marker-start=\"url(#a)\" marker-end=\"url(#a)\" /></svg>")]
    public void SvgShapesPathsAndTextAreStatic(string artifact)
    {
        var (kind, reasons) = ArtifactClassifier.Classify(artifact);

        Assert.Empty(reasons);
        Assert.Equal(ArtifactKind.Static, kind);
    }

    [Fact]
    public void AStandaloneSvgWithAScriptIsScripted()
    {
        var (kind, reasons) = ArtifactClassifier.Classify(
            "<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script><rect width=\"5\" height=\"5\" /></svg>");

        Assert.Equal(ArtifactKind.Scripted, kind);
        Assert.Equal(["runs scripts: a script element in SVG"], reasons);
    }

    [Theory]
    [InlineData("text/babel", "JSX")]
    [InlineData("text/jsx", "JSX")]
    [InlineData("application/jsx", "JSX")]
    [InlineData("text/tsx", "JSX")]
    [InlineData("text/typescript", "TypeScript")]
    [InlineData("application/typescript", "TypeScript")]
    public void ScriptsThatNeedAnInPageCompilerAreRoutedToTheBrowserWithAReason(string type, string language)
    {
        var (kind, reasons) = ArtifactClassifier.Classify($"<div id=root></div><script type=\"{type}\">const x = <b>hi</b>;</script>");

        Assert.Equal(ArtifactKind.NeedsBrowser, kind);
        Assert.Contains($"uses in-page {language}; {language} must be compiled by the host", reasons);
    }

    [Fact]
    public void TheHostCompiledFormOfAJsxArtifactIsAnOrdinaryScript()
    {
        // What the host hands over in place of the JSX script: plain JavaScript.
        var (kind, reasons) = ArtifactClassifier.Classify("<div id=root></div><script>ReactDOM.render(React.createElement('b', null, 'hi'), document.getElementById('root'));</script>");

        Assert.Equal(ArtifactKind.Scripted, kind);
        Assert.DoesNotContain(reasons, r => r.Contains("compiled by the host"));
    }

    [Theory]
    [InlineData("const out = Babel.transform(src, { presets: ['react'] }).code;")]
    [InlineData("Babel.transformScriptTags();")]
    [InlineData("const js = ts.transpileModule(src, {}).outputText;")]
    public void CallsToAnInPageCompilerAreRoutedToTheBrowser(string script)
    {
        var (kind, reasons) = ArtifactClassifier.Classify($"<script>{script}</script>");

        Assert.Equal(ArtifactKind.NeedsBrowser, kind);
        Assert.Contains("compiles code in the page; JSX and TypeScript must be compiled by the host", reasons);
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
