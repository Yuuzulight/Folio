using System.Text;
using Folio.Css;
using Folio.Dom;
using Folio.Resources;

namespace Folio;

/// <summary>Where a host should show an artifact (docs/architecture.md, public API sketch).</summary>
public enum ArtifactKind
{
    /// <summary>Folio renders it fully.</summary>
    Static,

    /// <summary>It runs scripts, which Folio runs from milestone 4; until then the host shows it in the system browser.</summary>
    Scripted,

    /// <summary>It needs something Folio does not do yet: hand it to the system browser.</summary>
    NeedsBrowser,
}

/// <summary>Where a host should show an artifact, and why.</summary>
/// <param name="Reasons">
/// Short notes a host can show, such as "uses SVG <mask>" or "uses MathML": what routes the artifact out
/// first, then what needs scripting. Empty for <see cref="ArtifactKind.Static"/>. Written for people, not for parsing.
/// </param>
public sealed record ArtifactClassification(ArtifactKind Kind, IReadOnlyList<string> Reasons);

/// <summary>
/// Decides, without rendering, whether Folio can show an artifact at the current milestone (docs/roadmap.md: until a
/// milestone lands, artifacts that need it go to the system browser). The scan is conservative: anything Folio would
/// show differently from a browser routes the artifact out.
/// </summary>
/// <remarks>
/// <para><see cref="ArtifactKind.NeedsBrowser"/> when any of these holds:</para>
/// <list type="bullet">
/// <item>a milestone-6 feature whose backend is incomplete or whose host capability is not granted;</item>
/// <item>a resource the options' loader would refuse: stylesheets, images, scripts, <c>@import</c> and CSS
///   <c>url()</c>s other than <c>data:</c> URLs (the only loads the default options allow);</item>
/// <item>images in formats Folio does not decode (anything but PNG and JPEG);</item>
/// <item>elements Folio draws only as empty boxes: MathML, <c>video</c>, <c>audio</c>, <c>iframe</c>,
///   <c>object</c>, <c>embed</c>; and <c>popover</c> content, which browsers hide;</item>
/// <item>SVG (inline or a standalone SVG document) beyond shapes, paths, text, gradients, clip paths, masks, filters
///   and their primitives, <c>use</c>, <c>symbol</c> and markers: other SVG elements (patterns, images, animation, ...),
///   an <c>feImage</c> of an element, stroked text and the text attributes Folio does not lay out;</item>
/// <item>CSS that Folio's property table does not know or cannot parse, gradients, <c>background-clip: text</c>,
///   selectors it cannot match, and <c>@font-face</c>, <c>@container</c>, <c>@counter-style</c>, <c>@scope</c>.
///   Not counted: declarations that only matter to interaction (<c>cursor</c>, <c>transition</c>, ...), rules
///   that apply only on hover, focus or activation, vendor-prefixed selectors, media blocks that never match
///   a screen, and unknown properties set to <c>none</c>, <c>normal</c>, <c>auto</c>, <c>initial</c>,
///   <c>unset</c> or <c>0</c>;</item>
/// <item>a limit cut the document short.</item>
/// </list>
/// <para>Otherwise <see cref="ArtifactKind.Scripted"/> when it has a script element of a JavaScript type, an event
/// handler attribute (<c>onclick</c>, ...) or a <c>javascript:</c> URL; otherwise <see cref="ArtifactKind.Static"/>.</para>
/// </remarks>
public static class ArtifactClassifier
{
    [Flags]
    internal enum ImplementedFeatures
    {
        None = 0, WebGl = 1, Media = 2, Workers = 4, WebAssembly = 8,
        Iframes = 16, WebRtc = 32, ServiceWorkers = 64
    }

    // Flip each entry only after its original feature acceptance is established.
    // Host permission alone cannot make a prototype backend complete.
    private const ImplementedFeatures ProductionFeatures = ImplementedFeatures.WebGl;

    public static ArtifactClassification Classify(string html, FolioOptions? options = null) =>
        Classify(html, options, ProductionFeatures);

