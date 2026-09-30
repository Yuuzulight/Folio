using System.Text;
using Folio.Css;
using Folio.Dom;
using Folio.Resources;

namespace Folio.Style;

/// <summary>https://www.w3.org/TR/css-cascade-5/#cascade-origin</summary>
internal enum Origin
{
    UserAgent,
    User,
    Author,
}

/// <summary>One declaration as the cascade sees it: a longhand value, or a custom property's text or keyword.</summary>
internal sealed record CascadeDeclaration(PropertyId Id, CssValue? Value, string? CustomName, CustomProperties.Declared Custom, bool Important);

/// <summary>A style rule's declarations with where they come from.</summary>
/// <param name="Layer">Cascade layer path; each level ends with int.MaxValue for "not in a sub-layer".</param>
internal sealed record CascadeRule(IReadOnlyList<CascadeDeclaration> Declarations, Origin Origin, int[] Layer, int Order);

/// <summary>
/// The compiled style rules of one origin (docs/study/04-cascade-and-computed-values.md): nesting flattened,
/// <c>@media</c> and <c>@supports</c> evaluated, layers ordered, declarations parsed into longhands.
/// </summary>
/// <summary>Where external stylesheets come from, for @import and link elements.</summary>
/// <param name="RequireCssType">Standards-mode documents only use responses typed text/css (when a type is known).</param>
/// <param name="Report">Told about every stylesheet that was not loaded, and why.</param>
internal sealed record StyleSources(ResourceLoader Loader, string? BaseUrl, bool RequireCssType = true, Action<string>? Report = null)
{
    /// <summary>@import depth limit (docs/study/16-resources-and-security.md).</summary>
    public const int MaxImportDepth = 4;

    /// <summary>Loads and decodes a stylesheet; null (reported) when it cannot be used.</summary>
    public string? LoadStyleSheet(string url)
    {
        var response = Loader.Load(new ResourceRequest(url, ResourceKind.Stylesheet));
        if (!response.Succeeded)
        {
            Report?.Invoke($"Stylesheet {Shorten(url)} was not loaded: {response.Error}");
            return null;
        }
        if (RequireCssType && response.ContentType is { } type && !type.Split(';')[0].Trim().Equals("text/css", StringComparison.OrdinalIgnoreCase))
        {
            Report?.Invoke($"Stylesheet {Shorten(url)} was ignored: its type is {type}, not text/css.");
            return null;
        }
        return Decode(response.Data!);
    }

    // https://www.w3.org/TR/css-syntax-3/#input-byte-stream, simplified: a byte order mark, else UTF-8.
    private static string Decode(byte[] bytes) => bytes switch
    {
        [0xEF, 0xBB, 0xBF, ..] => Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3),
        [0xFE, 0xFF, ..] => Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2),
        [0xFF, 0xFE, ..] => Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2),
        _ => Encoding.UTF8.GetString(bytes),
    };

    private static string Shorten(string url) => url.Length > 80 ? url[..77] + "..." : url;
}

internal sealed class CascadeData(Origin origin, Func<string, Atom> intern, MediaContext media, StyleSources? sources = null)
{
    private readonly LayerNode _layers = new();
    private int _order;

    public Origin Origin { get; } = origin;
    public RuleIndex<CascadeRule> Rules { get; } = new();
    public Dictionary<string, RegisteredProperty> Registered { get; } = new(StringComparer.Ordinal);

    /// <summary>Order-of-appearance counter shared by all origins and the style attribute.</summary>
    public int NextOrder() => _order++;

    /// <param name="url">The sheet's own URL (for @import resolution); null for inline sheets, which use the base URL.</param>
    public void Add(CssStyleSheet sheet, string? url = null) =>
        AddSheet(sheet.Source, sheet.Rules, url ?? sources?.BaseUrl, [], _layers, url is null ? [] : [url], 0);

    // A sheet's top level: @import rules count only before any rule other than @charset and @layer statements.
    // chain: the URLs of the sheets being imported into (for cycles); depth: how many @imports deep this sheet is.
    private void AddSheet(string source, List<CssRule> rules, string? baseUrl, int[] layer, LayerNode layerNode, List<string> chain, int depth)
    {
        var importsAllowed = true;
        foreach (var rule in rules)
        {
            if (rule is AtRule { Name: "import", HasBlock: false } import)
            {
                if (importsAllowed)
                    Import(source, import, baseUrl, layer, layerNode, chain, depth);
                continue;
            }
            if (rule is not (AtRule { Name: "charset" } or AtRule { Name: "layer", HasBlock: false }))
                importsAllowed = false;
            AddRules(source, [rule], null, layer, layerNode);
        }
    }

