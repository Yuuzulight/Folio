using System.Text.Json;
using Folio.Dom;

namespace Folio.Scripting.Dom;

/// <summary>
/// The CSS object model's script surface (issues #498, #499): <c>StyleSheetList</c>, <c>CSSStyleSheet</c>, <c>CSSRuleList</c>,
/// <c>MediaList</c> and the rule interfaces. The classes are script-level handles (identity, <c>instanceof</c>,
/// iteration); the rules themselves, their parsing and printing, and every edit are the host's
/// (<see cref="CssomRegistry"/>), so a sheet is never described twice.
/// </summary>
internal static class CssomBindings
{
    internal static void Install(IScriptEngine engine, DomContext context)
    {
        var registry = context.Cssom;
        var host = new HostObjectTemplate("FolioCssomHost")
            .DefineMethod("sheets", (_, _, _) => Json(registry.SheetIds()))
            .DefineMethod("sheetOf", (_, _, args) =>
                args.Length > 0 && context.Engine.TryGetHostTarget(args[0], out var target) && target is ElementNode element && registry.SheetOf(element) is { } sheet
                    ? ScriptValue.FromNumber(sheet.Id)
                    : ScriptValue.FromNumber(0))
            .DefineMethod("sheet", (_, _, args) => Sheet(registry, args) is { } sheet ? ScriptValue.FromString(registry.SheetJson(sheet)) : ScriptValue.Null)
            .DefineMethod("owner", (_, _, args) => Sheet(registry, args) is { } sheet ? context.Wrap(sheet.Owner) : ScriptValue.Null)
            .DefineMethod("children", (_, _, args) => Json(Children(registry, args)))
            .DefineMethod("rule", (_, _, args) => Rule(registry, args, 0) is { } rule ? ScriptValue.FromString(registry.RuleJson(rule)) : ScriptValue.Null)
            .DefineMethod("insert", (_, _, args) =>
            {
                var (sheet, rule) = Parent(registry, args);
                return ScriptValue.FromNumber(registry.Insert(sheet, rule, Text(args, 2), (long)Number(args, 3)));
            })
            .DefineMethod("delete", (_, _, args) =>
            {
                var (sheet, rule) = Parent(registry, args);
                registry.Delete(sheet, rule, (long)Number(args, 2));
                return ScriptValue.Undefined;
            })
            .DefineMethod("setSelector", (_, _, args) => { if (Rule(registry, args, 0) is { } rule) registry.SetSelector(rule, Text(args, 1)); return ScriptValue.Undefined; })
            .DefineMethod("setKeyText", (_, _, args) => { if (Rule(registry, args, 0) is { } rule) registry.SetKeyText(rule, Text(args, 1)); return ScriptValue.Undefined; })
            .DefineMethod("style", (_, _, args) => Rule(registry, args, 0) is { } rule
                ? context.Engine.CreateHostObject(context.Templates.StyleDeclarationTemplate, registry.StyleOf(rule))
                : ScriptValue.Null)
            .DefineMethod("setDisabled", (_, _, args) => { if (Sheet(registry, args) is { } sheet) registry.SetDisabled(sheet, args.Length > 1 && args[1].AsBoolean()); return ScriptValue.Undefined; })
            .DefineMethod("setMedia", (_, _, args) => { if (Sheet(registry, args) is { } sheet) registry.SetMedia(sheet, Text(args, 1)); return ScriptValue.Undefined; });
        engine.SetGlobalProperty("__folio_cssom_host", engine.CreateHostObject(host, new object()));
        engine.EvaluateHostScript(Source, "folio:cssom");
        context.CssomStyleSheets = engine.GetGlobalProperty("__folio_cssom_styleSheets");
        context.CssomSheetOf = engine.GetGlobalProperty("__folio_cssom_sheetOf");
        engine.Evaluate("delete globalThis.__folio_cssom_host; delete globalThis.__folio_cssom_styleSheets; delete globalThis.__folio_cssom_sheetOf;");
    }