    // Per-call availability allows tests to exercise future implementations
    // without mutable process-wide switches or granting a public bypass.
    internal static ArtifactClassification Classify(string html, FolioOptions? options, ImplementedFeatures implemented)
    {
        ArgumentNullException.ThrowIfNull(html);
        options ??= new FolioOptions();
        using var document = Document.Parse(html, options);
        var scan = new Scan(document, options, implemented);
        scan.Run();

        var kind = scan.Unsupported.Count > 0 ? ArtifactKind.NeedsBrowser
            : scan.Scripts.Count > 0 ? ArtifactKind.Scripted
            : ArtifactKind.Static;
        return new ArtifactClassification(kind, [.. scan.Unsupported, .. scan.Scripts]);
    }

    private sealed class Scan(Document document, FolioOptions options, ImplementedFeatures implemented)
    {
        public readonly HashSet<string> Unsupported = [];
        public readonly HashSet<string> Scripts = [];
        public const int ScriptSizeThreshold = 50 * 1024;
        private int _totalScriptSize;
        private bool _diagramBlockSeen;

        // Media blocks count when they match some screen: a phone or a wide window.
        private readonly MediaContext[] _screens =
        [
            new(375, 812, 1, options.ColorScheme == ColorScheme.Dark) { ReducedMotion = options.ReducedMotion },
            new(1920, 1080, 1, options.ColorScheme == ColorScheme.Dark) { ReducedMotion = options.ReducedMotion },
        ];

        private static readonly HashSet<string> BoxOnlyElements =
            ["video", "audio", "iframe", "object", "embed", "frame", "frameset"];

        // Script types an in-page compiler (Babel standalone, a TypeScript transpiler) turns into JavaScript.
        private static readonly Dictionary<string, string> CompiledByHost = new(StringComparer.OrdinalIgnoreCase)
        {
            ["text/babel"] = "JSX", ["text/jsx"] = "JSX", ["application/jsx"] = "JSX", ["text/tsx"] = "JSX",
            ["text/typescript"] = "TypeScript", ["application/typescript"] = "TypeScript", ["text/ts"] = "TypeScript",
        };

        // https://mimesniff.spec.whatwg.org/#javascript-mime-type, plus module scripts.
        private static readonly HashSet<string> ScriptTypes = new(StringComparer.OrdinalIgnoreCase)
        {
            "", "module", "application/ecmascript", "application/javascript", "application/x-ecmascript",
            "application/x-javascript", "text/ecmascript", "text/javascript", "text/javascript1.0", "text/javascript1.1",
            "text/javascript1.2", "text/javascript1.3", "text/javascript1.4", "text/javascript1.5", "text/jscript",
            "text/livescript", "text/x-ecmascript", "text/x-javascript",
        };

        // Properties with no effect on a still picture of the page in its initial state.
        private static readonly HashSet<string> Inert =
        [
            "cursor", "pointer-events", "user-select", "-webkit-user-select", "-moz-user-select", "-ms-user-select",
            "transition", "transition-property", "transition-duration", "transition-timing-function", "transition-delay",
            "transition-behavior", "will-change", "scroll-behavior", "touch-action", "caret-color", "resize",
            "-webkit-font-smoothing", "-moz-osx-font-smoothing", "font-smooth", "text-rendering",
            "-webkit-tap-highlight-color", "text-size-adjust", "-webkit-text-size-adjust", "-moz-text-size-adjust",
            "-ms-text-size-adjust", "overscroll-behavior", "overscroll-behavior-x", "overscroll-behavior-y",
            "scroll-snap-type", "scroll-snap-align", "scroll-snap-stop", "scroll-margin", "scroll-margin-top",
            "scroll-padding", "scroll-padding-top", "print-color-adjust", "-webkit-print-color-adjust", "color-adjust",
            "page-break-before", "page-break-after", "page-break-inside", "break-before", "break-after", "break-inside",
            "orphans", "widows",
        ];

        // ponytail: assumes these are the property's initial value, true for nearly every property authors reset this
        // way (appearance: none is the known exception); a per-property initial value table would be exact.
        private static readonly HashSet<string> NoOpValues = ["none", "normal", "auto", "initial", "unset", "0"];