    // https://www.w3.org/TR/css-cascade-5/#at-import: url [ layer | layer(name) ]? [ supports(...) ]? [ media-query-list ]?
    private void Import(string source, AtRule import, string? baseUrl, int[] layer, LayerNode layerNode, List<string> chain, int depth)
    {
        var r = new ValueReader(source, import.Prelude);
        if ((r.Url() ?? r.String()) is not { } href)
            return;

        var layered = false;
        string? layerName = null;
        if (r.Keyword("layer") is not null)
        {
            layered = true;
        }
        else if (r.Function("layer") is { } layerArgs)
        {
            var names = LayerNames(source, layerArgs.Rest());
            if (names is not [var name])
                return;
            (layered, layerName) = (true, name);
        }

        if (r.Function("supports") is { } supports)
        {
            var condition = supports.Rest();
            var text = condition.Count == 0 ? "" : source[condition[0].Start..condition[^1].End];
            var (conditionSource, conditionValues) = CssParser.ParseComponentValues("(" + text + ")");
            if (!Conditions.Supports(conditionSource, conditionValues))
                return;
        }
        if (!Conditions.MediaMatches(source, r.Rest(), media))
            return;

        if (sources is null)
            return;
        if (ResourceLoader.Resolve(baseUrl, href) is not { } url)
        {
            sources.Report?.Invoke($"@import \"{href}\" has no base URL to resolve against.");
            return;
        }
        if (chain.Contains(url, StringComparer.Ordinal))
            return; // an import cycle
        if (depth >= StyleSources.MaxImportDepth)
        {
            sources.Report?.Invoke($"@import \"{href}\" is nested deeper than {StyleSources.MaxImportDepth} levels.");
            return;
        }
        if (sources.LoadStyleSheet(url) is not { } css)
            return;

        var sheet = CssParser.ParseStyleSheet(css);
        var (importLayer, importNode) = layered ? layerNode.Enter(layerName, layer) : (layer, layerNode);
        AddSheet(sheet.Source, sheet.Rules, url, importLayer, importNode, [.. chain, url], depth + 1);
    }

    private void AddRules(string source, List<CssRule> rules, SelectorList? parent, int[] layer, LayerNode layerNode)
    {
        foreach (var rule in rules)
        {
            switch (rule)
            {
                case StyleRule style:
                    var selectors = SelectorParser.Parse(source, style.Prelude, intern, parent, nested: parent is not null);
                    if (selectors is null)
                        break; // an invalid selector drops the rule and everything nested in it
                    AddDeclarations(source, style.Declarations, selectors, layer);
                    AddRules(source, style.Rules, selectors, layer, layerNode);
                    break;
                case NestedDeclarations nested when parent is not null:
                    AddDeclarations(source, nested.Declarations, parent, layer);
                    break;
                case AtRule at:
                    AddAtRule(source, at, parent, layer, layerNode);
                    break;
            }
        }
    }

    private void AddAtRule(string source, AtRule at, SelectorList? parent, int[] layer, LayerNode layerNode)
    {
        switch (at.Name)
        {
            case "media" when at.HasBlock:
                if (Conditions.MediaMatches(source, at.Prelude, media))
                    AddBlock(source, at, parent, layer, layerNode);
                break;
            case "supports" when at.HasBlock:
                if (Conditions.Supports(source, at.Prelude))
                    AddBlock(source, at, parent, layer, layerNode);
                break;
            case "layer":
                var names = LayerNames(source, at.Prelude);
                if (names is null)
                    break;
                if (!at.HasBlock)
                {
                    foreach (var name in names)
                        layerNode.Enter(name, layer); // statement: fixes the order only
                }
                else if (names.Count <= 1)
                {
                    var (path, node) = layerNode.Enter(names.FirstOrDefault(), layer);
                    AddBlock(source, at, parent, path, node);
                }
                break;
            case "property" when parent is null && at.HasBlock:
                Register(source, at);
                break;
            // @import is handled at a sheet's top level (AddSheet); @font-face, @keyframes and @page arrive with web
            // fonts, animations and printing.
        }
    }

    // Declarations directly in a conditional or layer block apply to the enclosing style rule (nesting).
    private void AddBlock(string source, BlockRule block, SelectorList? parent, int[] layer, LayerNode layerNode)
    {
        if (parent is not null)
            AddDeclarations(source, block.Declarations, parent, layer);
        AddRules(source, block.Rules, parent, layer, layerNode);
    }

    private void AddDeclarations(string source, List<Declaration> declarations, SelectorList selectors, int[] layer)
    {
        var parsed = Parse(source, declarations);
        if (parsed.Count == 0)
            return;
        var rule = new CascadeRule(parsed, Origin, Complete(layer), NextOrder());
        foreach (var selector in selectors.Selectors)
            Rules.Add(selector, rule);
    }