    private static ScriptValue Json<T>(T value) => ScriptValue.FromString(JsonSerializer.Serialize(value));

    private static string Text(ReadOnlySpan<ScriptValue> args, int at) => args.Length > at ? args[at].AsString() ?? "" : "";

    private static double Number(ReadOnlySpan<ScriptValue> args, int at) => args.Length > at ? args[at].AsNumber() : 0;

    private static CssomSheet? Sheet(CssomRegistry registry, ReadOnlySpan<ScriptValue> args) => registry.Sheet((int)Number(args, 0));

    private static CssomRule? Rule(CssomRegistry registry, ReadOnlySpan<ScriptValue> args, int at) => registry.Rule((int)Number(args, at));

    // (kind, id): a sheet or a rule as the parent of an insert, delete or listing.
    private static (CssomSheet?, CssomRule?) Parent(CssomRegistry registry, ReadOnlySpan<ScriptValue> args) =>
        Text(args, 0) == "sheet" ? (registry.Sheet((int)Number(args, 1)), null) : (null, registry.Rule((int)Number(args, 1)));

    private static List<object[]> Children(CssomRegistry registry, ReadOnlySpan<ScriptValue> args)
    {
        var (sheet, rule) = Parent(registry, args);
        if (sheet is null && rule is null)
            return [];
        return registry.Children(sheet, rule);
    }