        public void Run()
        {
            foreach (var diagnostic in document.Diagnostics)
            {
                if (diagnostic.Code == DiagnosticCode.LimitExceeded)
                    Unsupported.Add($"hits a limit: {diagnostic.Message}");
            }

            var elements = document.QuerySelectorAll("*");
            _diagramBlockSeen = elements.Any(HasDiagramBlock);
            foreach (var element in elements)
                Element(element);

            foreach (var script in document.QuerySelectorAll("script"))
                ScriptSource(script.TextContent);

            if (_totalScriptSize > ScriptSizeThreshold)
                Unsupported.Add($"script text size ({_totalScriptSize / 1024} KB) exceeds the scripted start-up threshold ({ScriptSizeThreshold / 1024} KB); heavy scripts must run in the browser");
        }

        private void Element(Element element)
        {
            switch (element.NamespaceUri)
            {
                case Namespaces.SvgUri:
                    Svg(element);
                    break;
                case Namespaces.MathMLUri:
                    Unsupported.Add("uses MathML");
                    break;
            }

            foreach (var (name, value) in element.Attributes)
            {
                if (name.Length > 2 && name.StartsWith("on", StringComparison.Ordinal))
                {
                    Scripts.Add($"runs scripts: an {name} attribute");
                    ScriptSource(value);
                }
                else if (name is "href" or "src" or "action" or "formaction" or "xlink:href"
                         && value.Trim().StartsWith("javascript:", StringComparison.OrdinalIgnoreCase))
                {
                    Scripts.Add("runs scripts: a javascript: link");
                    Unsupported.Add("uses javascript: URLs; javascript: URLs are ignored in Folio");
                    ScriptSource(value.Trim()["javascript:".Length..]);
                }
            }

            if (element.GetAttribute("style") is { } style)
            {
                var (source, block) = CssParser.ParseBlockContents(style);
                Declarations(source, block.Declarations);
            }
            if (element.GetAttribute("popover") is not null)
                Unsupported.Add("uses popovers");

            if (element.NamespaceUri != Namespaces.HtmlUri)
            {
                if (element.LocalName == "script")
                    Scripts.Add("runs scripts: a script element in SVG");
                else if (element.LocalName == "style" && element.NamespaceUri == Namespaces.SvgUri)
                    StyleSheet(element.TextContent);
                return;
            }

            var tag = element.LocalName;
            if (BoxOnlyElements.Contains(tag))
            {
                if (tag is "video" or "audio")
                    Require(ImplementedFeatures.Media, options.Capabilities.Media, $"uses <{tag}>", "media");
                else if (tag is "iframe" or "frame" or "frameset")
                    Require(ImplementedFeatures.Iframes, options.Capabilities.Iframes, $"uses <{tag}>", "iframes");
                else
                    Unsupported.Add($"uses <{tag}>");
            }

            switch (tag)
            {
                // Folio never runs an in-page compiler: the host hands it plain JavaScript in place of each JSX or TypeScript script
                // (docs/interactive-artifacts.md, "JSX and TypeScript").
                case "script" when CompiledByHost.TryGetValue((element.GetAttribute("type") ?? "").Trim(), out var language):
                    Unsupported.Add($"uses in-page {language}; {language} must be compiled by the host");
                    break;
                case "script" when ScriptTypes.Contains((element.GetAttribute("type") ?? "").Trim()):
                    Scripts.Add("runs scripts: a script element");
                    var scriptType = (element.GetAttribute("type") ?? "").Trim();
                    if (string.Equals(scriptType, "module", StringComparison.OrdinalIgnoreCase))
                    {
                        var textContent = element.TextContent;
                        if (textContent.Contains("import ", StringComparison.Ordinal) || textContent.Contains("import(", StringComparison.Ordinal) || textContent.Contains("from ", StringComparison.Ordinal))
                            Unsupported.Add("uses module script imports; ES modules with imports are not supported in M4");
                    }
                    if (element.GetAttribute("src") is { } src)
                    {
                        if (IsKnownCanvasLibrary(src) && document.QuerySelectorAll("canvas").Count > 0)
                            Unsupported.Add("uses a canvas library whose painting is not yet pixel-equivalent: Chart.js");
                        if (!IsConfiguredDiagramLibrary(src) || !_diagramBlockSeen)
                            Load(src, "a script");
                        else
                            Scripts.Add("renders diagram blocks through Folio.Diagrams");
                    }
                    break;
                case "style":
                    StyleSheet(element.TextContent);
                    break;
                case "link" when HasToken(element.GetAttribute("rel"), "stylesheet") && element.GetAttribute("href") is { } href:
                    LinkedStyleSheet(href);
                    break;
                case "img" or "source" or "input" when tag != "input" || string.Equals(element.GetAttribute("type"), "image", StringComparison.OrdinalIgnoreCase):
                    if (element.GetAttribute("src") is { Length: > 0 } image)
                        Image(image);
                    foreach (var candidate in (element.GetAttribute("srcset") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        Image(candidate.Split(' ', 2)[0]);
                    break;
            }

            if (HasDiagramBlock(element))
                _diagramBlockSeen = true;
        }

        private bool IsConfiguredDiagramLibrary(string url)
        {
            url = url.Trim();
            if (options.DiagramLibraryUrls.Contains(url))
                return true;
            if (options.BaseUri is not null && Uri.TryCreate(options.BaseUri, url, out var resolved))
                return options.DiagramLibraryUrls.Contains(resolved.AbsoluteUri);
            return false;
        }

        private static bool IsKnownCanvasLibrary(string url) =>
            url.Contains("chart.js", StringComparison.OrdinalIgnoreCase) ||
            url.Contains("chart.umd", StringComparison.OrdinalIgnoreCase);

        private static bool HasDiagramBlock(Element element)
        {
            var classes = element.GetAttribute("class") ?? "";
            var hasMermaidClass = classes.Split([' ', '\t', '\n', '\f', '\r'], StringSplitOptions.RemoveEmptyEntries)
                .Contains("mermaid", StringComparer.OrdinalIgnoreCase);
            return hasMermaidClass || element.GetAttribute("data-diagram") is not null;
        }

        private void ScriptSource(string? source)
        {
            if (string.IsNullOrWhiteSpace(source))
                return;
            var text = source;
            // Whitespace and quote style do not change these API calls. Compacting first keeps this scan
            // conservative without attempting to parse arbitrary JavaScript.
            var compact = string.Concat(text.Where(character => !char.IsWhiteSpace(character)));
            compact = compact.Replace("?.[", "[", StringComparison.Ordinal)
                .Replace("?.(", "(", StringComparison.Ordinal).Replace("?.", ".", StringComparison.Ordinal);
            // Literal property access is common in optional API detection. This
            // remains a conservative source scan, not a JavaScript evaluator.
            foreach (var property in (ReadOnlySpan<string>)["serviceWorker", "WebAssembly", "getUserMedia", "getContext", "Worker", "SharedWorker"])
            {
                compact = compact.Replace($"['{property}']", $".{property}", StringComparison.OrdinalIgnoreCase)
                    .Replace($"[\"{property}\"]", $".{property}", StringComparison.OrdinalIgnoreCase);
            }
            if (Contains(compact, "getContext('webgl") || Contains(compact, "getContext(\"webgl") || Contains(compact, "getContext(`webgl")
                || Contains(compact, "getContext('experimental-webgl") || Contains(compact, "getContext(\"experimental-webgl") || Contains(compact, "getContext(`experimental-webgl"))
                Require(ImplementedFeatures.WebGl, options.Capabilities.WebGl, "uses WebGL", "WebGL");
            _totalScriptSize += text.Length;
            if (Constructs(compact, "MutationObserver"))
                Unsupported.Add("uses MutationObserver; MutationObserver is planned for milestone 5");
            if (Contains(compact, ".styleSheets") || Contains(compact, ".insertRule(") || Contains(compact, "CSS.supports("))
                Unsupported.Add("uses stylesheet mutation or CSS.supports; dynamic CSSOM is planned for milestone 5");
            if (Contains(compact, "getContext('2d") || Contains(compact, "getContext(\"2d") || Contains(compact, "getContext(`2d"))
                Unsupported.Add("uses 2D canvas (getContext('2d')); 2D canvas is planned for milestone 5");
            if (Contains(compact, "fetch(") || Constructs(compact, "XMLHttpRequest"))
                Unsupported.Add("uses network requests (fetch / XMLHttpRequest); networking is not permitted in sandbox");
            if (Constructs(compact, "WebSocket"))
                Unsupported.Add("uses WebSocket; WebSocket is planned for milestone 6");
            if (Contains(compact, "customElements.") || Contains(compact, ".attachShadow("))
                Unsupported.Add("uses custom elements or shadow DOM; web components are planned for milestone 5");
            if (Contains(text, "Babel.transform(") || Contains(text, "Babel.transformScriptTags(") || Contains(text, "ts.transpileModule(") || Contains(text, "ts.transpile("))
                Unsupported.Add("compiles code in the page; JSX and TypeScript must be compiled by the host");
            if (Constructs(compact, "SharedWorker"))
                Unsupported.Add("uses shared workers; SharedWorker is not implemented in Folio");
            if (Constructs(compact, "Worker"))
                Require(ImplementedFeatures.Workers, options.Capabilities.Workers, "uses workers", "workers");
            if (Contains(compact, "WebAssembly.") || Contains(compact, "WebAssembly["))
                Require(ImplementedFeatures.WebAssembly, options.Capabilities.WebAssembly, "uses WebAssembly", "WebAssembly");
            if (Contains(compact, "RTCPeerConnection") || Contains(compact, "getUserMedia("))
                Require(ImplementedFeatures.WebRtc, options.Capabilities.WebRtc, "uses WebRTC", "WebRTC");
            if (Contains(compact, "navigator.serviceWorker") || Contains(compact, "serviceWorker.register("))
                Require(ImplementedFeatures.ServiceWorkers, options.Capabilities.ServiceWorkers, "uses service workers", "service workers");
        }

        private static bool Contains(string source, string value) => source.Contains(value, StringComparison.OrdinalIgnoreCase);

        private static bool Constructs(string source, string constructor) =>
            Contains(source, $"new{constructor}(") || Contains(source, $"newwindow.{constructor}(")
            || Contains(source, $"newglobalThis.{constructor}(") || Contains(source, $"newself.{constructor}(");

        private void Require(ImplementedFeatures required, bool granted, string feature, string capability)
        {
            if ((implemented & required) == 0)
                Unsupported.Add($"{feature}; {capability} support is not yet complete in Folio");
            if (!granted)
                Unsupported.Add($"{feature}; the host has not granted the {capability} capability");
        }

        // What SVG stage 1 draws (docs/study/13-svg.md); defs, title, desc and metadata draw nothing themselves, and
        // style and script are checked like HTML's.
        private static readonly HashSet<string> SvgElements =
        [
            "svg", "g", "a", "defs", "rect", "circle", "ellipse", "line", "polyline", "polygon", "path", "text", "tspan", "title",
            "desc", "metadata", "style", "script", "linearGradient", "radialGradient", "stop", "clipPath", "use", "symbol", "marker", "mask",
            "filter", "feGaussianBlur", "feOffset", "feFlood", "feDropShadow", "feComposite", "feMerge", "feMergeNode", "feBlend",
            "feColorMatrix", "feComponentTransfer", "feFuncR", "feFuncG", "feFuncB", "feFuncA", "feMorphology", "feTurbulence",
            "feDisplacementMap", "feTile", "feConvolveMatrix", "feImage", "feDiffuseLighting", "feSpecularLighting", "feDistantLight",
            "fePointLight", "feSpotLight",
        ];

        private void Svg(Element element)
        {
            var name = element.LocalName;
            if (!SvgElements.Contains(name))
                Unsupported.Add($"uses SVG <{name}>");
            if (element.GetAttribute("vector-effect") is { } effect && effect.Trim() != "none")
                Unsupported.Add("uses vector-effect");
            // feImage draws images, not elements.
            if (name == "feImage" && (element.GetAttribute("href") ?? element.GetAttribute("xlink:href")) is { } image)
            {
                if (image.Trim().StartsWith('#'))
                    Unsupported.Add("uses an SVG <feImage> of an element");
                else
                    Image(image);
            }
            if (name is not ("text" or "tspan"))
                return;
            foreach (var attribute in (ReadOnlySpan<string>)["rotate", "textLength", "lengthAdjust"])
            {
                if (element.GetAttribute(attribute) is not null)
                    Unsupported.Add($"uses the SVG text attribute {attribute}");
            }
            // The stroke is inherited, so an ancestor's counts too.
            // ponytail: strokes set on text by CSS rules are not seen.
            for (var e = element; e is not null && e.NamespaceUri == Namespaces.SvgUri; e = e.Parent)
            {
                if (e.GetAttribute("stroke") is { } stroke)
                {
                    if (stroke.Trim() != "none")
                        Unsupported.Add("uses stroked SVG text");
                    break;
                }
            }
        }

        private static bool HasToken(string? list, string token) =>
            list?.Split([' ', '\t', '\n', '\f', '\r'], StringSplitOptions.RemoveEmptyEntries)
                .Contains(token, StringComparer.OrdinalIgnoreCase) == true;

        /// <summary>
        /// True when the options' loader would load the URL. The only loader is the default one, which loads
        /// <c>data:</c> URLs alone; this follows the public loader when it joins <see cref="FolioOptions"/>.
        /// </summary>
        private static bool Loadable(string url) => url.StartsWith("data:", StringComparison.OrdinalIgnoreCase);

        private void Load(string url, string what)
        {
            url = url.Trim();
            if (!Loadable(url))
                Unsupported.Add($"loads {what} these options do not allow: {Shorten(url)}");
        }

        private void Image(string url)
        {
            url = url.Trim();
            if (url.Length == 0 || url.StartsWith('#'))
                return; // an empty URL loads nothing; #id points into the document (SVG references)
            if (!Loadable(url))
                Load(url, "an image");
            else if (!DataUrl.TryParse(url, out _, out var type) || type is not ("image/png" or "image/jpeg"))
                Unsupported.Add($"uses an image format Folio does not decode: {(type.Length > 0 ? type : "unknown")}");
        }

        private void LinkedStyleSheet(string url)
        {
            url = url.Trim();
            if (!Loadable(url))
                Load(url, "a stylesheet");
            else if (DataUrl.TryParse(url, out var data, out _))
                StyleSheet(Encoding.UTF8.GetString(data));
        }

        private void StyleSheet(string css)
        {
            var sheet = CssParser.ParseStyleSheet(css, options.Limits.MaxStyleRules,
                _ => Unsupported.Add($"hits a limit: a stylesheet has more than {options.Limits.MaxStyleRules} rules"));
            Rules(sheet.Source, sheet.Rules, null);
        }

        private void Rules(string source, List<CssRule> rules, SelectorList? parent)
        {
            foreach (var rule in rules)
            {
                switch (rule)
                {
                    case StyleRule style:
                        var text = Text(source, style.Prelude);
                        if (OnlyInteractionStates(text))
                            break;
                        var selectors = SelectorParser.Parse(source, style.Prelude, document.Node.Intern, parent, nested: parent is not null);
                        if (selectors is null)
                        {
                            if (!IsVendorSpecific(text))
                                Unsupported.Add($"uses a CSS selector Folio does not support: {Shorten(text)}");
                            break;
                        }
                        Block(source, style, selectors);
                        break;
                    case AtRule at:
                        AtRule(source, at, parent);
                        break;
                    case NestedDeclarations nested:
                        Declarations(source, nested.Declarations);
                        break;
                }
            }
        }

        private void AtRule(string source, AtRule at, SelectorList? parent)
        {
            switch (at.Name)
            {
                case "media" when at.HasBlock:
                    if (_screens.Any(screen => Conditions.MediaMatches(source, at.Prelude, screen)))
                        Block(source, at, parent);
                    break;
                case "supports" or "layer" when at.HasBlock:
                    Block(source, at, parent); // what a browser supports is what matters, not what Folio does
                    break;
                case "import":
                    var reader = new ValueReader(source, at.Prelude);
                    if ((reader.Url() ?? reader.String()) is { } url)
                        LinkedStyleSheet(url);
                    break;
                case "font-face":
                    Unsupported.Add("uses web fonts (@font-face)");
                    break;
                case "container" or "counter-style" or "scope":
                    Unsupported.Add($"uses @{at.Name}");
                    break;
                // @keyframes (used only through animation properties, which are checked), @page, @property,
                // @charset, @starting-style and the rest change nothing in a still picture Folio would draw.
            }
        }

        private void Block(string source, BlockRule block, SelectorList? parent)
        {
            Declarations(source, block.Declarations);
            Rules(source, block.Rules, parent);
        }

        private void Declarations(string source, List<Declaration> declarations)
        {
            foreach (var declaration in declarations)
            {
                if (Inert.Contains(declaration.Name))
                    continue;
                Values(declaration.Value, source);
                if (declaration.IsCustomProperty)
                    continue;

                var text = Text(source, declaration.Value);
                var known = Properties.Find(declaration.Name) is not null || Properties.LonghandsOf(declaration.Name) is not null;
                if (!known)
                {
                    if (!NoOpValues.Contains(text.ToLowerInvariant()))
                        Unsupported.Add($"uses the CSS property {declaration.Name}");
                }
                else if (!Properties.ContainsVar(declaration.Value) && Properties.Parse(source, declaration) is null)
                {
                    Unsupported.Add($"uses a CSS value Folio does not support: {declaration.Name}: {Shorten(text)}");
                }
                else if (declaration.Name is "background" or "background-clip"
                         && declaration.Value.Any(v => v is PreservedToken { Token: { Kind: CssTokenKind.Ident, Value: var ident } } && ident.Equals("text", StringComparison.OrdinalIgnoreCase)))
                {
                    Unsupported.Add("uses background-clip: text");
                }
            }
        }

        // URLs in any value, and functions Folio parses but does not draw.
        private void Values(List<ComponentValue> values, string source)
        {
            foreach (var value in values)
            {
                switch (value)
                {
                    case PreservedToken { Token.Kind: CssTokenKind.Url } url:
                        Image(url.Token.Value);
                        break;
                    case CssFunction { Name: var name } function:
                        var lower = name.ToLowerInvariant();
                        if (lower == "url")
                        {
                            if (new ValueReader(source, function.Arguments).String() is { } quoted)
                                Image(quoted);
                        }
                        else if (lower.EndsWith("-gradient", StringComparison.Ordinal))
                        {
                            Unsupported.Add("uses CSS gradients");
                        }
                        else if (lower is "image-set" or "-webkit-image-set" or "cross-fade" or "element" or "paint")
                        {
                            Unsupported.Add($"uses CSS {lower}()");
                        }
                        Values(function.Arguments, source);
                        break;
                    case SimpleBlock block:
                        Values(block.Contents, source);
                        break;
                }
            }
        }

        // ponytail: splits on every comma, so a comma inside :is(...) splits too; that only makes the check stricter.
        private static bool OnlyInteractionStates(string selectors) => selectors.Split(',').All(selector =>
        {
            var lower = selector.ToLowerInvariant();
            return lower.Contains(":hover") || lower.Contains(":focus") || lower.Contains(":active") || lower.Contains("::selection")
                || lower.Contains(":focus-visible") || lower.Contains(":focus-within") || lower.Contains(":checked") || lower.Contains(":indeterminate");
        });

        private static bool IsVendorSpecific(string selectors) =>
            selectors.Contains("::-webkit-", StringComparison.OrdinalIgnoreCase) || selectors.Contains(":-moz-", StringComparison.OrdinalIgnoreCase)
            || selectors.Contains("::-moz-", StringComparison.OrdinalIgnoreCase) || selectors.Contains(":-ms-", StringComparison.OrdinalIgnoreCase);

        private static string Text(string source, List<ComponentValue> values) =>
            values.Count == 0 ? "" : source[values[0].Start..values[^1].End].Trim();

        private static string Shorten(string text) => text.Length <= 80 ? text : text[..77] + "...";
    }
}