    /// <summary>Parses declarations into longhands and custom properties, dropping invalid ones.</summary>
    public static List<CascadeDeclaration> Parse(string source, List<Declaration> declarations)
    {
        var result = new List<CascadeDeclaration>();
        foreach (var declaration in declarations)
        {
            if (declaration.IsCustomProperty)
            {
                var text = declaration.Value.Count == 0 ? "" : source[declaration.Value[0].Start..declaration.Value[^1].End];
                var keyword = text.Trim().ToLowerInvariant() switch
                {
                    "initial" => CssWideKeyword.Initial,
                    "inherit" => CssWideKeyword.Inherit,
                    "unset" => CssWideKeyword.Unset,
                    "revert" => CssWideKeyword.Revert,
                    "revert-layer" => CssWideKeyword.RevertLayer,
                    _ => (CssWideKeyword?)null,
                };
                result.Add(new CascadeDeclaration(default, null, declaration.Name, new(keyword is null ? text : null, keyword), declaration.Important));
                continue;
            }
            if (Properties.Parse(source, declaration) is { } values)
            {
                foreach (var (id, value) in values)
                    result.Add(new CascadeDeclaration(id, value, null, default, declaration.Important));
            }
        }
        return result;
    }

    private void Register(string source, AtRule at)
    {
        if (at.Prelude.FirstOrDefault(v => v is not PreservedToken { Token.Kind: CssTokenKind.Whitespace }) is not PreservedToken { Token.Kind: CssTokenKind.Ident } name
            || !name.Token.Value.StartsWith("--", StringComparison.Ordinal))
            return;
        bool? inherits = null;
        string? initial = null;
        foreach (var declaration in at.Declarations)
        {
            var text = declaration.Value.Count == 0 ? "" : source[declaration.Value[0].Start..declaration.Value[^1].End].Trim();
            if (declaration.Name == "inherits")
                inherits = text.ToLowerInvariant() switch { "true" => true, "false" => false, _ => null };
            else if (declaration.Name == "initial-value")
                initial = text;
        }
        if (inherits is { } i)
            Registered[name.Token.Value] = new RegisteredProperty(i, initial); // syntax checking arrives in M2
    }

    private static List<string>? LayerNames(string source, List<ComponentValue> prelude)
    {
        var names = new List<string>();
        var current = "";
        foreach (var value in prelude)
        {
            switch (value)
            {
                case PreservedToken { Token.Kind: CssTokenKind.Whitespace }:
                    break;
                case PreservedToken { Token.Kind: CssTokenKind.Ident } ident:
                    current += ident.Token.Value;
                    break;
                case PreservedToken dot when dot.Token.IsDelim('.') && current.Length > 0:
                    current += ".";
                    break;
                case PreservedToken { Token.Kind: CssTokenKind.Comma } when current.Length > 0:
                    names.Add(current);
                    current = "";
                    break;
                default:
                    return null;
            }
        }
        if (current.Length > 0)
            names.Add(current);
        return names;
    }

    private static int[] Complete(int[] layer) => [.. layer, int.MaxValue];

    /// <summary>Layers in declaration order (https://www.w3.org/TR/css-cascade-5/#layer-ordering).</summary>
    private sealed class LayerNode
    {
        private readonly Dictionary<string, (int Index, LayerNode Node)> _children = new(StringComparer.Ordinal);
        private int _next;

        /// <summary>Enters a (dotted) layer name, or an anonymous layer for null.</summary>
        public (int[] Path, LayerNode Node) Enter(string? dottedName, int[] path)
        {
            if (dottedName is null)
                return ([.. path, _next++], new LayerNode());
            var node = this;
            foreach (var part in dottedName.Split('.'))
            {
                if (!node._children.TryGetValue(part, out var child))
                    node._children[part] = child = (node._next++, new LayerNode());
                path = [.. path, child.Index];
                node = child.Node;
            }
            return (path, node);
        }
    }
}

/// <summary>Finds each property's cascaded value for an element (https://www.w3.org/TR/css-cascade-5/#cascade-sort).</summary>
internal static class Cascade
{
    private readonly record struct Candidate(CascadeDeclaration Declaration, Origin Origin, bool ElementAttached, int[] Layer, Specificity Specificity, int Order, int Index);

    /// <summary>Appends the rules matching an element (or one of its pseudo-elements), origin by origin, each in order of appearance.</summary>
    public static void Match(ElementNode element, List<CascadeData> origins, MatchContext context, PseudoElement pseudoElement,
                             List<RuleIndex<CascadeRule>.Entry> matched)
    {
        foreach (var data in origins)
            data.Rules.Collect(element, pseudoElement, context, matched);
    }

    private static readonly int[] HintLayer = [-1];
    private static readonly int[] StyleAttributeLayer = [int.MaxValue];