    private const string Source = """
        (function(host) {
            'use strict';
            const INTERNAL = {};
            const sheets = new Map();   // id -> CSSStyleSheet
            const rules = new Map();    // id -> CSSRule
            const lists = new WeakMap(); // owner -> CSSRuleList
            const styles = new WeakMap();
            // A rule that has left its sheet (deleted, or its sheet's text was replaced) is no longer the host's: it reads as empty.
            const DETACHED = { type: 0, cssText: '', prelude: '', href: '', media: '', layerName: null, supports: null, prefix: '', names: [], owned: 0, sheet: 0 };
            const info = (id) => { const json = host.rule(id); return json === null ? DETACHED : JSON.parse(json); };
            const own = Object.freeze({ configurable: true });

            function indexError(message) { return new DOMException(message, 'IndexSizeError'); }

            // A live list: a real array, so indexing, length and iteration just work; item() is the one extra.
            class CSSRuleList extends Array {
                static get [Symbol.species]() { return Array; }
                item(index) { const value = this[index >>> 0]; return value === undefined ? null : value; }
            }
            class StyleSheetList extends Array {
                static get [Symbol.species]() { return Array; }
                item(index) { const value = this[index >>> 0]; return value === undefined ? null : value; }
            }

            function ruleFor(id, kind) {
                let rule = rules.get(id);
                if (rule === undefined) {
                    const Class = CLASSES[kind === undefined ? info(id).kind : kind] || CSSRule;
                    rule = new Class(INTERNAL, id);
                    rules.set(id, rule);
                }
                return rule;
            }

            // The list of a sheet's or a rule's child rules, brought up to date with the host's.
            function listOf(owner, kind, id) {
                let list = lists.get(owner);
                if (list === undefined) { list = new CSSRuleList(); lists.set(owner, list); }
                const children = JSON.parse(host.children(kind, id));
                let same = list.length === children.length;
                for (let i = 0; same && i < children.length; i++) same = rules.get(children[i][0]) === list[i];
                if (!same) {
                    list.length = 0;
                    for (const [childId, childKind] of children) list.push(ruleFor(childId, childKind));
                }
                return list;
            }

            // -------------------------------------------------------------------------- MediaList
            const mediaOwners = new WeakMap();
            class MediaList {
                constructor(token, sheetId) { if (token !== INTERNAL) throw new TypeError('Illegal constructor'); mediaOwners.set(this, sheetId); }
                get mediaText() { return JSON.parse(host.sheet(mediaOwners.get(this))).media; }
                set mediaText(value) { host.setMedia(mediaOwners.get(this), value === null ? '' : String(value)); }
                get length() { return this.#items().length; }
                item(index) { const value = this.#items()[index >>> 0]; return value === undefined ? null : value; }
                appendMedium(medium) {
                    medium = String(medium);
                    const items = this.#items();
                    if (!items.includes(medium)) host.setMedia(mediaOwners.get(this), items.concat(medium).join(', '));
                }
                deleteMedium(medium) {
                    medium = String(medium);
                    const items = this.#items();
                    if (!items.includes(medium)) throw new DOMException('The medium provided (' + medium + ') is not in the list.', 'NotFoundError');
                    host.setMedia(mediaOwners.get(this), items.filter((item) => item !== medium).join(', '));
                }
                toString() { return this.mediaText; }
                #items() { const text = this.mediaText; return text === '' ? [] : text.split(',').map((item) => item.trim()); }
            }

            // -------------------------------------------------------------------------- style sheets
            class StyleSheet {
                constructor(token, id) { if (token !== INTERNAL) throw new TypeError('Illegal constructor'); this._id = id; }
                get type() { return 'text/css'; }
                get href() { return JSON.parse(host.sheet(this._id)).href; }
                get ownerNode() { return host.owner(this._id); }
                get parentStyleSheet() { return null; }
                get title() { return JSON.parse(host.sheet(this._id)).title; }
                get media() { return mediaOf(this); }
                get disabled() { return JSON.parse(host.sheet(this._id)).disabled; }
                set disabled(value) { host.setDisabled(this._id, !!value); }
            }
            const mediaLists = new WeakMap();
            function mediaOf(sheet) {
                let media = mediaLists.get(sheet);
                if (media === undefined) { media = new MediaList(INTERNAL, sheet._id); mediaLists.set(sheet, media); }
                return media;
            }

            class CSSStyleSheet extends StyleSheet {
                get ownerRule() { return null; }
                get cssRules() { return listOf(this, 'sheet', this._id); }
                get rules() { return this.cssRules; }
                insertRule(rule, index) {
                    if (arguments.length === 0) throw new TypeError("Failed to execute 'insertRule' on 'CSSStyleSheet': 1 argument required, but only 0 present.");
                    const at = index === undefined ? 0 : index >>> 0;
                    const result = host.insert('sheet', this._id, String(rule), at);
                    listOf(this, 'sheet', this._id);
                    return result;
                }
                deleteRule(index) {
                    if (arguments.length === 0) throw new TypeError("Failed to execute 'deleteRule' on 'CSSStyleSheet': 1 argument required, but only 0 present.");
                    host.delete('sheet', this._id, index >>> 0);
                    listOf(this, 'sheet', this._id);
                }
                addRule(selector, block, index) {
                    const length = this.cssRules.length;
                    this.insertRule(String(selector) + ' { ' + (block === undefined ? '' : String(block)) + ' }', index === undefined ? length : index);
                    return -1;
                }
                removeRule(index) { this.deleteRule(index === undefined ? 0 : index); }
            }

            function sheetFor(id) {
                let sheet = sheets.get(id);
                if (sheet === undefined) { sheet = new CSSStyleSheet(INTERNAL, id); sheets.set(id, sheet); }
                return sheet;
            }

            const sheetList = new StyleSheetList();
            function styleSheets() {
                const ids = JSON.parse(host.sheets());
                let same = sheetList.length === ids.length;
                for (let i = 0; same && i < ids.length; i++) same = sheets.get(ids[i]) === sheetList[i];
                if (!same) { sheetList.length = 0; for (const id of ids) sheetList.push(sheetFor(id)); }
                return sheetList;
            }
            function sheetOf(element) { const id = host.sheetOf(element); return id === 0 ? null : sheetFor(id); }

            // -------------------------------------------------------------------------- rules
            class CSSRule {
                constructor(token, id) { if (token !== INTERNAL) throw new TypeError('Illegal constructor'); this._id = id; }
                get type() { return info(this._id).type; }
                get cssText() { return info(this._id).cssText; }
                set cssText(value) { /* ignored, as the CSSOM says */ }
                get parentRule() { const owner = info(this._id).owned; return owner === 0 ? null : ruleFor(owner); }
                get parentStyleSheet() { const sheet = info(this._id).sheet; return sheet === 0 ? null : sheetFor(sheet); }
            }
            for (const [name, value] of Object.entries({ STYLE_RULE: 1, CHARSET_RULE: 2, IMPORT_RULE: 3, MEDIA_RULE: 4, FONT_FACE_RULE: 5, PAGE_RULE: 6, KEYFRAMES_RULE: 7, KEYFRAME_RULE: 8, NAMESPACE_RULE: 10, SUPPORTS_RULE: 12 })) {
                Object.defineProperty(CSSRule, name, { value, enumerable: true });
                Object.defineProperty(CSSRule.prototype, name, { value, enumerable: true });
            }

            function styleOf(rule) {
                let style = styles.get(rule);
                if (style === undefined) { style = host.style(rule._id); styles.set(rule, style); }
                return style;
            }

            class CSSGroupingRule extends CSSRule {
                get cssRules() { return listOf(this, 'rule', this._id); }
                insertRule(rule, index) {
                    if (arguments.length === 0) throw new TypeError("Failed to execute 'insertRule' on 'CSSGroupingRule': 1 argument required, but only 0 present.");
                    const result = host.insert('rule', this._id, String(rule), index === undefined ? 0 : index >>> 0);
                    listOf(this, 'rule', this._id);
                    return result;
                }
                deleteRule(index) {
                    if (arguments.length === 0) throw new TypeError("Failed to execute 'deleteRule' on 'CSSGroupingRule': 1 argument required, but only 0 present.");
                    host.delete('rule', this._id, index >>> 0);
                    listOf(this, 'rule', this._id);
                }
            }
            class CSSStyleRule extends CSSGroupingRule {
                get selectorText() { return info(this._id).prelude; }
                set selectorText(value) { host.setSelector(this._id, String(value)); }
                get style() { return styleOf(this); }
                get styleMap() { return undefined; }
            }
            class CSSConditionRule extends CSSGroupingRule {
                get conditionText() { return info(this._id).prelude; }
            }
            class CSSMediaRule extends CSSConditionRule {
                get media() {
                    const rule = this, text = () => info(rule._id).prelude;
                    return { get mediaText() { return text(); }, get length() { return text() === '' ? 0 : text().split(',').length; }, toString: text };
                }
            }
            class CSSSupportsRule extends CSSConditionRule {}
            class CSSContainerRule extends CSSConditionRule {
                get containerName() { const match = /^([^\s(]+)\s*(?=\()/.exec(info(this._id).prelude); return match ? match[1] : ''; }
                get containerQuery() { return info(this._id).prelude.replace(/^[^\s(]+\s*(?=\()/, ''); }
            }
            class CSSLayerBlockRule extends CSSGroupingRule { get name() { return info(this._id).prelude; } }
            class CSSScopeRule extends CSSGroupingRule {}
            class CSSStartingStyleRule extends CSSGroupingRule {}
            class CSSLayerStatementRule extends CSSRule { get nameList() { return info(this._id).names; } }
            class CSSImportRule extends CSSRule {
                get href() { return info(this._id).href; }
                get media() { const rule = this; return { get mediaText() { return info(rule._id).media; }, toString() { return info(rule._id).media; } }; }
                get layerName() { return info(this._id).layerName; }
                get supportsText() { return info(this._id).supports; }
                get styleSheet() { return null; }
            }
            class CSSFontFaceRule extends CSSRule { get style() { return styleOf(this); } }
            class CSSPageRule extends CSSRule {
                get selectorText() { return info(this._id).prelude; }
                get style() { return styleOf(this); }
            }
            class CSSNestedDeclarations extends CSSRule { get style() { return styleOf(this); } }
            class CSSNamespaceRule extends CSSRule {
                get namespaceURI() { return info(this._id).href; }
                get prefix() { return info(this._id).prefix; }
            }
            class CSSPropertyRule extends CSSRule {
                get name() { return info(this._id).prelude; }
                get syntax() { return this.#value('syntax').replace(/^"|"$/g, ''); }
                get inherits() { return this.#value('inherits') === 'true'; }
                get initialValue() { const value = this.#value('initial-value'); return value === '' ? null : value; }
                #value(name) {
                    const match = new RegExp('(?:^|[\\s;{])' + name + '\\s*:\\s*([^;}]*)').exec(info(this._id).cssText);
                    return match ? match[1].trim() : '';
                }
            }
            class CSSKeyframeRule extends CSSRule {
                get keyText() { return info(this._id).prelude; }
                set keyText(value) { host.setKeyText(this._id, String(value)); }
                get style() { return styleOf(this); }
            }
            class CSSKeyframesRule extends CSSRule {
                get name() { return info(this._id).prelude; }
                get cssRules() { return listOf(this, 'rule', this._id); }
                get length() { return this.cssRules.length; }
                appendRule(rule) {
                    host.insert('rule', this._id, String(rule), this.cssRules.length);
                    listOf(this, 'rule', this._id);
                }
                deleteRule(select) {
                    const index = this.#find(select);
                    if (index >= 0) { host.delete('rule', this._id, index); listOf(this, 'rule', this._id); }
                }
                findRule(select) { const index = this.#find(select); return index < 0 ? null : this.cssRules[index]; }
                #find(select) {
                    const key = String(select).trim().toLowerCase().split(',').map((part) => part.trim()).join(', ');
                    const list = this.cssRules;
                    for (let i = list.length - 1; i >= 0; i--) if (list[i].keyText === key) return i;
                    return -1;
                }
            }

            const CLASSES = {
                Style: CSSStyleRule, Media: CSSMediaRule, Supports: CSSSupportsRule, Container: CSSContainerRule, LayerBlock: CSSLayerBlockRule,
                LayerStatement: CSSLayerStatementRule, Scope: CSSScopeRule, StartingStyle: CSSStartingStyleRule, Import: CSSImportRule,
                FontFace: CSSFontFaceRule, Page: CSSPageRule, Keyframes: CSSKeyframesRule, Keyframe: CSSKeyframeRule, Namespace: CSSNamespaceRule,
                Property: CSSPropertyRule, NestedDeclarations: CSSNestedDeclarations,
            };

            for (const Class of [StyleSheet, CSSStyleSheet, MediaList, CSSRuleList, StyleSheetList, CSSRule, CSSGroupingRule, CSSStyleRule, CSSConditionRule,
                                 CSSMediaRule, CSSSupportsRule, CSSContainerRule, CSSLayerBlockRule, CSSScopeRule, CSSStartingStyleRule, CSSLayerStatementRule,
                                 CSSImportRule, CSSFontFaceRule, CSSPageRule, CSSNestedDeclarations, CSSNamespaceRule, CSSPropertyRule, CSSKeyframeRule, CSSKeyframesRule]) {
                Object.defineProperty(globalThis, Class.name, { value: Class, writable: true, configurable: true, enumerable: false });
                Object.defineProperty(Class.prototype, Symbol.toStringTag, { value: Class.name, configurable: true });
            }
            globalThis.__folio_cssom_styleSheets = styleSheets;
            globalThis.__folio_cssom_sheetOf = sheetOf;
        })(globalThis.__folio_cssom_host);
        """;
}
