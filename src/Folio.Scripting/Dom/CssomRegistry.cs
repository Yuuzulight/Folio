using System.Text.Json;
using Folio.Dom;
using Folio.Resources;

namespace Folio.Scripting.Dom;

/// <summary>
/// The document's CSSOM sheets (issues #498, #499): one <see cref="CssomSheet"/> per style or link element in tree order,
/// created on first access, parsed with Folio's parser and kept in step with the element. Edits (insertRule, deleteRule,
/// selector and style changes) are written back as the element's sheet override and restyle through the normal update path.
/// </summary>
internal sealed class CssomRegistry(DomContext context)
{
    /// <summary>The most rules, at any depth, one sheet may hold (study 16).</summary>
    public const int MaxRules = 100_000;

    private readonly Dictionary<ElementNode, CssomSheet> _byOwner = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<int, CssomSheet> _sheets = [];
    private readonly Dictionary<int, CssomRule> _rules = [];
    private readonly Dictionary<CssomSheet, string> _loadedFrom = [];
    private int _nextId;

    // ---------------------------------------------------------------------------------------------- sheets

    /// <summary>The ids of the document's sheets, in tree order.</summary>
    public List<int> SheetIds()
    {
        var ids = new List<int>();
        foreach (var element in context.Document.Descendants.OfType<ElementNode>())
            if (IsSheetOwner(element)) ids.Add(SheetFor(element).Id);
        return ids;
    }

    public CssomSheet? Sheet(int id) => _sheets.GetValueOrDefault(id);

    public CssomRule? Rule(int id) => _rules.GetValueOrDefault(id);

    /// <summary>The sheet of a style element, or of a link rel=stylesheet; null for any other element.</summary>
    public CssomSheet? SheetOf(ElementNode element) => IsSheetOwner(element) ? SheetFor(element) : null;

    private static bool IsSheetOwner(ElementNode element)
    {
        if (element.Name.Namespace != Namespaces.Html && !(element.Name.Namespace == Namespaces.Svg && element.LocalName == "style"))
            return false;
        if (element.GetAttribute("type") is { Length: > 0 } type && !type.Equals("text/css", StringComparison.OrdinalIgnoreCase))
            return false;
        if (element.LocalName == "style")
            return true;
        if (element.LocalName != "link" || element.GetAttribute("href") is not { Length: > 0 })
            return false;
        return (element.GetAttribute("rel") ?? "").Split(ElementNode.AsciiWhitespace, StringSplitOptions.RemoveEmptyEntries)
            .Contains("stylesheet", StringComparer.OrdinalIgnoreCase);
    }

    private CssomSheet SheetFor(ElementNode owner)
    {
        if (!_byOwner.TryGetValue(owner, out var sheet))
        {
            sheet = new CssomSheet(++_nextId, owner);
            _byOwner[owner] = sheet;
            _sheets[sheet.Id] = sheet;
        }
        Sync(sheet);
        return sheet;
    }

    // The text a sheet is made of now: the CSSOM's own override, a style element's text, or the file a link loads.
    private string TextOf(CssomSheet sheet)
    {
        var owner = sheet.Owner;
        if (owner.SheetText is { } edited)
            return edited;
        if (owner.LocalName == "style")
            return owner.TextContent ?? "";
        var href = owner.GetAttribute("href") ?? "";
        if (_loadedFrom.TryGetValue(sheet, out var key) && key.StartsWith(href + "\n", StringComparison.Ordinal))
            return key[(href.Length + 1)..];
        var baseUrl = Folio.Style.StyleResolver.BaseUrl(context.Document, context.BaseUri?.AbsoluteUri);
        var text = "";
        if (Folio.Resources.ResourceLoader.Resolve(baseUrl, href) is { } url)
        {
            var loaded = new Folio.Resources.ResourceLoader(allowedFolders: null, maxBytes: Folio.Resources.ResourceLoader.MaxResourceBytes, host: context.ResourceLoader)
                .Load(new ResourceRequest(url, ResourceKind.Stylesheet));
            if (loaded.Data is { } data)
                text = System.Text.Encoding.UTF8.GetString(data);
        }
        _loadedFrom[sheet] = href + "\n" + text;
        return text;
    }

    // Parses again when the element's text (or the loaded file) is no longer the text the rules came from.
    private void Sync(CssomSheet sheet)
    {
        var text = TextOf(sheet);
        if (sheet.Text == text)
            return;
        foreach (var old in sheet.Rules.SelectMany(r => r.Descendants()))
        {
            old.Sheet = null;
            old.ParentRule = null;
            _rules.Remove(old.Id);
        }
        sheet.Rules.Clear();
        foreach (var rule in CssomParser.Parse(text, Create, MaxRules))
            Adopt(sheet, null, rule);
        sheet.Text = text;
    }