    /// <summary>The cascaded values from matched rules, the style attribute and presentational hints.</summary>
    public static (Dictionary<PropertyId, CssValue> Values, Dictionary<string, CustomProperties.Declared> Custom) Compute(
        List<RuleIndex<CascadeRule>.Entry> matched, List<CascadeDeclaration>? styleAttribute, int styleAttributeOrder, List<CascadeDeclaration>? hints)
    {
        var candidates = new List<Candidate>();
        // Presentational hints: author origin, zero specificity, before every author rule, below every author layer.
        if (hints is not null)
        {
            for (var d = 0; d < hints.Count; d++)
                candidates.Add(new Candidate(hints[d], Origin.Author, false, HintLayer, default, -1, d));
        }
        foreach (var match in matched)
        {
            var declarations = match.Data.Declarations;
            for (var d = 0; d < declarations.Count; d++)
                candidates.Add(new Candidate(declarations[d], match.Data.Origin, false, match.Data.Layer, match.Selector.Specificity, match.Data.Order, d));
        }
        if (styleAttribute is not null)
        {
            for (var d = 0; d < styleAttribute.Count; d++)
                candidates.Add(new Candidate(styleAttribute[d], Origin.Author, true, StyleAttributeLayer, default, styleAttributeOrder, d));
        }

        // Highest priority first; within one rule, later declarations come first too.
        candidates.Sort((a, b) => Compare(b, a));

        var values = new Dictionary<PropertyId, CssValue>();
        var custom = new Dictionary<string, CustomProperties.Declared>(StringComparer.Ordinal);
        Dictionary<object, Func<Candidate, bool>>? rollbacks = null; // only revert and revert-layer need them
        for (var i = 0; i < candidates.Count; i++)
        {
            var c = candidates[i];
            if ((c.Declaration.CustomName is { } n ? custom.ContainsKey(n) : values.ContainsKey(c.Declaration.Id))
                || (rollbacks is not null && rollbacks.TryGetValue(KeyOf(c), out var skip) && skip(c)))
                continue;

            var keyword = c.Declaration.CustomName is not null ? c.Declaration.Custom.Keyword : (c.Declaration.Value as CssWideValue)?.Keyword;
            if (keyword == CssWideKeyword.RevertLayer && c.Layer.Length == 1)
                keyword = CssWideKeyword.Revert; // outside any layer there is no layer to go back to
            if (keyword == CssWideKeyword.Revert && c.Origin != Origin.UserAgent)
            {
                // Roll back to the previous origin: ignore the rest of this origin, both importances.
                var origin = c.Origin;
                (rollbacks ??= [])[KeyOf(c)] = other => other.Origin == origin;
                continue;
            }
            if (keyword == CssWideKeyword.RevertLayer)
            {
                // Roll back to the previous layer of the same origin and importance.
                var (origin, important, layer) = (c.Origin, c.Declaration.Important, c.Layer);
                (rollbacks ??= [])[KeyOf(c)] = other => other.Origin == origin && other.Declaration.Important == important && other.Layer.SequenceEqual(layer);
                continue;
            }

            var resolved = keyword is CssWideKeyword.Revert or CssWideKeyword.RevertLayer ? CssWideKeyword.Unset : keyword;
            if (c.Declaration.CustomName is { } customName)
                custom[customName] = resolved is null ? c.Declaration.Custom : new CustomProperties.Declared(null, resolved);
            else
                values[c.Declaration.Id] = resolved is { } k && k != keyword ? new CssWideValue(k) : c.Declaration.Value!;
        }
        return (values, custom);
    }

    // A property's key for rollbacks: its id, or a custom property's name.
    private static object KeyOf(Candidate c) => c.Declaration.CustomName is { } name ? name : c.Declaration.Id;

    // Origin and importance, then element-attached (style attribute), then layer, specificity, order.
    private static int Compare(Candidate a, Candidate b)
    {
        var rank = Rank(a).CompareTo(Rank(b));
        if (rank != 0)
            return rank;
        if (a.ElementAttached != b.ElementAttached)
            return a.ElementAttached ? 1 : -1;
        var layer = CompareLayers(a.Layer, b.Layer);
        if (layer != 0)
            return a.Declaration.Important ? -layer : layer; // important declarations reverse layer order
        var specificity = a.Specificity.CompareTo(b.Specificity);
        if (specificity != 0)
            return specificity;
        var order = a.Order.CompareTo(b.Order);
        return order != 0 ? order : a.Index.CompareTo(b.Index);
    }

    // UA < user < author for normal declarations, reversed for important ones.
    private static int Rank(Candidate c) => c.Declaration.Important ? 5 - (int)c.Origin : (int)c.Origin;

    private static int CompareLayers(int[] a, int[] b)
    {
        for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            if (a[i] != b[i])
                return a[i].CompareTo(b[i]);
        }
        return a.Length.CompareTo(b.Length);
    }
}