    private CssomRule Create(CssomKind kind)
    {
        var rule = new CssomRule(++_nextId, kind);
        _rules[rule.Id] = rule;
        return rule;
    }

    // Every rule under a new rule knows its sheet and parent.
    private static void Adopt(CssomSheet sheet, CssomRule? parent, CssomRule rule)
    {
        rule.Sheet = sheet;
        rule.ParentRule = parent;
        if (parent is null)
            sheet.Rules.Add(rule);
        foreach (var child in rule.Children)
            Adopt(sheet, rule, child);
    }

    private void Commit(CssomSheet sheet)
    {
        var text = string.Join("\n", sheet.Rules.Select(r => r.CssText));
        sheet.Text = text;
        sheet.Owner.SheetText = text;
    }

    // ---------------------------------------------------------------------------------------------- reading

    /// <summary>The child rules of a sheet or rule as (id, kind), after bringing the sheet up to date with its element.</summary>
    public List<object[]> Children(CssomSheet? sheet, CssomRule? rule)
    {
        if ((sheet ?? rule?.Sheet) is { } owner)
            Sync(owner);
        return (rule is null ? sheet!.Rules : rule.Children).Select(r => new object[] { r.Id, r.Kind.ToString() }).ToList();
    }

    public string SheetJson(CssomSheet sheet)
    {
        var owner = sheet.Owner;
        string? href = null;
        if (owner.LocalName == "link" && Folio.Resources.ResourceLoader.Resolve(Folio.Style.StyleResolver.BaseUrl(context.Document, context.BaseUri?.AbsoluteUri), owner.GetAttribute("href") ?? "") is { } resolved)
            href = resolved;
        return JsonSerializer.Serialize(new
        {
            id = sheet.Id,
            href,
            title = owner.GetAttribute("title"),
            media = MediaText(sheet),
            disabled = owner.GetAttribute("disabled") is not null,
        });
    }

    public string MediaText(CssomSheet sheet)
    {
        var media = sheet.Owner.GetAttribute("media") ?? "";
        var (source, values) = Folio.Css.CssParser.ParseComponentValues(media);
        return CssomParser.Print(values, source, CssomParser.Mode.Condition);
    }

    public string RuleJson(CssomRule rule) => JsonSerializer.Serialize(new
    {
        id = rule.Id,
        kind = rule.Kind.ToString(),
        type = rule.Type,
        cssText = rule.CssText,
        prelude = rule.Prelude,
        href = rule.Href,
        media = rule.MediaText,
        layerName = rule.LayerName,
        supports = rule.SupportsText,
        prefix = rule.Prefix,
        names = rule.LayerNames,
        attached = rule.Sheet is not null,
        owned = rule.ParentRule?.Id ?? 0,
        sheet = rule.Sheet?.Id ?? 0,
    });

    // ---------------------------------------------------------------------------------------------- editing

    /// <summary>insertRule on a sheet (rule null) or a grouping rule: https://drafts.csswg.org/cssom/#insert-a-css-rule.</summary>
    public int Insert(CssomSheet? sheet, CssomRule? parent, string text, long index)
    {
        sheet ??= parent!.Sheet ?? throw DomError(DomExceptionNames.InvalidStateError, "The rule is not in a style sheet.");
        Sync(sheet);
        var list = parent is null ? sheet.Rules : parent.Children;
        if (index < 0 || index > list.Count)
            throw DomError(DomExceptionNames.IndexSizeError, $"The index {index} is larger than the number of rules ({list.Count}).");

        var inKeyframes = parent?.Kind == CssomKind.Keyframes;
        var rule = CssomParser.ParseOne(text, Create, nested: parent is { Kind: CssomKind.Style } || parent is { Kind: not CssomKind.Keyframes } && parent is not null && IsInsideStyle(parent), inKeyframes);
        if (rule is null || rule.Kind == CssomKind.Keyframe != inKeyframes)
            throw DomError(DomExceptionNames.SyntaxError, "Failed to parse the rule '" + Trim(text) + "'.");

        if (rule.Kind is CssomKind.Import or CssomKind.Namespace && parent is not null)
            throw DomError(DomExceptionNames.HierarchyRequestError, "@import and @namespace rules can only be in the top level of a style sheet.");
        if (parent is null)
        {
            if (rule.Kind == CssomKind.Import && list.Take((int)index).Any(r => r.Kind is not (CssomKind.Import or CssomKind.LayerStatement)))
                throw DomError(DomExceptionNames.HierarchyRequestError, "@import rules must come before every other rule.");
            if (rule.Kind == CssomKind.Namespace)
            {
                if (list.Any(r => r.Kind is not (CssomKind.Import or CssomKind.Namespace)))
                    throw DomError(DomExceptionNames.InvalidStateError, "@namespace rules can only be inserted when the sheet has only @import and @namespace rules.");
            }
            else if (rule.Kind != CssomKind.Import && list.Skip((int)index).Any(r => r.Kind is CssomKind.Import or CssomKind.Namespace))
                throw DomError(DomExceptionNames.HierarchyRequestError, "This rule can't be inserted before an @import or @namespace rule.");
        }
        if (rule.Kind == CssomKind.NestedDeclarations)
            throw DomError(DomExceptionNames.SyntaxError, "Failed to parse the rule '" + Trim(text) + "'.");

        var total = sheet.Rules.Sum(r => r.Descendants().Count()) + rule.Descendants().Count();
        if (total > MaxRules)
            throw DomError(DomExceptionNames.QuotaExceededError, $"A style sheet may hold at most {MaxRules} rules.");

        list.Insert((int)index, rule);
        rule.ParentRule = parent;
        rule.Sheet = sheet;
        foreach (var child in rule.Children)
            Adopt(sheet, rule, child);
        Commit(sheet);
        return (int)index;
    }

    private static bool IsInsideStyle(CssomRule rule)
    {
        for (var current = rule; current is not null; current = current.ParentRule)
        {
            if (current.Kind == CssomKind.Style)
                return true;
        }
        return false;
    }

    public void Delete(CssomSheet? sheet, CssomRule? parent, long index)
    {
        sheet ??= parent!.Sheet ?? throw DomError(DomExceptionNames.InvalidStateError, "The rule is not in a style sheet.");
        Sync(sheet);
        var list = parent is null ? sheet.Rules : parent.Children;
        if (index < 0 || index >= list.Count)
            throw DomError(DomExceptionNames.IndexSizeError, $"The index {index} is not less than the number of rules ({list.Count}).");
        var removed = list[(int)index];
        if (removed.Kind == CssomKind.Namespace && parent is null && list.Any(r => r.Kind is not (CssomKind.Import or CssomKind.Namespace)))
            throw DomError(DomExceptionNames.InvalidStateError, "An @namespace rule can't be deleted while the sheet has other kinds of rules.");
        list.RemoveAt((int)index);
        foreach (var gone in removed.Descendants())
        {
            gone.Sheet = null;
            gone.ParentRule = null;
        }
        Commit(sheet);
    }

    public void SetSelector(CssomRule rule, string text)
    {
        if (rule.Sheet is not { } sheet)
            return;
        Sync(sheet);
        var parsed = CssomParser.ParseOne(text + " { }", Create, nested: IsInsideStyle(rule.ParentRule ?? rule) && rule.ParentRule is not null, inKeyframes: false);
        if (parsed is null || parsed.Kind != CssomKind.Style)
            return; // an invalid selector is ignored, as the setter's steps say
        rule.Prelude = parsed.Prelude;
        Commit(sheet);
    }

    public void SetKeyText(CssomRule rule, string text)
    {
        if (rule.Sheet is not { } sheet)
            return;
        var probe = CssomParser.ParseOne("@keyframes x { " + text + " { } }", Create, nested: false, inKeyframes: false);
        if (probe is not { Kind: CssomKind.Keyframes } || probe.Children.Count != 1)
            throw DomError(DomExceptionNames.SyntaxError, "The key text '" + Trim(text) + "' is not a valid keyframe selector.");
        rule.Prelude = probe.Children[0].Prelude;
        Commit(sheet);
    }

    /// <summary>The CSSStyleDeclaration of a rule: reads and writes its declarations and commits the sheet.</summary>
    public LiveStyleDeclaration StyleOf(CssomRule rule) => new(
        () => rule.DeclarationText(),
        text =>
        {
            var (source, block) = Folio.Css.CssParser.ParseBlockContents(text);
            var parsed = CssomParser.ParseOne("a { " + text + " }", Create, nested: false, inKeyframes: false);
            if (parsed is not null)
                rule.Declarations = parsed.Declarations;
            else
                rule.Declarations = [];
            if (rule.Sheet is { } sheet)
                Commit(sheet);
        });

    public void SetDisabled(CssomSheet sheet, bool disabled)
    {
        if (disabled)
            sheet.Owner.SetAttribute("disabled", "");
        else
            sheet.Owner.RemoveAttribute("disabled");
    }

    public void SetMedia(CssomSheet sheet, string text)
    {
        if (text.Length == 0)
            sheet.Owner.RemoveAttribute("media");
        else
            sheet.Owner.SetAttribute("media", text);
    }

    // ---------------------------------------------------------------------------------------------- errors

    private static WebIdlException DomError(string name, string message) => WebIdlException.DomException(name, message);

    private static string Trim(string text) => text.Length > 60 ? text[..60] + "..." : text;
}
