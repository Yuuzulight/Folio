using System.Text;
using Folio.Dom;
using Folio.Style;

namespace Folio.Scripting.Dom;

/// <summary>
/// Defines host object templates for DOM interfaces conforming to W3C / WHATWG WebIDL specifications (study 02, #451).
/// </summary>
internal sealed class DomNodeTemplates
{
    private readonly DomContext _context;
    private readonly Dictionary<ElementNode, CssStyleSheetState> _styleSheets = new();

    public DomNodeTemplates(DomContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));

        NodeTemplate = CreateNodeTemplate();
        ElementTemplate = CreateElementTemplate(NodeTemplate);
        HtmlElementTemplate = CreateHtmlElementTemplate(ElementTemplate);
        SvgElementTemplate = CreateSvgElementTemplate(ElementTemplate);
        CharacterDataTemplate = CreateCharacterDataTemplate(NodeTemplate);
        TextTemplate = CreateTextTemplate(CharacterDataTemplate);
        CommentTemplate = CreateCommentTemplate(CharacterDataTemplate);
        DocumentTypeTemplate = CreateDocumentTypeTemplate(NodeTemplate);
        DocumentFragmentTemplate = CreateDocumentFragmentTemplate(NodeTemplate);
        DocumentTemplate = CreateDocumentTemplate(NodeTemplate);
        NodeListTemplate = CreateNodeListTemplate();
        HtmlCollectionTemplate = CreateHtmlCollectionTemplate();

        DomTokenListTemplate = CreateDomTokenListTemplate();
        DomStringMapTemplate = CreateDomStringMapTemplate();
        NamedNodeMapTemplate = CreateNamedNodeMapTemplate();
        StyleDeclarationTemplate = CreateStyleDeclarationTemplate();
        ComputedStyleDeclarationTemplate = CreateComputedStyleDeclarationTemplate();
        DomRectTemplate = CreateDomRectTemplate();
        AttrTemplate = CreateAttrTemplate(NodeTemplate);
        MutationRecordTemplate = new HostObjectTemplate("MutationRecord")
            .DefineProperty("type", (target, _) => ScriptValue.FromString(((MutationRecordState)target).Type))
            .DefineProperty("attributeName", (target, _) => ((MutationRecordState)target).AttributeName is { } name ? ScriptValue.FromString(name) : ScriptValue.Null)
            .DefineProperty("oldValue", (target, _) => ((MutationRecordState)target).OldValue is { } value ? ScriptValue.FromString(value) : ScriptValue.Null)
            .DefineProperty("target", (target, _) => _context.Wrap(((MutationRecordState)target).Target))
            .DefineProperty("addedNodes", (target, _) => _context.Engine.CreateArray(((MutationRecordState)target).AddedNodes.Select(_context.Wrap).ToArray()))
            .DefineProperty("removedNodes", (target, _) => _context.Engine.CreateArray(((MutationRecordState)target).RemovedNodes.Select(_context.Wrap).ToArray()))
            .DefineProperty("previousSibling", (_, _) => ScriptValue.Null)
            .DefineProperty("nextSibling", (_, _) => ScriptValue.Null);
        MutationObserverTemplate = new HostObjectTemplate("MutationObserver")
            .DefineMethod("observe", (target, _, args) =>
            {
                if (args.Length == 0) throw WebIdlException.TypeError("observe requires a target.");
                _context.ObserveMutations((MutationObserverState)target, _context.UnwrapNode(args[0]));
                return ScriptValue.Undefined;
            })
            .DefineMethod("disconnect", (target, _, _) => { _context.DisconnectMutations((MutationObserverState)target); return ScriptValue.Undefined; })
            .DefineMethod("takeRecords", (target, _, _) => _context.TakeMutationRecords((MutationObserverState)target));
        MessageEventTemplate = new HostObjectTemplate("MessageEvent")
            .DefineProperty("data", (target, _) => ((MessageEventState)target).Data)
            .DefineProperty("origin", (target, _) => ScriptValue.FromString(((MessageEventState)target).Origin))
            .DefineProperty("source", (target, _) => ((MessageEventState)target).Source)
            .DefineProperty("type", (target, _) => ScriptValue.FromString(((MessageEventState)target).Type))
            .DefineProperty("target", (target, _) => ((MessageEventState)target).Target);
        MediaEventTemplate = new HostObjectTemplate("Event")
            .DefineProperty("type", (target, _) => ScriptValue.FromString(((MediaEventState)target).Type));

        HtmlInputElementTemplate = CreateHtmlInputElementTemplate(HtmlElementTemplate);
        HtmlButtonElementTemplate = CreateHtmlButtonElementTemplate(HtmlElementTemplate);
        HtmlAnchorElementTemplate = CreateHtmlAnchorElementTemplate(HtmlElementTemplate);
        HtmlImageElementTemplate = CreateHtmlImageElementTemplate(HtmlElementTemplate);
        HtmlOptionElementTemplate = CreateHtmlOptionElementTemplate(HtmlElementTemplate);
        HtmlSelectElementTemplate = CreateHtmlSelectElementTemplate(HtmlElementTemplate);
        HtmlTextAreaElementTemplate = CreateHtmlTextAreaElementTemplate(HtmlElementTemplate);
        HtmlTemplateElementTemplate = CreateHtmlTemplateElementTemplate(HtmlElementTemplate);
        HtmlScriptElementTemplate = CreateHtmlScriptElementTemplate(HtmlElementTemplate);
        HtmlCanvasElementTemplate = CreateHtmlCanvasElementTemplate(HtmlElementTemplate);
        HtmlMediaElementTemplate = CreateHtmlMediaElementTemplate(HtmlElementTemplate);
        HtmlAudioElementTemplate = CreateHtmlMediaElementTemplate(HtmlMediaElementTemplate, "HTMLAudioElement");
        HtmlVideoElementTemplate = CreateHtmlMediaElementTemplate(HtmlMediaElementTemplate, "HTMLVideoElement");
        HtmlIFrameElementTemplate = CreateHtmlIFrameElementTemplate(HtmlElementTemplate);
        TextTrackCueTemplate = new HostObjectTemplate("TextTrackCue")
            .DefineProperty("startTime", (target, _) => ScriptValue.FromNumber(((TextTrackCueState)target).StartTime))
            .DefineProperty("endTime", (target, _) => ScriptValue.FromNumber(((TextTrackCueState)target).EndTime))
            .DefineProperty("text", (target, _) => ScriptValue.FromString(((TextTrackCueState)target).Text));
        TimeRangesTemplate = new HostObjectTemplate("TimeRanges")
            .DefineProperty("length", (target, _) => ScriptValue.FromNumber(((MediaTimeRangesState)target).Ranges.Count))
            .DefineMethod("start", (target, _, args) => GetTimeRange((MediaTimeRangesState)target, args, start: true))
            .DefineMethod("end", (target, _, args) => GetTimeRange((MediaTimeRangesState)target, args, start: false));
        TextTrackTemplate = new HostObjectTemplate("TextTrack")
            .DefineProperty("kind", (target, _) => ScriptValue.FromString(((MediaTextTrackState)target).Kind))
            .DefineProperty("label", (target, _) => ScriptValue.FromString(((MediaTextTrackState)target).Label))
            .DefineProperty("language", (target, _) => ScriptValue.FromString(((MediaTextTrackState)target).Language))
            .DefineProperty("mode", (target, _) => ScriptValue.FromString(((MediaTextTrackState)target).Mode),
                (target, _, value) => ((MediaTextTrackState)target).Mode = WebIdlConversions.ToDOMString(value))
            .DefineProperty("cues", (target, _) => _context.Engine.CreateArray(((MediaTextTrackState)target).Cues.Select(cue => _context.Engine.CreateHostObject(TextTrackCueTemplate, cue)).ToArray()))
            .DefineMethod("addCue", (target, _, args) =>
            {
                if (args.Length < 3) throw WebIdlException.TypeError("addCue requires start time, end time, and text.");
                ((MediaTextTrackState)target).Cues.Add(new TextTrackCueState(WebIdlConversions.ToDouble(args[0]), WebIdlConversions.ToDouble(args[1]), WebIdlConversions.ToDOMString(args[2])));
                return ScriptValue.Undefined;
            });
        CanvasGradientTemplate = new HostObjectTemplate("CanvasGradient")
            .DefineMethod("addColorStop", (target, _, args) =>
            {
                if (args.Length > 1)
                    ((CanvasGradientState)target).Stops.Add((Math.Clamp(WebIdlConversions.ToDouble(args[0]), 0, 1), WebIdlConversions.ToDOMString(args[1])));
                return ScriptValue.Undefined;
            });
        TextMetricsTemplate = new HostObjectTemplate("TextMetrics")
            .DefineProperty("width", (target, _) => ScriptValue.FromNumber(((CanvasTextMetricsState)target).Width));
        ImageDataTemplate = new HostObjectTemplate("ImageData")
            .DefineProperty("width", (target, _) => ScriptValue.FromNumber(((CanvasImageDataState)target).Width))
            .DefineProperty("height", (target, _) => ScriptValue.FromNumber(((CanvasImageDataState)target).Height))
            .DefineProperty("data", (target, _) => ((CanvasImageDataState)target).GetDataArray(_context.Engine));
        CanvasRenderingContextTemplate = new HostObjectTemplate("CanvasRenderingContext2D")
            .DefineProperty("fillStyle", (target, _) => ScriptValue.FromString(((CanvasElementState)target).FillStyle), (target, _, value) => SetCanvasStyle((CanvasElementState)target, value, stroke: false))
            .DefineProperty("strokeStyle", (target, _) => ScriptValue.FromString(((CanvasElementState)target).StrokeStyle), (target, _, value) => SetCanvasStyle((CanvasElementState)target, value, stroke: true))
            .DefineProperty("font", (target, _) => ScriptValue.FromString(((CanvasElementState)target).Font), (target, _, value) => ((CanvasElementState)target).Font = WebIdlConversions.ToDOMString(value))
            .DefineProperty("textAlign", (target, _) => ScriptValue.FromString(((CanvasElementState)target).TextAlign), (target, _, value) => ((CanvasElementState)target).TextAlign = WebIdlConversions.ToDOMString(value))
            .DefineProperty("textBaseline", (target, _) => ScriptValue.FromString(((CanvasElementState)target).TextBaseline), (target, _, value) => ((CanvasElementState)target).TextBaseline = WebIdlConversions.ToDOMString(value))
            .DefineProperty("shadowColor", (target, _) => ScriptValue.FromString(((CanvasElementState)target).ShadowColor), (target, _, value) => ((CanvasElementState)target).ShadowColor = WebIdlConversions.ToDOMString(value))
            .DefineProperty("shadowBlur", (target, _) => ScriptValue.FromNumber(((CanvasElementState)target).ShadowBlur), (target, _, value) => ((CanvasElementState)target).ShadowBlur = Math.Max(0, WebIdlConversions.ToDouble(value)))
            .DefineProperty("shadowOffsetX", (target, _) => ScriptValue.FromNumber(((CanvasElementState)target).ShadowOffsetX), (target, _, value) => ((CanvasElementState)target).ShadowOffsetX = WebIdlConversions.ToDouble(value))
            .DefineProperty("shadowOffsetY", (target, _) => ScriptValue.FromNumber(((CanvasElementState)target).ShadowOffsetY), (target, _, value) => ((CanvasElementState)target).ShadowOffsetY = WebIdlConversions.ToDouble(value))
            .DefineProperty("lineWidth", (target, _) => ScriptValue.FromNumber(((CanvasElementState)target).LineWidth), (target, _, value) => ((CanvasElementState)target).LineWidth = Math.Max(0, WebIdlConversions.ToDouble(value)))
            .DefineProperty("lineCap", (target, _) => ScriptValue.FromString(((CanvasElementState)target).LineCap), (target, _, value) => ((CanvasElementState)target).LineCap = WebIdlConversions.ToDOMString(value))
            .DefineProperty("lineJoin", (target, _) => ScriptValue.FromString(((CanvasElementState)target).LineJoin), (target, _, value) => ((CanvasElementState)target).LineJoin = WebIdlConversions.ToDOMString(value))
            .DefineProperty("globalAlpha", (target, _) => ScriptValue.FromNumber(((CanvasElementState)target).GlobalAlpha), (target, _, value) => ((CanvasElementState)target).GlobalAlpha = Math.Clamp(WebIdlConversions.ToDouble(value), 0, 1))
            .DefineProperty("globalCompositeOperation", (target, _) => ScriptValue.FromString(((CanvasElementState)target).GlobalCompositeOperation), (target, _, value) => ((CanvasElementState)target).GlobalCompositeOperation = WebIdlConversions.ToDOMString(value))
            .DefineMethod("save", (target, _, _) => SaveCanvasState((CanvasElementState)target))
            .DefineMethod("restore", (target, _, _) => RestoreCanvasState((CanvasElementState)target))
            .DefineMethod("setLineDash", (target, _, args) => SetLineDash((CanvasElementState)target, args))
            .DefineMethod("getLineDash", (target, _, _) => _context.Engine.CreateArray(((CanvasElementState)target).LineDash.Select(ScriptValue.FromNumber).ToArray()))
            .DefineMethod("translate", (target, _, args) => AddCanvasCommand((CanvasElementState)target, "translate", args.ToArray()))
            .DefineMethod("scale", (target, _, args) => AddCanvasCommand((CanvasElementState)target, "scale", args.ToArray()))
            .DefineMethod("rotate", (target, _, args) => AddCanvasCommand((CanvasElementState)target, "rotate", args.ToArray()))
            .DefineMethod("setTransform", (target, _, args) => AddCanvasCommand((CanvasElementState)target, "setTransform", args.ToArray()))
            .DefineMethod("resetTransform", (target, _, _) => AddCanvasCommand((CanvasElementState)target, "resetTransform", []))
            .DefineMethod("fillText", (target, _, args) => AddTextCommand((CanvasElementState)target, "fillText", args.ToArray()))
            .DefineMethod("strokeText", (target, _, args) => AddTextCommand((CanvasElementState)target, "strokeText", args.ToArray()))
            .DefineMethod("measureText", (_, _, args) =>
            {
                var text = args.Length > 0 ? WebIdlConversions.ToDOMString(args[0]) : "";
                return _context.Engine.CreateHostObject(TextMetricsTemplate, new CanvasTextMetricsState(text.Length * 7));
            })
            .DefineMethod("drawImage", (target, _, _) => { ((CanvasElementState)target).Commands.Add(new CanvasCommand("drawImage", 0, 0, 0, 0, "")); return ScriptValue.Undefined; })
            .DefineMethod("getImageData", (target, _, args) =>
            {
                var width = args.Length > 2 ? Math.Max(0, WebIdlConversions.ToInt32(args[2])) : 0;
                var height = args.Length > 3 ? Math.Max(0, WebIdlConversions.ToInt32(args[3])) : 0;
                var image = new CanvasImageDataState(width, height);
                var state = (CanvasElementState)target;
                Array.Copy(state.Pixels, image.Data, Math.Min(state.Pixels.Length, image.Data.Length));
                return _context.Engine.CreateHostObject(ImageDataTemplate, image);
            })
            .DefineMethod("createImageData", (_, _, args) =>
            {
                var width = args.Length > 0 ? Math.Max(0, WebIdlConversions.ToInt32(args[0])) : 0;
                var height = args.Length > 1 ? Math.Max(0, WebIdlConversions.ToInt32(args[1])) : 0;
                return _context.Engine.CreateHostObject(ImageDataTemplate, new CanvasImageDataState(width, height));
            })
            .DefineMethod("putImageData", (target, _, args) =>
            {
                if (args.Length > 0 && _context.Engine.TryGetHostTarget(args[0], out var imageTarget) && imageTarget is CanvasImageDataState image)
                {
                    var state = (CanvasElementState)target;
                    if (_context.Engine.TryGetArrayItems(image.GetDataArray(_context.Engine), out var values))
                    {
                        for (var i = 0; i < image.Data.Length && i < values.Count; i++)
                            image.Data[i] = Math.Clamp(WebIdlConversions.ToInt32(values[i]), 0, 255);
                    }
                    Array.Copy(image.Data, state.Pixels, Math.Min(image.Data.Length, state.Pixels.Length));
                }
                return ScriptValue.Undefined;
            })
            .DefineMethod("createLinearGradient", (_, _, args) =>
            {
                var values = args.ToArray();
                double Number(int index) => index < values.Length ? WebIdlConversions.ToDouble(values[index]) : 0;
                return _context.Engine.CreateHostObject(CanvasGradientTemplate, new CanvasGradientState(Number(0), Number(1), Number(2), Number(3)));
            })
            .DefineMethod("fillRect", (target, _, args) => AddCanvasCommand((CanvasElementState)target, "fillRect", args.ToArray()))
            .DefineMethod("clearRect", (target, _, args) => AddCanvasCommand((CanvasElementState)target, "clearRect", args.ToArray()))
            .DefineMethod("strokeRect", (target, _, args) => AddCanvasCommand((CanvasElementState)target, "strokeRect", args.ToArray()))
            .DefineMethod("beginPath", (target, _, _) => { ((CanvasElementState)target).Commands.Add(new CanvasCommand("beginPath", 0, 0, 0, 0, "")); return ScriptValue.Undefined; })
            .DefineMethod("moveTo", (target, _, args) => AddPathCommand((CanvasElementState)target, "moveTo", args.ToArray()))
            .DefineMethod("lineTo", (target, _, args) => AddPathCommand((CanvasElementState)target, "lineTo", args.ToArray()))
            .DefineMethod("rect", (target, _, args) => AddCanvasCommand((CanvasElementState)target, "rect", args.ToArray()))
            .DefineMethod("quadraticCurveTo", (target, _, args) => AddCanvasCommand((CanvasElementState)target, "quadraticCurveTo", args.ToArray()))
            .DefineMethod("bezierCurveTo", (target, _, args) => AddCanvasCommand((CanvasElementState)target, "bezierCurveTo", args.ToArray()))
            .DefineMethod("arc", (target, _, args) => AddCanvasCommand((CanvasElementState)target, "arc", args.ToArray()))
            .DefineMethod("arcTo", (target, _, args) => AddCanvasCommand((CanvasElementState)target, "arcTo", args.ToArray()))
            .DefineMethod("closePath", (target, _, _) => { ((CanvasElementState)target).Commands.Add(new CanvasCommand("closePath", 0, 0, 0, 0, "")); return ScriptValue.Undefined; })
            .DefineMethod("fill", (target, _, _) => { var state = (CanvasElementState)target; state.Commands.Add(new CanvasCommand("fill", 0, 0, 0, 0, state.FillStyle, Composite: state.GlobalCompositeOperation)); return ScriptValue.Undefined; })
            .DefineMethod("stroke", (target, _, _) => { var state = (CanvasElementState)target; state.Commands.Add(new CanvasCommand("stroke", 0, 0, 0, 0, state.FillStyle, state.StrokeStyle, Composite: state.GlobalCompositeOperation)); return ScriptValue.Undefined; });
        WebGlRenderingContextTemplate = CreateWebGlRenderingContextTemplate();
        WebGlLoseContextTemplate = new HostObjectTemplate("WEBGLLoseContext")
            .DefineMethod("loseContext", (target, _, _) => { ((WebGlContextState)target).Lose(); return ScriptValue.Undefined; })
            .DefineMethod("restoreContext", (target, _, _) => { ((WebGlContextState)target).Restore(); return ScriptValue.Undefined; });
        Path2DTemplate = new HostObjectTemplate("Path2D")
            .DefineMethod("moveTo", (target, _, args) => AddPathSegment((CanvasPathState)target, "moveTo", args.ToArray()))
            .DefineMethod("lineTo", (target, _, args) => AddPathSegment((CanvasPathState)target, "lineTo", args.ToArray()))
            .DefineMethod("rect", (target, _, args) => AddPathSegment((CanvasPathState)target, "rect", args.ToArray()))
            .DefineMethod("quadraticCurveTo", (target, _, args) => AddPathSegment((CanvasPathState)target, "quadraticCurveTo", args.ToArray()))
            .DefineMethod("bezierCurveTo", (target, _, args) => AddPathSegment((CanvasPathState)target, "bezierCurveTo", args.ToArray()))
            .DefineMethod("arc", (target, _, args) => AddPathSegment((CanvasPathState)target, "arc", args.ToArray()))
            .DefineMethod("closePath", (target, _, _) => { ((CanvasPathState)target).Segments.Add(("closePath", 0, 0)); return ScriptValue.Undefined; });
        CssRuleTemplate = new HostObjectTemplate("CSSStyleRule")
            .DefineProperty("cssText", (target, _) => ScriptValue.FromString(((CssRuleState)target).CssText));
        CssRuleStyleTemplate = new HostObjectTemplate("CSSStyleDeclaration")
            .DefineProperty("length", (target, _) => ScriptValue.FromNumber(((CssRuleStyleState)target).Properties.Count))
            .DefineMethod("getPropertyValue", (target, _, args) =>
            {
                var name = args.Length > 0 ? WebIdlConversions.ToDOMString(args[0]) : "";
                return ScriptValue.FromString(((CssRuleStyleState)target).Properties.GetValueOrDefault(name, ""));
            })
            .DefineMethod("setProperty", (target, _, args) =>
            {
                if (args.Length > 1)
                    ((CssRuleStyleState)target).Properties[WebIdlConversions.ToDOMString(args[0])] = WebIdlConversions.ToDOMString(args[1]);
                return ScriptValue.Undefined;
            });
        CssRuleTemplate
            .DefineProperty("selectorText", (target, _) => ScriptValue.FromString(((CssRuleState)target).SelectorText))
            .DefineProperty("style", (target, _) => _context.Engine.CreateHostObject(CssRuleStyleTemplate, ((CssRuleState)target).Style));
        CssStyleSheetTemplate = new HostObjectTemplate("CSSStyleSheet")
            .DefineProperty("cssRules", (target, _) =>
            {
                var sheet = (CssStyleSheetState)target;
                sheet.RefreshFromSource();
                return _context.Engine.CreateArray(sheet.Rules.Select(rule => _context.Engine.CreateHostObject(CssRuleTemplate, new CssRuleState(rule))).ToArray());
            })
            .DefineMethod("insertRule", (target, _, args) =>
            {
                var text = args.Length > 0 ? WebIdlConversions.ToDOMString(args[0]) : "";
                var index = args.Length > 1 ? Math.Clamp(WebIdlConversions.ToInt32(args[1]), 0, ((CssStyleSheetState)target).Rules.Count) : ((CssStyleSheetState)target).Rules.Count;
                var sheet = (CssStyleSheetState)target;
                sheet.Rules.Insert(index, text);
                sheet.SyncToSource();
                return ScriptValue.FromNumber(index);
            })
            .DefineMethod("deleteRule", (target, _, args) =>
            {
                if (args.Length > 0 && WebIdlConversions.ToInt32(args[0]) is var index && index >= 0 && index < ((CssStyleSheetState)target).Rules.Count)
                {
                    var sheet = (CssStyleSheetState)target;
                    sheet.Rules.RemoveAt(index);
                    sheet.SyncToSource();
                }
                return ScriptValue.Undefined;
            });
    }

    public HostObjectTemplate NodeTemplate { get; }
    public HostObjectTemplate ElementTemplate { get; }
    public HostObjectTemplate HtmlElementTemplate { get; }
    public HostObjectTemplate SvgElementTemplate { get; }
    public HostObjectTemplate CharacterDataTemplate { get; }
    public HostObjectTemplate TextTemplate { get; }
    public HostObjectTemplate CommentTemplate { get; }
    public HostObjectTemplate DocumentTypeTemplate { get; }
    public HostObjectTemplate DocumentFragmentTemplate { get; }
    public HostObjectTemplate DocumentTemplate { get; }
    public HostObjectTemplate NodeListTemplate { get; }
    public HostObjectTemplate HtmlCollectionTemplate { get; }
    public HostObjectTemplate DomTokenListTemplate { get; }
    public HostObjectTemplate DomStringMapTemplate { get; }
    public HostObjectTemplate NamedNodeMapTemplate { get; }
    public HostObjectTemplate AttrTemplate { get; }
    public HostObjectTemplate StyleDeclarationTemplate { get; }
    public HostObjectTemplate ComputedStyleDeclarationTemplate { get; }
    public HostObjectTemplate DomRectTemplate { get; }
    public HostObjectTemplate MutationRecordTemplate { get; }
    public HostObjectTemplate MutationObserverTemplate { get; }
    public HostObjectTemplate MessageEventTemplate { get; }
    public HostObjectTemplate MediaEventTemplate { get; }
    public HostObjectTemplate HtmlInputElementTemplate { get; }
    public HostObjectTemplate HtmlButtonElementTemplate { get; }
    public HostObjectTemplate HtmlAnchorElementTemplate { get; }
    public HostObjectTemplate HtmlImageElementTemplate { get; }
    public HostObjectTemplate HtmlOptionElementTemplate { get; }
    public HostObjectTemplate HtmlSelectElementTemplate { get; }
    public HostObjectTemplate HtmlTextAreaElementTemplate { get; }
    public HostObjectTemplate HtmlTemplateElementTemplate { get; }
    public HostObjectTemplate HtmlScriptElementTemplate { get; }
    public HostObjectTemplate HtmlCanvasElementTemplate { get; }
    public HostObjectTemplate HtmlMediaElementTemplate { get; }
    public HostObjectTemplate HtmlAudioElementTemplate { get; }
    public HostObjectTemplate HtmlVideoElementTemplate { get; }
    public HostObjectTemplate HtmlIFrameElementTemplate { get; }
    public HostObjectTemplate TextTrackTemplate { get; }
    public HostObjectTemplate TextTrackCueTemplate { get; }
    public HostObjectTemplate TimeRangesTemplate { get; }
    public HostObjectTemplate CanvasRenderingContextTemplate { get; }
    public HostObjectTemplate WebGlRenderingContextTemplate { get; }
    public HostObjectTemplate WebGlLoseContextTemplate { get; }
    public HostObjectTemplate CanvasGradientTemplate { get; }
    public HostObjectTemplate TextMetricsTemplate { get; }
    public HostObjectTemplate ImageDataTemplate { get; }
    public HostObjectTemplate Path2DTemplate { get; }
    public HostObjectTemplate CssRuleTemplate { get; }
    public HostObjectTemplate CssRuleStyleTemplate { get; }
    public HostObjectTemplate CssStyleSheetTemplate { get; }

    private static ScriptValue AddCanvasCommand(CanvasElementState state, string name, ScriptValue[] args)
    {
        double Number(int index) => index < args.Length ? WebIdlConversions.ToDouble(args[index]) : 0;
        state.Commands.Add(new CanvasCommand(name, Number(0), Number(1), Number(2), Number(3), state.FillStyle,
            name.StartsWith("stroke", StringComparison.Ordinal) ? state.StrokeStyle : null, Composite: state.GlobalCompositeOperation,
            Arguments: args.Select(WebIdlConversions.ToDouble).ToArray()));
        return ScriptValue.Undefined;
    }

    private void SetCanvasStyle(CanvasElementState state, ScriptValue value, bool stroke)
    {
        var style = _context.Engine.TryGetHostTarget(value, out var target) && target is CanvasGradientState gradient && gradient.Stops.Count > 0
            ? gradient.PaintStyle
            : WebIdlConversions.ToDOMString(value);
        if (stroke) state.StrokeStyle = style;
        else state.FillStyle = style;
    }

    private static ScriptValue SaveCanvasState(CanvasElementState state)
    {
        state.SavedStyles.Push(new CanvasStyleSnapshot(state.FillStyle, state.LineWidth, state.LineCap, state.LineJoin, state.GlobalAlpha, state.GlobalCompositeOperation, state.LineDash.ToArray()));
        return ScriptValue.Undefined;
    }

    private static ScriptValue RestoreCanvasState(CanvasElementState state)
    {
        if (state.SavedStyles.TryPop(out var snapshot))
        {
            state.FillStyle = snapshot.FillStyle;
            state.LineWidth = snapshot.LineWidth;
            state.LineCap = snapshot.LineCap;
            state.LineJoin = snapshot.LineJoin;
            state.GlobalAlpha = snapshot.GlobalAlpha;
            state.GlobalCompositeOperation = snapshot.GlobalCompositeOperation;
            state.LineDash.Clear();
            state.LineDash.AddRange(snapshot.LineDash);
        }
        return ScriptValue.Undefined;
    }

    private ScriptValue SetLineDash(CanvasElementState state, ReadOnlySpan<ScriptValue> args)
    {
        state.LineDash.Clear();
        var values = args.Length == 1 && _context.Engine.TryGetArrayItems(args[0], out var array) ? array : args.ToArray();
        foreach (var value in values)
            if (value.IsNumber)
                state.LineDash.Add(Math.Max(0, value.AsNumber()));
        return ScriptValue.Undefined;
    }

    private static ScriptValue AddPathCommand(CanvasElementState state, string name, ScriptValue[] args)
    {
        double Number(int index) => index < args.Length ? WebIdlConversions.ToDouble(args[index]) : 0;
        var (x, y) = name switch
        {
            "quadraticCurveTo" => (Number(2), Number(3)),
            "bezierCurveTo" => (Number(4), Number(5)),
            _ => (Number(0), Number(1)),
        };
        state.Commands.Add(new CanvasCommand(name, x, y, 0, 0, "", Arguments: args.Select(WebIdlConversions.ToDouble).ToArray()));
        return ScriptValue.Undefined;
    }

    private static ScriptValue AddTextCommand(CanvasElementState state, string name, ScriptValue[] args)
    {
        double Number(int index) => index < args.Length ? WebIdlConversions.ToDouble(args[index]) : 0;
        var text = args.Length > 0 ? WebIdlConversions.ToDOMString(args[0]) : "";
        state.Commands.Add(new CanvasCommand(name, Number(1), Number(2), text.Length * 7, 10, state.FillStyle, state.StrokeStyle, text, state.GlobalCompositeOperation));
        return ScriptValue.Undefined;
    }

    private static ScriptValue AddPathSegment(CanvasPathState path, string name, ScriptValue[] args)
    {
        double Number(int index) => index < args.Length ? WebIdlConversions.ToDouble(args[index]) : 0;
        path.Segments.Add((name, Number(0), Number(1)));
        return ScriptValue.Undefined;
    }

    private HostObjectTemplate CreateHtmlCanvasElementTemplate(IHostObjectTemplate htmlElementProto)
    {
        var t = new HostObjectTemplate("HTMLCanvasElement", htmlElementProto);
        t.DefineProperty("width", (target, _) => ScriptValue.FromNumber(ReadDimension((ElementNode)target, "width", 300)), (target, _, value) => ((ElementNode)target).SetAttribute("width", WebIdlConversions.ToUint32(value).ToString()));
        t.DefineProperty("height", (target, _) => ScriptValue.FromNumber(ReadDimension((ElementNode)target, "height", 150)), (target, _, value) => ((ElementNode)target).SetAttribute("height", WebIdlConversions.ToUint32(value).ToString()));
        t.DefineMethod("getContext", (target, _, args) =>
        {
            if (args.Length == 0) return ScriptValue.Null;
            var kind = WebIdlConversions.ToDOMString(args[0]);
            if (string.Equals(kind, "2d", StringComparison.OrdinalIgnoreCase))
                return _context.Engine.CreateHostObject(CanvasRenderingContextTemplate, _context.GetCanvas((ElementNode)target));
            if (string.Equals(kind, "webgl2", StringComparison.OrdinalIgnoreCase) && _context.Capabilities.WebGl)
            {
                var canvas = _context.GetCanvas((ElementNode)target);
                return _context.Engine.CreateHostObject(WebGlRenderingContextTemplate, canvas.WebGlContext ??= new WebGlContextState(_context.Engine, canvas, _context.MaxGpuBytes));
            }
            return ScriptValue.Null;
        });
        t.DefineMethod("toDataURL", (_, _, _) => ScriptValue.FromString("data:image/png;base64,"));
        return t;

        static uint ReadDimension(ElementNode element, string name, uint fallback) =>
            uint.TryParse(element.GetAttribute(name), out var value) ? value : fallback;
    }

    private HostObjectTemplate CreateHtmlMediaElementTemplate(IHostObjectTemplate htmlElementProto, string interfaceName = "HTMLMediaElement")
    {
        var t = new HostObjectTemplate(interfaceName, htmlElementProto);
        t.DefineProperty("src", (target, _) => ScriptValue.FromString(((ElementNode)target).GetAttribute("src") ?? ""),
            (target, _, value) => ((ElementNode)target).SetAttribute("src", WebIdlConversions.ToDOMString(value)));
        t.DefineProperty("currentSrc", (target, _) => ScriptValue.FromString(((ElementNode)target).GetAttribute("src") ?? ""));
        t.DefineProperty("srcObject", (target, _) => _context.GetMedia((ElementNode)target).SrcObject,
            (target, _, value) => _context.GetMedia((ElementNode)target).SrcObject = value);
        t.DefineProperty("currentTime", (target, _) => ScriptValue.FromNumber(_context.GetMedia((ElementNode)target).CurrentTime),
            (target, _, value) => { var state = _context.GetMedia((ElementNode)target); state.CurrentTime = Math.Max(0, WebIdlConversions.ToDouble(value)); if (state.Duration > 0 && state.CurrentTime >= state.Duration && !state.Ended) { state.Ended = true; state.Dispatch(state.OnEnded, "ended"); } });
        t.DefineProperty("duration", (target, _) => ScriptValue.FromNumber(_context.GetMedia((ElementNode)target).Duration));
        t.DefineProperty("paused", (target, _) => ScriptValue.FromBoolean(_context.GetMedia((ElementNode)target).Paused));
        t.DefineProperty("ended", (target, _) => ScriptValue.FromBoolean(_context.GetMedia((ElementNode)target).Ended));
        t.DefineProperty("volume", (target, _) => ScriptValue.FromNumber(_context.GetMedia((ElementNode)target).Volume),
            (target, _, value) => _context.GetMedia((ElementNode)target).Volume = Math.Clamp(WebIdlConversions.ToDouble(value), 0, 1));
        t.DefineProperty("muted", (target, _) => ScriptValue.FromBoolean(_context.GetMedia((ElementNode)target).Muted),
            (target, _, value) => _context.GetMedia((ElementNode)target).Muted = WebIdlConversions.ToBoolean(value));
        t.DefineProperty("autoplay", (target, _) => ScriptValue.FromBoolean(_context.GetMedia((ElementNode)target).Autoplay),
            (target, _, value) => _context.GetMedia((ElementNode)target).Autoplay = WebIdlConversions.ToBoolean(value));
        t.DefineProperty("loop", (target, _) => ScriptValue.FromBoolean(_context.GetMedia((ElementNode)target).Loop),
            (target, _, value) => _context.GetMedia((ElementNode)target).Loop = WebIdlConversions.ToBoolean(value));
        t.DefineProperty("controls", (target, _) => ScriptValue.FromBoolean(_context.GetMedia((ElementNode)target).Controls),
            (target, _, value) => _context.GetMedia((ElementNode)target).Controls = WebIdlConversions.ToBoolean(value));
        t.DefineProperty("playbackRate", (target, _) => ScriptValue.FromNumber(_context.GetMedia((ElementNode)target).PlaybackRate),
            (target, _, value) => _context.GetMedia((ElementNode)target).PlaybackRate = WebIdlConversions.ToDouble(value));
        t.DefineProperty("defaultPlaybackRate", (target, _) => ScriptValue.FromNumber(_context.GetMedia((ElementNode)target).DefaultPlaybackRate),
            (target, _, value) => _context.GetMedia((ElementNode)target).DefaultPlaybackRate = WebIdlConversions.ToDouble(value));
        t.DefineProperty("preservesPitch", (target, _) => ScriptValue.FromBoolean(_context.GetMedia((ElementNode)target).PreservesPitch),
            (target, _, value) => _context.GetMedia((ElementNode)target).PreservesPitch = WebIdlConversions.ToBoolean(value));
        t.DefineProperty("readyState", (target, _) => ScriptValue.FromNumber(_context.GetMedia((ElementNode)target).ReadyState));
        t.DefineProperty("networkState", (target, _) => ScriptValue.FromNumber(_context.GetMedia((ElementNode)target).NetworkState));
        t.DefineProperty("error", (target, _) => _context.GetMedia((ElementNode)target).Error is { } error ? ScriptValue.FromString(error) : ScriptValue.Null);
        t.DefineProperty("onloadedmetadata", (target, _) => _context.GetMedia((ElementNode)target).OnLoadedMetadata,
            (target, _, value) => _context.GetMedia((ElementNode)target).OnLoadedMetadata = value);
        t.DefineProperty("onplay", (target, _) => _context.GetMedia((ElementNode)target).OnPlay,
            (target, _, value) => _context.GetMedia((ElementNode)target).OnPlay = value);
        t.DefineProperty("onpause", (target, _) => _context.GetMedia((ElementNode)target).OnPause,
            (target, _, value) => _context.GetMedia((ElementNode)target).OnPause = value);
        t.DefineProperty("onended", (target, _) => _context.GetMedia((ElementNode)target).OnEnded,
            (target, _, value) => _context.GetMedia((ElementNode)target).OnEnded = value);
        t.DefineMethod("addEventListener", (target, _, args) => { if (args.Length > 1) _context.GetMedia((ElementNode)target).AddEventListener(WebIdlConversions.ToDOMString(args[0]), args[1]); return ScriptValue.Undefined; });
        t.DefineMethod("removeEventListener", (target, _, args) => { if (args.Length > 1) _context.GetMedia((ElementNode)target).RemoveEventListener(WebIdlConversions.ToDOMString(args[0]), args[1]); return ScriptValue.Undefined; });
        t.DefineProperty("buffered", (target, _) => CreateMediaRanges((ElementNode)target, buffered: true));
        t.DefineProperty("seekable", (target, _) => CreateMediaRanges((ElementNode)target, buffered: false));
        t.DefineProperty("played", (target, _) => CreateMediaRanges((ElementNode)target, buffered: null));
        t.DefineMethod("play", (target, _, _) =>
        {
            var state = _context.GetMedia((ElementNode)target);
            state.Play();
            return state.Error is null
                ? _context.Engine.Evaluate("Promise.resolve()")
                : _context.Engine.Evaluate("Promise.reject(new DOMException('Media playback is unavailable.', 'NotSupportedError'))");
        });
        t.DefineMethod("pause", (target, _, _) => { _context.GetMedia((ElementNode)target).Pause(); return ScriptValue.Undefined; });
        t.DefineMethod("load", (target, _, _) => { _context.GetMedia((ElementNode)target).Load(); return ScriptValue.Undefined; });
        t.DefineMethod("canPlayType", (target, _, args) =>
        {
            if (!_context.GetMedia((ElementNode)target).Enabled || args.Length == 0) return ScriptValue.FromString("");
            var type = WebIdlConversions.ToDOMString(args[0]).ToLowerInvariant();
            return ScriptValue.FromString(type.Contains("audio") || type.Contains("video") ? "maybe" : "");
        });
        t.DefineMethod("addTextTrack", (target, _, args) =>
        {
            var kind = args.Length > 0 ? WebIdlConversions.ToDOMString(args[0]) : "metadata";
            var label = args.Length > 1 ? WebIdlConversions.ToDOMString(args[1]) : "";
            var language = args.Length > 2 ? WebIdlConversions.ToDOMString(args[2]) : "";
            var track = new MediaTextTrackState(kind, label, language);
            _context.GetMedia((ElementNode)target).TextTracks.Add(track);
            return _context.Engine.CreateHostObject(TextTrackTemplate, track);
        });
        return t;
    }

    private ScriptValue CreateMediaRanges(ElementNode element, bool? buffered)
    {
        var state = _context.GetMedia(element);
        var ranges = buffered switch
        {
            true when state.Duration > 0 => new[] { (0d, state.Duration) },
            false when state.Duration > 0 => new[] { (0d, state.Duration) },
            null when state.CurrentTime > 0 => new[] { (0d, Math.Min(state.CurrentTime, state.Duration > 0 ? state.Duration : state.CurrentTime)) },
            _ => Array.Empty<(double, double)>()
        };
        return _context.Engine.CreateHostObject(TimeRangesTemplate, new MediaTimeRangesState(ranges));
    }

    private static ScriptValue GetTimeRange(MediaTimeRangesState state, ReadOnlySpan<ScriptValue> args, bool start)
    {
        var index = args.Length > 0 ? WebIdlConversions.ToInt32(args[0]) : -1;
        if (index < 0 || index >= state.Ranges.Count)
            throw WebIdlException.DomException(DomExceptionNames.IndexSizeError, "Time range index is out of bounds.");
        return ScriptValue.FromNumber(start ? state.Ranges[index].Start : state.Ranges[index].End);
    }

    private HostObjectTemplate CreateHtmlIFrameElementTemplate(IHostObjectTemplate htmlElementProto)
    {
        var windowTemplate = new HostObjectTemplate("WindowProxy")
            .DefineProperty("closed", (target, _) => ScriptValue.FromBoolean(((IframeState)target).Closed))
            .DefineProperty("origin", (target, _) => ScriptValue.FromString(((IframeState)target).Origin))
            .DefineProperty("onmessage", (target, _) => ((IframeState)target).OnMessage, (target, _, value) => ((IframeState)target).OnMessage = value)
            .DefineMethod("addEventListener", (target, _, args) => { if (args.Length > 1) ((IframeState)target).AddMessageListener(args[1]); return ScriptValue.Undefined; })
            .DefineMethod("removeEventListener", (target, _, args) => { if (args.Length > 1) ((IframeState)target).RemoveMessageListener(args[1]); return ScriptValue.Undefined; })
            .DefineMethod("postMessage", (target, _, args) => { if (args.Length > 0) ((IframeState)target).PostMessage(args[0], args.Length > 1 ? WebIdlConversions.ToDOMString(args[1]) : "*"); return ScriptValue.Undefined; })
            .DefineMethod("close", (target, _, _) => { ((IframeState)target).Close(); return ScriptValue.Undefined; });
        var t = new HostObjectTemplate("HTMLIFrameElement", htmlElementProto);
        t.DefineProperty("src", (target, _) => ScriptValue.FromString(((ElementNode)target).GetAttribute("src") ?? ""),
            (target, _, value) => ((ElementNode)target).SetAttribute("src", WebIdlConversions.ToDOMString(value)));
        t.DefineProperty("sandbox", (target, _) => ScriptValue.FromString(((ElementNode)target).GetAttribute("sandbox") ?? ""),
            (target, _, value) => ((ElementNode)target).SetAttribute("sandbox", WebIdlConversions.ToDOMString(value)));
        t.DefineProperty("contentWindow", (target, _) =>
        {
            var state = _context.GetIframe((ElementNode)target);
            if (!state.Window.IsObject)
                state.Window = _context.Engine.CreateHostObject(windowTemplate, state);
            return state.Window;
        });
        t.DefineProperty("contentDocument", (_, _) => ScriptValue.Null);
        return t;
    }

    private HostObjectTemplate CreateWebGlRenderingContextTemplate()
    {
        var t = new HostObjectTemplate("WebGL2RenderingContext");
        t.DefineProperty("ARRAY_BUFFER", (_, _) => ScriptValue.FromNumber(0x8892));
        t.DefineProperty("ELEMENT_ARRAY_BUFFER", (_, _) => ScriptValue.FromNumber(0x8893));
        t.DefineProperty("STATIC_DRAW", (_, _) => ScriptValue.FromNumber(0x88E4));
        t.DefineProperty("VERTEX_SHADER", (_, _) => ScriptValue.FromNumber(0x8B31));
        t.DefineProperty("FRAGMENT_SHADER", (_, _) => ScriptValue.FromNumber(0x8B30));
        t.DefineProperty("COMPILE_STATUS", (_, _) => ScriptValue.FromNumber(0x8B81));
        t.DefineProperty("LINK_STATUS", (_, _) => ScriptValue.FromNumber(0x8B82));
        t.DefineProperty("TRIANGLES", (_, _) => ScriptValue.FromNumber(0x0004));
        t.DefineProperty("COLOR_BUFFER_BIT", (_, _) => ScriptValue.FromNumber(0x4000));
        t.DefineProperty("DEPTH_BUFFER_BIT", (_, _) => ScriptValue.FromNumber(0x0100));
        t.DefineProperty("DEPTH_TEST", (_, _) => ScriptValue.FromNumber(0x0B71));
        t.DefineProperty("LEQUAL", (_, _) => ScriptValue.FromNumber(0x0203));
        t.DefineProperty("BLEND", (_, _) => ScriptValue.FromNumber(0x0BE2));
        t.DefineProperty("SRC_ALPHA", (_, _) => ScriptValue.FromNumber(0x0302));
        t.DefineProperty("ONE_MINUS_SRC_ALPHA", (_, _) => ScriptValue.FromNumber(0x0303));
        t.DefineProperty("FUNC_ADD", (_, _) => ScriptValue.FromNumber(0x8006));
        t.DefineProperty("STENCIL_TEST", (_, _) => ScriptValue.FromNumber(0x0B90));
        t.DefineProperty("TEXTURE_2D", (_, _) => ScriptValue.FromNumber(0x0DE1));
        t.DefineProperty("RGBA", (_, _) => ScriptValue.FromNumber(0x1908));
        t.DefineProperty("UNSIGNED_BYTE", (_, _) => ScriptValue.FromNumber(0x1401));
        t.DefineProperty("UNSIGNED_SHORT", (_, _) => ScriptValue.FromNumber(0x1403));
        t.DefineProperty("UNSIGNED_INT", (_, _) => ScriptValue.FromNumber(0x1405));
        t.DefineProperty("FLOAT", (_, _) => ScriptValue.FromNumber(0x1406));
        t.DefineProperty("VERTEX_ATTRIB_ARRAY_ENABLED", (_, _) => ScriptValue.FromNumber(0x8622));
        t.DefineProperty("VERTEX_ATTRIB_ARRAY_SIZE", (_, _) => ScriptValue.FromNumber(0x8623));
        t.DefineProperty("VERTEX_ATTRIB_ARRAY_STRIDE", (_, _) => ScriptValue.FromNumber(0x8624));
        t.DefineProperty("VERTEX_ATTRIB_ARRAY_TYPE", (_, _) => ScriptValue.FromNumber(0x8625));
        t.DefineProperty("TEXTURE_MIN_FILTER", (_, _) => ScriptValue.FromNumber(0x2801));
        t.DefineProperty("TEXTURE_MAG_FILTER", (_, _) => ScriptValue.FromNumber(0x2800));
        t.DefineProperty("TEXTURE_WRAP_S", (_, _) => ScriptValue.FromNumber(0x2802));
        t.DefineProperty("TEXTURE_WRAP_T", (_, _) => ScriptValue.FromNumber(0x2803));
        t.DefineProperty("LINEAR", (_, _) => ScriptValue.FromNumber(0x2601));
        t.DefineProperty("NEAREST", (_, _) => ScriptValue.FromNumber(0x2600));
        t.DefineProperty("CLAMP_TO_EDGE", (_, _) => ScriptValue.FromNumber(0x812F));
        t.DefineProperty("TEXTURE0", (_, _) => ScriptValue.FromNumber(0x84C0));
        t.DefineProperty("FRAMEBUFFER", (_, _) => ScriptValue.FromNumber(0x8D40));
        t.DefineProperty("FRAMEBUFFER_COMPLETE", (_, _) => ScriptValue.FromNumber(0x8CD5));
        t.DefineProperty("COLOR_ATTACHMENT0", (_, _) => ScriptValue.FromNumber(0x8CE0));
        t.DefineProperty("FRAMEBUFFER_ATTACHMENT_OBJECT_TYPE", (_, _) => ScriptValue.FromNumber(0x8CD0));
        t.DefineProperty("FRAMEBUFFER_ATTACHMENT_OBJECT_NAME", (_, _) => ScriptValue.FromNumber(0x8CD1));
        t.DefineProperty("TEXTURE", (_, _) => ScriptValue.FromNumber(0x1702));
        t.DefineProperty("VERTEX_ARRAY_BINDING", (_, _) => ScriptValue.FromNumber(0x85B5));
        t.DefineProperty("MAX_TEXTURE_SIZE", (_, _) => ScriptValue.FromNumber(0x0D33));
        t.DefineProperty("MAX_VERTEX_ATTRIBS", (_, _) => ScriptValue.FromNumber(0x8869));
        t.DefineProperty("MAX_COMBINED_TEXTURE_IMAGE_UNITS", (_, _) => ScriptValue.FromNumber(0x8B4D));
        t.DefineProperty("VERSION", (_, _) => ScriptValue.FromNumber(0x1F02));
        t.DefineProperty("VENDOR", (_, _) => ScriptValue.FromNumber(0x1F00));
        t.DefineProperty("RENDERER", (_, _) => ScriptValue.FromNumber(0x1F01));
        t.DefineProperty("SHADING_LANGUAGE_VERSION", (_, _) => ScriptValue.FromNumber(0x8B8C));
        t.DefineProperty("VIEWPORT", (_, _) => ScriptValue.FromNumber(0x0BA2));
        t.DefineProperty("SCISSOR_BOX", (_, _) => ScriptValue.FromNumber(0x0C10));
        t.DefineProperty("LOW_FLOAT", (_, _) => ScriptValue.FromNumber(0x8DF0));
        t.DefineProperty("MEDIUM_FLOAT", (_, _) => ScriptValue.FromNumber(0x8DF1));
        t.DefineProperty("HIGH_FLOAT", (_, _) => ScriptValue.FromNumber(0x8DF2));
        t.DefineProperty("drawingBufferWidth", (target, _) => ScriptValue.FromNumber(ReadCanvasDimension(((WebGlContextState)target).Canvas, "width", 300)));
        t.DefineProperty("drawingBufferHeight", (target, _) => ScriptValue.FromNumber(ReadCanvasDimension(((WebGlContextState)target).Canvas, "height", 150)));
        t.DefineProperty("oncontextlost", (target, _) => ((WebGlContextState)target).OnContextLost, (target, _, value) => ((WebGlContextState)target).OnContextLost = value);
        t.DefineProperty("oncontextrestored", (target, _) => ((WebGlContextState)target).OnContextRestored, (target, _, value) => ((WebGlContextState)target).OnContextRestored = value);
        t.DefineMethod("getParameter", (target, _, args) =>
        {
            if (args.Length == 0) return ScriptValue.Null;
            return WebIdlConversions.ToUint32(args[0]) switch
            {
                0x0D33 => ScriptValue.FromNumber(4096),
                0x8869 => ScriptValue.FromNumber(16),
                0x8B4D => ScriptValue.FromNumber(16),
                0x1F02 => ScriptValue.FromString("WebGL 2.0 Folio"),
                0x1F00 => ScriptValue.FromString("Folio"),
                0x1F01 => ScriptValue.FromString("Folio bounded renderer"),
                0x8B8C => ScriptValue.FromString("WebGL GLSL ES 3.00"),
                0x0BA2 => _context.Engine.Evaluate($"[{((WebGlContextState)target).ViewportX}, {((WebGlContextState)target).ViewportY}, {((WebGlContextState)target).ViewportWidth}, {((WebGlContextState)target).ViewportHeight}]"),
                0x0C10 => _context.Engine.Evaluate($"[{((WebGlContextState)target).ScissorX}, {((WebGlContextState)target).ScissorY}, {((WebGlContextState)target).ScissorWidth}, {((WebGlContextState)target).ScissorHeight}]"),
                _ => ScriptValue.Null
            };
        });
        t.DefineMethod("getShaderPrecisionFormat", (_, _, _) => _context.Engine.Evaluate("({ rangeMin: 127, rangeMax: 127, precision: 23 })"));
        t.DefineMethod("viewport", (target, _, args) => { if (args.Length > 3) { var state = (WebGlContextState)target; state.ViewportX = WebIdlConversions.ToInt32(args[0]); state.ViewportY = WebIdlConversions.ToInt32(args[1]); state.ViewportWidth = Math.Max(0, WebIdlConversions.ToInt32(args[2])); state.ViewportHeight = Math.Max(0, WebIdlConversions.ToInt32(args[3])); } return ScriptValue.Undefined; });
        t.DefineMethod("scissor", (target, _, args) => { if (args.Length > 3) { var state = (WebGlContextState)target; state.ScissorX = WebIdlConversions.ToInt32(args[0]); state.ScissorY = WebIdlConversions.ToInt32(args[1]); state.ScissorWidth = Math.Max(0, WebIdlConversions.ToInt32(args[2])); state.ScissorHeight = Math.Max(0, WebIdlConversions.ToInt32(args[3])); } return ScriptValue.Undefined; });
        t.DefineMethod("getSupportedExtensions", (target, _, _) => _context.Engine.CreateArray([ScriptValue.FromString("WEBGL_lose_context")]));
        t.DefineMethod("getContextAttributes", (_, _, _) => _context.Engine.Evaluate("({ alpha: true, depth: true, stencil: false, antialias: false, premultipliedAlpha: true, preserveDrawingBuffer: false })"));
        t.DefineMethod("createBuffer", (target, _, _) => { var state = (WebGlContextState)target; var handle = state.CreateHandle(); state.Buffers[handle] = []; return ScriptValue.FromNumber(handle); });
        t.DefineMethod("bindBuffer", (target, _, args) => { var state = (WebGlContextState)target; state.BoundArrayBuffer = args.Length > 1 ? (uint?)WebIdlConversions.ToUint32(args[1]) : null; return ScriptValue.Undefined; });
        t.DefineMethod("bufferData", (target, _, args) =>
        {
            var state = (WebGlContextState)target;
            if (state.BoundArrayBuffer is not { } handle || args.Length < 2) { state.SetError(0x0502); return ScriptValue.Undefined; }
            var size = args[1].IsNumber
                ? Math.Clamp(WebIdlConversions.ToInt32(args[1]), 0, 16 * 1024 * 1024)
                : _context.Engine.TryGetArrayItems(args[1], out var data)
                    ? Math.Clamp(data.Count * 4, 0, 16 * 1024 * 1024)
                    : 0;
            state.ReplaceBuffer(handle, size);
            return ScriptValue.Undefined;
        });
        t.DefineMethod("deleteBuffer", (target, _, args) => { if (args.Length > 0) ((WebGlContextState)target).DeleteBuffer(WebIdlConversions.ToUint32(args[0])); return ScriptValue.Undefined; });
        t.DefineMethod("isBuffer", (target, _, args) => ScriptValue.FromBoolean(args.Length > 0 && ((WebGlContextState)target).Buffers.ContainsKey(WebIdlConversions.ToUint32(args[0]))));
        t.DefineMethod("createShader", (target, _, args) => { var state = (WebGlContextState)target; var handle = state.CreateHandle(); state.Shaders[handle] = (args.Length > 0 ? WebIdlConversions.ToInt32(args[0]) : 0, "", false); return ScriptValue.FromNumber(handle); });
        t.DefineMethod("shaderSource", (target, _, args) => { var state = (WebGlContextState)target; if (args.Length > 1) state.Shaders[WebIdlConversions.ToUint32(args[0])] = (0, WebIdlConversions.ToDOMString(args[1]), false); return ScriptValue.Undefined; });
        t.DefineMethod("compileShader", (target, _, args) => { var state = (WebGlContextState)target; if (args.Length > 0 && state.Shaders.TryGetValue(WebIdlConversions.ToUint32(args[0]), out var shader)) state.Shaders[WebIdlConversions.ToUint32(args[0])] = shader with { Compiled = shader.Source.Length <= 1_000_000 }; return ScriptValue.Undefined; });
        t.DefineMethod("deleteShader", (target, _, args) => { if (args.Length > 0) ((WebGlContextState)target).DeleteShader(WebIdlConversions.ToUint32(args[0])); return ScriptValue.Undefined; });
        t.DefineMethod("isShader", (target, _, args) => ScriptValue.FromBoolean(args.Length > 0 && ((WebGlContextState)target).Shaders.ContainsKey(WebIdlConversions.ToUint32(args[0]))));
        t.DefineMethod("getShaderParameter", (target, _, args) => ScriptValue.FromBoolean(args.Length > 0 && ((WebGlContextState)target).Shaders.TryGetValue(WebIdlConversions.ToUint32(args[0]), out var shader) && shader.Compiled));
        t.DefineMethod("getShaderInfoLog", (target, _, args) => ScriptValue.FromString(args.Length > 0 && ((WebGlContextState)target).Shaders.TryGetValue(WebIdlConversions.ToUint32(args[0]), out var shader) && !shader.Compiled ? "Shader source exceeds the host limit." : ""));
        t.DefineMethod("createProgram", (target, _, _) => { var state = (WebGlContextState)target; var handle = state.CreateHandle(); state.Programs.Add(handle); return ScriptValue.FromNumber(handle); });
        t.DefineMethod("attachShader", (target, _, args) =>
        {
            var state = (WebGlContextState)target;
            if (args.Length > 1)
            {
                var program = WebIdlConversions.ToUint32(args[0]);
                var shader = WebIdlConversions.ToUint32(args[1]);
                if (!state.Shaders.ContainsKey(shader)) state.SetError(0x0501);
                else
                {
                    if (!state.ProgramShaders.TryGetValue(program, out var attached))
                        state.ProgramShaders[program] = attached = [];
                    if (!attached.Contains(shader)) attached.Add(shader);
                }
            }
            return ScriptValue.Undefined;
        });
        t.DefineMethod("linkProgram", (target, _, args) =>
        {
            var state = (WebGlContextState)target;
            if (args.Length > 0)
            {
                var program = WebIdlConversions.ToUint32(args[0]);
                state.Programs.Add(program);
                var linked = !state.ProgramShaders.TryGetValue(program, out var shaders) || shaders.All(shader => state.Shaders.TryGetValue(shader, out var value) && value.Compiled);
                if (linked) state.LinkedPrograms.Add(program); else state.LinkedPrograms.Remove(program);
            }
            return ScriptValue.Undefined;
        });
        t.DefineMethod("detachShader", (target, _, args) =>
        {
            if (args.Length > 1 && ((WebGlContextState)target).ProgramShaders.TryGetValue(WebIdlConversions.ToUint32(args[0]), out var attached))
                attached.Remove(WebIdlConversions.ToUint32(args[1]));
            return ScriptValue.Undefined;
        });
        t.DefineMethod("deleteProgram", (target, _, args) => { if (args.Length > 0) ((WebGlContextState)target).DeleteProgram(WebIdlConversions.ToUint32(args[0])); return ScriptValue.Undefined; });
        t.DefineMethod("isProgram", (target, _, args) => ScriptValue.FromBoolean(args.Length > 0 && ((WebGlContextState)target).Programs.Contains(WebIdlConversions.ToUint32(args[0]))));
        t.DefineMethod("getProgramParameter", (target, _, args) => ScriptValue.FromBoolean(args.Length > 1 && WebIdlConversions.ToUint32(args[1]) == 0x8B82 && ((WebGlContextState)target).LinkedPrograms.Contains(WebIdlConversions.ToUint32(args[0]))));
        t.DefineMethod("getProgramInfoLog", (target, _, args) => ScriptValue.FromString(args.Length > 0 && !((WebGlContextState)target).LinkedPrograms.Contains(WebIdlConversions.ToUint32(args[0])) ? "Program is not linked." : ""));
        t.DefineMethod("useProgram", (target, _, args) => { ((WebGlContextState)target).CurrentProgram = args.Length > 1 ? WebIdlConversions.ToUint32(args[1]) : null; return ScriptValue.Undefined; });
        t.DefineMethod("getAttribLocation", (target, _, args) =>
            ScriptValue.FromNumber(args.Length > 1 && ((WebGlContextState)target).LinkedPrograms.Contains(WebIdlConversions.ToUint32(args[0])) ? 0 : -1));
        t.DefineMethod("getUniformLocation", (target, _, args) =>
            args.Length > 1 && ((WebGlContextState)target).LinkedPrograms.Contains(WebIdlConversions.ToUint32(args[0])) ? ScriptValue.FromNumber(0) : ScriptValue.Null);
        t.DefineMethod("enableVertexAttribArray", (target, _, args) => { if (args.Length > 0) ((WebGlContextState)target).EnabledVertexAttributes.Add(WebIdlConversions.ToInt32(args[0])); return ScriptValue.Undefined; });
        t.DefineMethod("disableVertexAttribArray", (target, _, args) => { if (args.Length > 0) ((WebGlContextState)target).EnabledVertexAttributes.Remove(WebIdlConversions.ToInt32(args[0])); return ScriptValue.Undefined; });
        t.DefineMethod("vertexAttribPointer", (target, _, args) => { var state = (WebGlContextState)target; if (args.Length < 6) { state.SetError(0x0502); return ScriptValue.Undefined; } var index = WebIdlConversions.ToInt32(args[0]); var size = WebIdlConversions.ToInt32(args[1]); if (index < 0 || size < 1 || size > 4) { state.SetError(0x0501); return ScriptValue.Undefined; } state.VertexAttribPointers[index] = (size, WebIdlConversions.ToUint32(args[2]), WebIdlConversions.ToBoolean(args[3]), WebIdlConversions.ToInt32(args[4]), WebIdlConversions.ToInt32(args[5])); return ScriptValue.Undefined; });
        t.DefineMethod("getVertexAttrib", (target, _, args) => { var state = (WebGlContextState)target; if (args.Length < 2) return ScriptValue.Null; var index = WebIdlConversions.ToInt32(args[0]); var pname = WebIdlConversions.ToUint32(args[1]); var enabled = state.EnabledVertexAttributes.Contains(index); if (pname == 0x8622) return ScriptValue.FromBoolean(enabled); if (!state.VertexAttribPointers.TryGetValue(index, out var pointer)) return pname == 0x8623 ? ScriptValue.FromNumber(4) : ScriptValue.FromNumber(0); return pname switch { 0x8623 => ScriptValue.FromNumber(pointer.Size), 0x8624 => ScriptValue.FromNumber(pointer.Stride), 0x8625 => ScriptValue.FromNumber(pointer.Type), _ => ScriptValue.Null }; });
        t.DefineMethod("uniform1f", (target, _, args) => StoreUniform((WebGlContextState)target, args, 1));
        t.DefineMethod("uniform1i", (target, _, args) => StoreUniform((WebGlContextState)target, args, 1));
        t.DefineMethod("uniform4f", (target, _, args) => StoreUniform((WebGlContextState)target, args, 4));
        t.DefineMethod("uniformMatrix4fv", (target, _, args) => StoreUniform((WebGlContextState)target, args, 16));
        t.DefineMethod("enable", (target, _, args) => { if (args.Length > 0) ((WebGlContextState)target).EnabledCapabilities.Add(WebIdlConversions.ToUint32(args[0])); return ScriptValue.Undefined; });
        t.DefineMethod("disable", (target, _, args) => { if (args.Length > 0) ((WebGlContextState)target).EnabledCapabilities.Remove(WebIdlConversions.ToUint32(args[0])); return ScriptValue.Undefined; });
        t.DefineMethod("clearDepth", (target, _, args) => { if (args.Length > 0) ((WebGlContextState)target).ClearDepth = Math.Clamp((float)WebIdlConversions.ToDouble(args[0]), 0, 1); return ScriptValue.Undefined; });
        t.DefineMethod("depthFunc", (target, _, args) => { if (args.Length > 0) ((WebGlContextState)target).DepthFunction = WebIdlConversions.ToUint32(args[0]); return ScriptValue.Undefined; });
        t.DefineMethod("blendFunc", (target, _, args) => { if (args.Length > 1) { var state = (WebGlContextState)target; state.BlendSourceFactor = WebIdlConversions.ToUint32(args[0]); state.BlendDestinationFactor = WebIdlConversions.ToUint32(args[1]); } return ScriptValue.Undefined; });
        t.DefineMethod("blendEquation", (target, _, args) => { if (args.Length > 0) ((WebGlContextState)target).BlendEquation = WebIdlConversions.ToUint32(args[0]); return ScriptValue.Undefined; });
        t.DefineMethod("stencilFunc", (target, _, args) => { if (args.Length > 2) { var state = (WebGlContextState)target; state.StencilFunction = WebIdlConversions.ToUint32(args[0]); state.StencilReference = WebIdlConversions.ToInt32(args[1]); state.StencilMask = WebIdlConversions.ToUint32(args[2]); } return ScriptValue.Undefined; });
        t.DefineMethod("stencilOp", (target, _, args) => { if (args.Length > 2) { var state = (WebGlContextState)target; state.StencilFail = WebIdlConversions.ToUint32(args[0]); state.StencilDepthFail = WebIdlConversions.ToUint32(args[1]); state.StencilDepthPass = WebIdlConversions.ToUint32(args[2]); } return ScriptValue.Undefined; });
        t.DefineMethod("clearStencil", (target, _, args) => { if (args.Length > 0) ((WebGlContextState)target).ClearStencil = WebIdlConversions.ToInt32(args[0]); return ScriptValue.Undefined; });
        t.DefineMethod("drawArrays", (target, _, args) => { if (args.Length > 3) ((WebGlContextState)target).AddDrawCall(); return ScriptValue.Undefined; });
        t.DefineMethod("drawElements", (target, _, args) => { if (args.Length > 3) ((WebGlContextState)target).AddDrawCall(); return ScriptValue.Undefined; });
        t.DefineMethod("createTexture", (target, _, _) => { var state = (WebGlContextState)target; var handle = state.CreateHandle(); state.Textures[handle] = (0, 0, []); state.TextureParameters[handle] = []; return ScriptValue.FromNumber(handle); });
        t.DefineMethod("activeTexture", (target, _, args) => { if (args.Length > 0) ((WebGlContextState)target).ActiveTextureUnit = WebIdlConversions.ToUint32(args[0]); return ScriptValue.Undefined; });
        t.DefineMethod("bindTexture", (target, _, args) => { var state = (WebGlContextState)target; state.BoundTexture = args.Length > 1 ? (uint?)WebIdlConversions.ToUint32(args[1]) : null; return ScriptValue.Undefined; });
        t.DefineMethod("texImage2D", (target, _, args) =>
        {
            var state = (WebGlContextState)target;
            if (state.BoundTexture is not { } handle || args.Length < 9) { state.SetError(0x0502); return ScriptValue.Undefined; }
            var width = Math.Clamp(WebIdlConversions.ToInt32(args[3]), 0, 4096);
            var height = Math.Clamp(WebIdlConversions.ToInt32(args[4]), 0, 4096);
            var bytes = Math.Min(width * height * 4, 64 * 1024 * 1024);
            state.ReplaceTexture(handle, width, height, bytes);
            return ScriptValue.Undefined;
        });
        t.DefineMethod("texParameteri", (target, _, args) => { var state = (WebGlContextState)target; if (state.BoundTexture is not { } handle || args.Length < 3) { state.SetError(0x0502); return ScriptValue.Undefined; } if (!state.TextureParameters.TryGetValue(handle, out var parameters)) state.TextureParameters[handle] = parameters = []; parameters[WebIdlConversions.ToUint32(args[1])] = WebIdlConversions.ToInt32(args[2]); return ScriptValue.Undefined; });
        t.DefineMethod("getTexParameter", (target, _, args) => { var state = (WebGlContextState)target; if (state.BoundTexture is not { } handle || args.Length < 2 || !state.TextureParameters.TryGetValue(handle, out var parameters) || !parameters.TryGetValue(WebIdlConversions.ToUint32(args[1]), out var value)) return ScriptValue.Null; return ScriptValue.FromNumber(value); });
        t.DefineMethod("deleteTexture", (target, _, args) => { if (args.Length > 0) ((WebGlContextState)target).DeleteTexture(WebIdlConversions.ToUint32(args[0])); return ScriptValue.Undefined; });
        t.DefineMethod("isTexture", (target, _, args) => ScriptValue.FromBoolean(args.Length > 0 && ((WebGlContextState)target).Textures.ContainsKey(WebIdlConversions.ToUint32(args[0]))));
        t.DefineMethod("createFramebuffer", (target, _, _) => { var state = (WebGlContextState)target; var handle = state.CreateHandle(); state.Framebuffers.Add(handle); return ScriptValue.FromNumber(handle); });
        t.DefineMethod("deleteFramebuffer", (target, _, args) =>
        {
            if (args.Length > 0)
            {
                var state = (WebGlContextState)target;
                var handle = WebIdlConversions.ToUint32(args[0]);
                state.DeleteFramebuffer(handle);
            }
            return ScriptValue.Undefined;
        });
        t.DefineMethod("isFramebuffer", (target, _, args) => ScriptValue.FromBoolean(args.Length > 0 && ((WebGlContextState)target).Framebuffers.Contains(WebIdlConversions.ToUint32(args[0]))));
        t.DefineMethod("createVertexArray", (target, _, _) => { var state = (WebGlContextState)target; var handle = state.CreateHandle(); state.VertexArrays.Add(handle); return ScriptValue.FromNumber(handle); });
        t.DefineMethod("bindVertexArray", (target, _, args) => { var state = (WebGlContextState)target; state.BoundVertexArray = args.Length > 0 && !args[0].IsNullOrUndefined ? WebIdlConversions.ToUint32(args[0]) : null; return ScriptValue.Undefined; });
        t.DefineMethod("deleteVertexArray", (target, _, args) => { if (args.Length > 0) { var state = (WebGlContextState)target; var handle = WebIdlConversions.ToUint32(args[0]); state.VertexArrays.Remove(handle); if (state.BoundVertexArray == handle) state.BoundVertexArray = null; } return ScriptValue.Undefined; });
        t.DefineMethod("isVertexArray", (target, _, args) => ScriptValue.FromBoolean(args.Length > 0 && ((WebGlContextState)target).VertexArrays.Contains(WebIdlConversions.ToUint32(args[0]))));
        t.DefineMethod("bindFramebuffer", (target, _, args) => { var state = (WebGlContextState)target; state.BoundFramebuffer = args.Length > 1 ? (uint?)WebIdlConversions.ToUint32(args[1]) : null; return ScriptValue.Undefined; });
        t.DefineMethod("framebufferTexture2D", (target, _, args) => { var state = (WebGlContextState)target; if (state.BoundFramebuffer is not { } framebuffer || args.Length < 5) { state.SetError(0x0502); return ScriptValue.Undefined; } var texture = args[3].IsNullOrUndefined ? 0u : WebIdlConversions.ToUint32(args[3]); if (texture != 0 && !state.Textures.ContainsKey(texture)) state.SetError(0x0502); else if (texture == 0) state.FramebufferColorAttachments.Remove(framebuffer); else state.FramebufferColorAttachments[framebuffer] = texture; return ScriptValue.Undefined; });
        t.DefineMethod("checkFramebufferStatus", (target, _, _) => { var state = (WebGlContextState)target; return ScriptValue.FromNumber(state.BoundFramebuffer is { } framebuffer && state.FramebufferColorAttachments.ContainsKey(framebuffer) ? 0x8CD5 : 0x8CD6); });
        t.DefineMethod("getFramebufferAttachmentParameter", (target, _, args) => { var state = (WebGlContextState)target; if (state.BoundFramebuffer is not { } framebuffer || args.Length < 3 || !state.FramebufferColorAttachments.TryGetValue(framebuffer, out var texture)) return ScriptValue.Null; return WebIdlConversions.ToUint32(args[2]) switch { 0x8CD0 => ScriptValue.FromNumber(0x1702), 0x8CD1 => ScriptValue.FromNumber(texture), _ => ScriptValue.Null }; });
        t.DefineMethod("readPixels", (target, _, args) =>
        {
            var state = (WebGlContextState)target;
            if (args.Length < 7 || !state.BoundFramebuffer.HasValue) { state.SetError(0x0502); return ScriptValue.Undefined; }
            var width = Math.Clamp(WebIdlConversions.ToInt32(args[3]), 0, 4096);
            var height = Math.Clamp(WebIdlConversions.ToInt32(args[4]), 0, 4096);
            var count = Math.Min(width * height * 4, 16 * 1024 * 1024);
            if (args.Length > 6)
                for (var i = 0; i < count; i++)
                    _context.Engine.TrySetArrayItem(args[6], i, ScriptValue.FromNumber(0));
            return ScriptValue.Undefined;
        });
        t.DefineMethod("clearColor", (target, _, args) => { if (args.Length > 3) ((WebGlContextState)target).SetClearColor((float)WebIdlConversions.ToDouble(args[0]), (float)WebIdlConversions.ToDouble(args[1]), (float)WebIdlConversions.ToDouble(args[2]), (float)WebIdlConversions.ToDouble(args[3])); return ScriptValue.Undefined; });
        t.DefineMethod("clear", (target, _, args) => { if (args.Length > 0) ((WebGlContextState)target).Clear(WebIdlConversions.ToUint32(args[0])); return ScriptValue.Undefined; });
        t.DefineMethod("getError", (target, _, _) => ScriptValue.FromNumber(((WebGlContextState)target).TakeError()));
        t.DefineMethod("isContextLost", (target, _, _) => ScriptValue.FromBoolean(((WebGlContextState)target).Lost));
        t.DefineMethod("getExtension", (target, _, args) =>
            args.Length > 0 && string.Equals(WebIdlConversions.ToDOMString(args[0]), "WEBGL_lose_context", StringComparison.OrdinalIgnoreCase)
                ? _context.Engine.CreateHostObject(WebGlLoseContextTemplate, target)
                : ScriptValue.Null);
        return t;

        static uint ReadCanvasDimension(CanvasElementState canvas, string name, uint fallback) =>
            uint.TryParse(canvas.Element.GetAttribute(name), out var value) ? value : fallback;

        static ScriptValue StoreUniform(WebGlContextState state, ReadOnlySpan<ScriptValue> args, int count)
        {
            if (args.Length < count + 1) { state.SetError(0x0502); return ScriptValue.Undefined; }
            var location = WebIdlConversions.ToInt32(args[0]);
            var values = new double[count];
            for (var i = 0; i < count; i++) values[i] = WebIdlConversions.ToDouble(args[i + 1]);
            state.UniformValues[location] = values;
            return ScriptValue.Undefined;
        }
    }

    private HostObjectTemplate CreateNodeTemplate()
    {
        var t = new HostObjectTemplate("Node");

        // Constants
        t.DefineProperty("ELEMENT_NODE", (_, _) => ScriptValue.FromNumber(1));
        t.DefineProperty("ATTRIBUTE_NODE", (_, _) => ScriptValue.FromNumber(2));
        t.DefineProperty("TEXT_NODE", (_, _) => ScriptValue.FromNumber(3));
        t.DefineProperty("CDATA_SECTION_NODE", (_, _) => ScriptValue.FromNumber(4));
        t.DefineProperty("ENTITY_REFERENCE_NODE", (_, _) => ScriptValue.FromNumber(5));
        t.DefineProperty("ENTITY_NODE", (_, _) => ScriptValue.FromNumber(6));
        t.DefineProperty("PROCESSING_INSTRUCTION_NODE", (_, _) => ScriptValue.FromNumber(7));
        t.DefineProperty("COMMENT_NODE", (_, _) => ScriptValue.FromNumber(8));
        t.DefineProperty("DOCUMENT_NODE", (_, _) => ScriptValue.FromNumber(9));
        t.DefineProperty("DOCUMENT_TYPE_NODE", (_, _) => ScriptValue.FromNumber(10));
        t.DefineProperty("DOCUMENT_FRAGMENT_NODE", (_, _) => ScriptValue.FromNumber(11));
        t.DefineProperty("NOTATION_NODE", (_, _) => ScriptValue.FromNumber(12));

        t.DefineProperty("DOCUMENT_POSITION_DISCONNECTED", (_, _) => ScriptValue.FromNumber(0x01));
        t.DefineProperty("DOCUMENT_POSITION_PRECEDING", (_, _) => ScriptValue.FromNumber(0x02));
        t.DefineProperty("DOCUMENT_POSITION_FOLLOWING", (_, _) => ScriptValue.FromNumber(0x04));
        t.DefineProperty("DOCUMENT_POSITION_CONTAINS", (_, _) => ScriptValue.FromNumber(0x08));
        t.DefineProperty("DOCUMENT_POSITION_CONTAINED_BY", (_, _) => ScriptValue.FromNumber(0x10));
        t.DefineProperty("DOCUMENT_POSITION_IMPLEMENTATION_SPECIFIC", (_, _) => ScriptValue.FromNumber(0x20));

        // Node properties
        t.DefineProperty("nodeType", (target, _) =>
        {
            var node = (Node)target;
            var type = node switch
            {
                ElementNode => 1,
                Text => 3,
                Comment => 8,
                DocumentNode => 9,
                DocumentType => 10,
                DocumentFragment => 11,
                _ => 0,
            };
            return ScriptValue.FromNumber(type);
        });

        t.DefineProperty("nodeName", (target, _) =>
        {
            var node = (Node)target;
            var name = node switch
            {
                ElementNode el => el.Name.Namespace == Namespaces.Html ? el.LocalName.ToUpperInvariant() : el.LocalName,
                Text => "#text",
                Comment => "#comment",
                DocumentNode => "#document",
                DocumentType dt => dt.Name,
                DocumentFragment => "#document-fragment",
                _ => "",
            };
            return ScriptValue.FromString(name);
        });

        t.DefineProperty("nodeValue",
            (target, _) =>
            {
                var node = (Node)target;
                return node is CharacterData cd ? ScriptValue.FromString(cd.Data) : ScriptValue.Null;
            },
            (target, _, val) =>
            {
                var node = (Node)target;
                if (node is CharacterData cd && !val.IsNullOrUndefined)
                    cd.Data = WebIdlConversions.ToDOMString(val);
            });

        t.DefineProperty("ownerDocument", (target, _) =>
        {
            var node = (Node)target;
            return node is DocumentNode ? ScriptValue.Null : _context.Wrap(node.OwnerDocument);
        });

        t.DefineProperty("parentNode", (target, _) => _context.Wrap(((Node)target).Parent));
        t.DefineProperty("parentElement", (target, _) => _context.Wrap(((Node)target).Parent as ElementNode));

        t.DefineProperty("childNodes", (target, _) =>
        {
            var node = (Node)target;
            if (node is ContainerNode container)
                return _context.WrapNodeList(container);
            return _context.WrapNodeList(_context.Document.CreateFragment());
        });

        t.DefineProperty("firstChild", (target, _) =>
            _context.Wrap(((Node)target) is ContainerNode c ? c.FirstChild : null));

        t.DefineProperty("lastChild", (target, _) =>
            _context.Wrap(((Node)target) is ContainerNode c ? c.LastChild : null));

        t.DefineProperty("previousSibling", (target, _) => _context.Wrap(((Node)target).PreviousSibling));
        t.DefineProperty("nextSibling", (target, _) => _context.Wrap(((Node)target).NextSibling));

        t.DefineProperty("isConnected", (target, _) =>
        {
            for (Node? n = (Node)target; n is not null; n = n.Parent)
            {
                if (n is DocumentNode)
                    return ScriptValue.FromBoolean(true);
            }
            return ScriptValue.FromBoolean(false);
        });

        t.DefineProperty("textContent",
            (target, _) =>
            {
                var node = (Node)target;
                if (node is DocumentNode or DocumentType)
                    return ScriptValue.Null;
                if (node is CharacterData cd)
                    return ScriptValue.FromString(cd.Data);
                if (node is ContainerNode cn)
                    return ScriptValue.FromString(cn.TextContent ?? "");
                return ScriptValue.Null;
            },
            (target, _, val) =>
            {
                var node = (Node)target;
                if (node is DocumentNode or DocumentType)
                    return;

                var text = val.IsNullOrUndefined ? "" : WebIdlConversions.ToDOMString(val);
                if (node is CharacterData cd)
                {
                    cd.Data = text;
                    return;
                }

                if (node is ContainerNode container)
                {
                    while (container.FirstChild is { } child)
                        container.RemoveChild(child);

                    if (text.Length > 0)
                        container.AppendChild(container.OwnerDocument.CreateText(text));
                }
            });

        // Methods
        t.DefineMethod("hasChildNodes", (target, _, _) =>
            ScriptValue.FromBoolean(target is ContainerNode c && c.FirstChild is not null));

        t.DefineMethod("contains", (target, _, args) =>
        {
            if (args.Length == 0 || args[0].IsNullOrUndefined)
                return ScriptValue.FromBoolean(false);

            if (!_context.Engine.TryGetHostTarget(args[0], out var otherTarget) || otherTarget is not Node otherNode)
                return ScriptValue.FromBoolean(false);

            return ScriptValue.FromBoolean(((Node)target).IsInclusiveAncestorOf(otherNode));
        });

        t.DefineMethod("isSameNode", (target, _, args) =>
        {
            if (args.Length == 0 || args[0].IsNullOrUndefined)
                return ScriptValue.FromBoolean(false);

            if (!_context.Engine.TryGetHostTarget(args[0], out var otherTarget) || otherTarget is not Node otherNode)
                return ScriptValue.FromBoolean(false);

            return ScriptValue.FromBoolean(ReferenceEquals(target, otherNode));
        });

        t.DefineMethod("isEqualNode", (target, _, args) =>
        {
            if (args.Length == 0 || args[0].IsNullOrUndefined)
                return ScriptValue.FromBoolean(false);

            if (!_context.Engine.TryGetHostTarget(args[0], out var otherTarget) || otherTarget is not Node otherNode)
                return ScriptValue.FromBoolean(false);

            return ScriptValue.FromBoolean(IsEqualNode((Node)target, otherNode));
        });

        t.DefineMethod("compareDocumentPosition", (target, _, args) =>
        {
            if (args.Length == 0 || args[0].IsNullOrUndefined)
                throw WebIdlException.TypeError("Failed to execute 'compareDocumentPosition' on 'Node': 1 argument required.");

            if (!_context.Engine.TryGetHostTarget(args[0], out var otherTarget) || otherTarget is not Node otherNode)
                throw WebIdlException.TypeError("Failed to execute 'compareDocumentPosition' on 'Node': parameter 1 is not of type 'Node'.");

            return ScriptValue.FromNumber(CompareDocumentPosition((Node)target, otherNode));
        });

        t.DefineMethod("cloneNode", (target, _, args) =>
        {
            var deep = args.Length > 0 && WebIdlConversions.ToBoolean(args[0]);
            var node = (Node)target;
            if (node is DocumentNode)
                throw WebIdlException.DomException(DomExceptionNames.NotSupportedError, "Cannot clone a Document.");

            var clone = node.Clone(deep);
            return _context.Wrap(clone);
        });

        t.DefineMethod("normalize", (target, _, _) =>
        {
            if (target is ContainerNode container)
                NormalizeContainer(container);
            return ScriptValue.Undefined;
        });

        t.DefineMethod("appendChild", (target, _, args) =>
        {
            if (args.Length == 0)
                throw WebIdlException.TypeError("Failed to execute 'appendChild' on 'Node': 1 argument required.");
            if (target is not ContainerNode container)
                throw WebIdlException.DomException(DomExceptionNames.HierarchyRequestError, "Node cannot have children.");

            return DomTreeMutation.AppendChild(_context, container, args[0]);
        });

        t.DefineMethod("insertBefore", (target, _, args) =>
        {
            if (args.Length < 2)
                throw WebIdlException.TypeError("Failed to execute 'insertBefore' on 'Node': 2 arguments required.");
            if (target is not ContainerNode container)
                throw WebIdlException.DomException(DomExceptionNames.HierarchyRequestError, "Node cannot have children.");

            return DomTreeMutation.InsertBefore(_context, container, args[0], args[1]);
        });

        t.DefineMethod("removeChild", (target, _, args) =>
        {
            if (args.Length == 0)
                throw WebIdlException.TypeError("Failed to execute 'removeChild' on 'Node': 1 argument required.");
            if (target is not ContainerNode container)
                throw WebIdlException.DomException(DomExceptionNames.NotFoundError, "Node has no children.");

            return DomTreeMutation.RemoveChild(_context, container, args[0]);
        });

        t.DefineMethod("replaceChild", (target, _, args) =>
        {
            if (args.Length < 2)
                throw WebIdlException.TypeError("Failed to execute 'replaceChild' on 'Node': 2 arguments required.");
            if (target is not ContainerNode container)
                throw WebIdlException.DomException(DomExceptionNames.HierarchyRequestError, "Node cannot have children.");

            return DomTreeMutation.ReplaceChild(_context, container, args[0], args[1]);
        });

        return t;
    }

    private HostObjectTemplate CreateElementTemplate(IHostObjectTemplate nodeProto)
    {
        var t = new HostObjectTemplate("Element", nodeProto);

        t.DefineProperty("tagName", (target, _) =>
        {
            var el = (ElementNode)target;
            return ScriptValue.FromString(el.Name.Namespace == Namespaces.Html ? el.LocalName.ToUpperInvariant() : el.LocalName);
        });

        t.DefineProperty("localName", (target, _) => ScriptValue.FromString(((ElementNode)target).LocalName));
        t.DefineProperty("namespaceURI", (target, _) =>
        {
            var uri = ((ElementNode)target).NamespaceUri;
            return string.IsNullOrEmpty(uri) ? ScriptValue.Null : ScriptValue.FromString(uri);
        });

        t.DefineProperty("id",
            (target, _) => ScriptValue.FromString(((ElementNode)target).GetAttribute("id") ?? ""),
            (target, _, val) => ((ElementNode)target).SetAttribute("id", WebIdlConversions.ToDOMString(val)));

        t.DefineProperty("className",
            (target, _) => ScriptValue.FromString(((ElementNode)target).GetAttribute("class") ?? ""),
            (target, _, val) => ((ElementNode)target).SetAttribute("class", WebIdlConversions.ToDOMString(val)));

        t.DefineProperty("classList", (target, _) => _context.WrapClassList((ElementNode)target));

        t.DefineProperty("attributes", (target, _) => _context.WrapAttributes((ElementNode)target));
        t.DefineProperty("style", (target, _) => _context.WrapStyle((ElementNode)target));
        t.DefineProperty("open", (target, _) => ScriptValue.FromBoolean(((ElementNode)target).GetAttribute("open") is not null),
            (target, _, value) => { if (WebIdlConversions.ToBoolean(value)) ((ElementNode)target).SetAttribute("open", ""); else ((ElementNode)target).RemoveAttribute("open"); });
        t.DefineMethod("focus", (target, _, _) => { _context.Focus((ElementNode)target); return ScriptValue.Undefined; });
        t.DefineMethod("blur", (target, _, _) => { _context.Blur((ElementNode)target); return ScriptValue.Undefined; });
        t.DefineMethod("getBoundingClientRect", (target, _, _) => _context.WrapRect(DomGeometry.Rect((ElementNode)target, _context.Layout)));
        t.DefineMethod("getClientRects", (target, _, _) => _context.Engine.CreateArray([_context.WrapRect(DomGeometry.Rect((ElementNode)target))]));
        t.DefineProperty("offsetWidth", (target, _) => ScriptValue.FromNumber(DomGeometry.Rect((ElementNode)target).Width));
        t.DefineProperty("offsetHeight", (target, _) => ScriptValue.FromNumber(DomGeometry.Rect((ElementNode)target).Height));
        t.DefineProperty("clientWidth", (target, _) => ScriptValue.FromNumber(DomGeometry.Rect((ElementNode)target).Width));
        t.DefineProperty("clientHeight", (target, _) => ScriptValue.FromNumber(DomGeometry.Rect((ElementNode)target).Height));
        t.DefineProperty("scrollLeft", (target, _) => ScriptValue.FromNumber(_context.GetScroll((ElementNode)target).X),
            (target, _, value) => _context.SetScroll((ElementNode)target, WebIdlConversions.ToDouble(value), _context.GetScroll((ElementNode)target).Y));
        t.DefineProperty("scrollTop", (target, _) => ScriptValue.FromNumber(_context.GetScroll((ElementNode)target).Y),
            (target, _, value) => _context.SetScroll((ElementNode)target, _context.GetScroll((ElementNode)target).X, WebIdlConversions.ToDouble(value)));
        t.DefineProperty("scrollWidth", (target, _) => ScriptValue.FromNumber(Math.Max(DomGeometry.Rect((ElementNode)target).Width, DomGeometry.Rect((ElementNode)target).Width + 0)));
        t.DefineProperty("scrollHeight", (target, _) => ScriptValue.FromNumber(Math.Max(DomGeometry.Rect((ElementNode)target).Height, DomGeometry.Rect((ElementNode)target).Height + 0)));
        t.DefineMethod("scroll", (target, _, args) =>
        {
            var element = (ElementNode)target;
            var current = _context.GetScroll(element);
            var x = current.X;
            var y = current.Y;
            if (args.Length > 0) x = WebIdlConversions.ToDouble(args[0]);
            if (args.Length > 1) y = WebIdlConversions.ToDouble(args[1]);
            _context.SetScroll(element, x, y);
            return ScriptValue.Undefined;
        });
        t.DefineMethod("scrollTo", (target, _, args) =>
        {
            var element = (ElementNode)target;
            var x = args.Length > 0 ? WebIdlConversions.ToDouble(args[0]) : 0;
            var y = args.Length > 1 ? WebIdlConversions.ToDouble(args[1]) : 0;
            _context.SetScroll(element, x, y);
            return ScriptValue.Undefined;
        });
        t.DefineMethod("scrollBy", (target, _, args) =>
        {
            var element = (ElementNode)target;
            var current = _context.GetScroll(element);
            var x = args.Length > 0 ? WebIdlConversions.ToDouble(args[0]) : 0;
            var y = args.Length > 1 ? WebIdlConversions.ToDouble(args[1]) : 0;
            _context.SetScroll(element, current.X + x, current.Y + y);
            return ScriptValue.Undefined;
        });
        t.DefineProperty("innerText",
            (target, _) => ScriptValue.FromString(DomText.InnerText((ElementNode)target)),
            (target, _, value) =>
            {
                var element = (ElementNode)target;
                while (element.FirstChild is { } child)
                    element.RemoveChild(child);
                var text = WebIdlConversions.ToDOMString(value);
                if (text.Length > 0)
                    element.AppendChild(element.OwnerDocument.CreateText(text));
            });
        t.DefineProperty("outerText",
            (target, _) => ScriptValue.FromString(DomText.InnerText((ElementNode)target)),
            (target, _, value) =>
            {
                var element = (ElementNode)target;
                while (element.FirstChild is { } child)
                    element.RemoveChild(child);
                var text = WebIdlConversions.ToDOMString(value);
                if (text.Length > 0)
                    element.AppendChild(element.OwnerDocument.CreateText(text));
            });

        // ParentNode members
        t.DefineProperty("children", (target, _) => _context.WrapHtmlCollection((ElementNode)target));
        t.DefineProperty("elements", (target, _) => _context.WrapHtmlCollection((ElementNode)target));
        t.DefineMethod("checkValidity", (target, _, _) =>
        {
            var valid = ((ElementNode)target).Descendants.OfType<ElementNode>().Where(e => e.LocalName is "input" or "textarea" or "select").All(control =>
            {
                var value = control.Value ?? control.GetAttribute("value") ?? "";
                return control.GetAttribute("disabled") is not null || control.GetAttribute("required") is null || value.Length > 0;
            });
            return ScriptValue.FromBoolean(valid);
        });
        t.DefineMethod("reportValidity", (target, thisValue, args) =>
        {
            return ScriptValue.FromBoolean(((ElementNode)target).Descendants.OfType<ElementNode>().Where(e => e.LocalName is "input" or "textarea" or "select").All(control => control.GetAttribute("required") is null || (control.Value ?? control.GetAttribute("value") ?? "").Length > 0));
        });
        t.DefineMethod("reset", (target, _, _) =>
        {
            foreach (var control in ((ElementNode)target).Descendants.OfType<ElementNode>())
                if (control.LocalName == "input") control.Value = control.GetAttribute("value") ?? "";
            return ScriptValue.Undefined;
        });
        t.DefineMethod("submit", (_, _, _) => ScriptValue.Undefined);
        t.DefineMethod("requestSubmit", (_, _, _) => ScriptValue.Undefined);
        t.DefineProperty("firstElementChild", (target, _) =>
            _context.Wrap(((ElementNode)target).Children.OfType<ElementNode>().FirstOrDefault()));
        t.DefineProperty("lastElementChild", (target, _) =>
            _context.Wrap(((ElementNode)target).Children.OfType<ElementNode>().LastOrDefault()));
        t.DefineProperty("childElementCount", (target, _) =>
            ScriptValue.FromNumber(((ElementNode)target).Children.OfType<ElementNode>().Count()));

        // NonDocumentTypeChildNode members
        t.DefineProperty("previousElementSibling", (target, _) =>
        {
            for (var node = ((ElementNode)target).PreviousSibling; node is not null; node = node.PreviousSibling)
            {
                if (node is ElementNode el)
                    return _context.Wrap(el);
            }
            return ScriptValue.Null;
        });

        t.DefineProperty("nextElementSibling", (target, _) =>
        {
            for (var node = ((ElementNode)target).NextSibling; node is not null; node = node.NextSibling)
            {
                if (node is ElementNode el)
                    return _context.Wrap(el);
            }
            return ScriptValue.Null;
        });

        // Attributes methods
        t.DefineMethod("getAttribute", (target, _, args) =>
        {
            if (args.Length == 0)
                throw WebIdlException.TypeError("getAttribute requires 1 argument.");
            var name = WebIdlConversions.ToDOMString(args[0]);
            var val = ((ElementNode)target).GetAttribute(name);
            return val is null ? ScriptValue.Null : ScriptValue.FromString(val);
        });

        t.DefineMethod("setAttribute", (target, _, args) =>
        {
            if (args.Length < 2)
                throw WebIdlException.TypeError("setAttribute requires 2 arguments.");
            var name = WebIdlConversions.ToDOMString(args[0]);
            var val = WebIdlConversions.ToDOMString(args[1]);
            var element = (ElementNode)target;
            var oldValue = element.GetAttribute(name);
            element.SetAttribute(name, val);
            _context.NotifyAttributeMutation(element, name, oldValue);
            return ScriptValue.Undefined;
        });

        t.DefineMethod("hasAttribute", (target, _, args) =>
        {
            if (args.Length == 0)
                return ScriptValue.FromBoolean(false);
            var name = WebIdlConversions.ToDOMString(args[0]);
            return ScriptValue.FromBoolean(((ElementNode)target).GetAttribute(name) is not null);
        });

        t.DefineMethod("removeAttribute", (target, _, args) =>
        {
            if (args.Length > 0)
            {
                var name = WebIdlConversions.ToDOMString(args[0]);
                var element = (ElementNode)target;
                var oldValue = element.GetAttribute(name);
                element.RemoveAttribute(name);
                if (oldValue is not null)
                    _context.NotifyAttributeMutation(element, name, oldValue);
            }
            return ScriptValue.Undefined;
        });

        t.DefineMethod("toggleAttribute", (target, _, args) =>
        {
            if (args.Length == 0)
                throw WebIdlException.TypeError("Failed to execute 'toggleAttribute' on 'Element': 1 argument required.");

            var el = (ElementNode)target;
            var name = WebIdlConversions.ToDOMString(args[0]);
            var has = el.GetAttribute(name) is not null;

            bool shouldHave;
            if (args.Length > 1)
            {
                shouldHave = WebIdlConversions.ToBoolean(args[1]);
            }
            else
            {
                shouldHave = !has;
            }

            if (shouldHave)
            {
                if (!has)
                {
                    el.SetAttribute(name, "");
                    _context.NotifyAttributeMutation(el, name, null);
                }
                return ScriptValue.FromBoolean(true);
            }
            else
            {
                if (has)
                {
                    el.RemoveAttribute(name);
                    _context.NotifyAttributeMutation(el, name, "");
                }
                return ScriptValue.FromBoolean(false);
            }
        });

        t.DefineMethod("getAttributeNames", (target, _, _) =>
        {
            var el = (ElementNode)target;
            var attrs = el.Attributes;
            var names = new ScriptValue[attrs.Length];
            for (var i = 0; i < attrs.Length; i++)
            {
                names[i] = ScriptValue.FromString(el.OwnerDocument.TextOf(attrs[i].Name));
            }
            return _context.Engine.CreateArray(names);
        });

        t.DefineMethod("hasAttributes", (target, _, _) =>
        {
            var el = (ElementNode)target;
            return ScriptValue.FromBoolean(el.Attributes.Length > 0);
        });

        t.DefineMethod("getAttributeNode", (target, _, args) =>
        {
            if (args.Length == 0)
                throw WebIdlException.TypeError("Failed to execute 'getAttributeNode' on 'Element': 1 argument required.");

            var el = (ElementNode)target;
            var name = WebIdlConversions.ToDOMString(args[0]);
            if (el.GetAttribute(name) is null)
                return ScriptValue.Null;

            return _context.WrapAttr(el, name);
        });

        t.DefineMethod("setAttributeNode", (target, _, args) =>
        {
            if (args.Length == 0)
                throw WebIdlException.TypeError("Failed to execute 'setAttributeNode' on 'Element': 1 argument required.");

            if (!_context.Engine.TryGetHostTarget(args[0], out var hostTarget) || hostTarget is not LiveAttr attr)
                throw WebIdlException.TypeError("parameter 1 is not of type 'Attr'.");

            var el = (ElementNode)target;
            if (attr.Element is not null && !ReferenceEquals(attr.Element, el))
                throw WebIdlException.DomException(DomExceptionNames.InUseAttributeError, "The attribute is already in use by another element.");

            LiveAttr? oldAttr = null;
            if (el.GetAttribute(attr.Name) is not null)
            {
                var wrappedOld = _context.WrapAttr(el, attr.Name);
                if (_context.Engine.TryGetHostTarget(wrappedOld, out var oldTarget) && oldTarget is LiveAttr la)
                    oldAttr = la;
            }

            el.SetAttribute(attr.Name, attr.Value);
            attr.Element = el;

            return oldAttr is not null ? _context.WrapAttr(oldAttr.Element!, oldAttr.Name) : ScriptValue.Null;
        });

        t.DefineMethod("removeAttributeNode", (target, _, args) =>
        {
            if (args.Length == 0)
                throw WebIdlException.TypeError("Failed to execute 'removeAttributeNode' on 'Element': 1 argument required.");

            if (!_context.Engine.TryGetHostTarget(args[0], out var hostTarget) || hostTarget is not LiveAttr attr)
                throw WebIdlException.TypeError("parameter 1 is not of type 'Attr'.");

            var el = (ElementNode)target;
            if (!ReferenceEquals(attr.Element, el) || el.GetAttribute(attr.Name) is null)
                throw WebIdlException.DomException(DomExceptionNames.NotFoundError, "The attribute could not be found.");

            var val = el.GetAttribute(attr.Name) ?? attr.Value;
            el.RemoveAttribute(attr.Name);
            attr.Detach(val);
            return args[0];
        });

        // ParentNode mutation methods
        t.DefineMethod("append", (target, _, args) =>
        {
            DomTreeMutation.Append(_context, (ElementNode)target, args);
            return ScriptValue.Undefined;
        });

        t.DefineMethod("prepend", (target, _, args) =>
        {
            DomTreeMutation.Prepend(_context, (ElementNode)target, args);
            return ScriptValue.Undefined;
        });

        t.DefineMethod("replaceChildren", (target, _, args) =>
        {
            DomTreeMutation.ReplaceChildren(_context, (ElementNode)target, args);
            return ScriptValue.Undefined;
        });

        // ChildNode mutation methods
        t.DefineMethod("before", (target, _, args) =>
        {
            DomTreeMutation.Before(_context, (ElementNode)target, args);
            return ScriptValue.Undefined;
        });

        t.DefineMethod("after", (target, _, args) =>
        {
            DomTreeMutation.After(_context, (ElementNode)target, args);
            return ScriptValue.Undefined;
        });

        t.DefineMethod("replaceWith", (target, _, args) =>
        {
            DomTreeMutation.ReplaceWith(_context, (ElementNode)target, args);
            return ScriptValue.Undefined;
        });

        t.DefineMethod("remove", (target, _, _) =>
        {
            DomTreeMutation.Remove((ElementNode)target);
            return ScriptValue.Undefined;
        });

        // Selectors and queries (ParentNode + Element)
        t.DefineMethod("querySelector", (target, _, args) =>
        {
            if (args.Length == 0)
                throw WebIdlException.TypeError("querySelector requires 1 argument.");
            var el = DomSelectors.QuerySelector((ElementNode)target, args[0]);
            return _context.Wrap(el);
        });

        t.DefineMethod("querySelectorAll", (target, _, args) =>
        {
            if (args.Length == 0)
                throw WebIdlException.TypeError("querySelectorAll requires 1 argument.");
            var list = DomSelectors.QuerySelectorAll((ElementNode)target, args[0]);
            return _context.WrapStaticNodeList(list);
        });

        t.DefineMethod("matches", (target, _, args) =>
        {
            if (args.Length == 0)
                throw WebIdlException.TypeError("matches requires 1 argument.");
            return ScriptValue.FromBoolean(DomSelectors.Matches((ElementNode)target, args[0]));
        });

        t.DefineMethod("closest", (target, _, args) =>
        {
            if (args.Length == 0)
                throw WebIdlException.TypeError("closest requires 1 argument.");
            var el = DomSelectors.Closest((ElementNode)target, args[0]);
            return _context.Wrap(el);
        });

        // HTML fragment serialization & parsing (Element traversal & insertion)
        t.DefineProperty("innerHTML",
            (target, _) => ScriptValue.FromString(DomHtml.SerializeInnerHtml((ElementNode)target)),
            (target, _, val) => DomHtml.SetInnerHtml((ElementNode)target, WebIdlConversions.ToDOMString(val)));

        t.DefineProperty("outerHTML",
            (target, _) => ScriptValue.FromString(DomHtml.SerializeOuterHtml((ElementNode)target)),
            (target, _, val) => DomHtml.SetOuterHtml((ElementNode)target, WebIdlConversions.ToDOMString(val)));

        t.DefineMethod("insertAdjacentHTML", (target, _, args) =>
        {
            if (args.Length < 2)
                throw WebIdlException.TypeError("Failed to execute 'insertAdjacentHTML' on 'Element': 2 arguments required.");
            var position = WebIdlConversions.ToDOMString(args[0]);
            var text = WebIdlConversions.ToDOMString(args[1]);
            DomHtml.InsertAdjacentHtml((ElementNode)target, position, text);
            return ScriptValue.Undefined;
        });

        return t;
    }

    private HostObjectTemplate CreateStyleDeclarationTemplate()
    {
        var t = new HostObjectTemplate("CSSStyleDeclaration");
        t.DefineProperty("cssText",
            (target, _) => ScriptValue.FromString(((LiveStyleDeclaration)target).CssText),
            (target, _, value) => ((LiveStyleDeclaration)target).Replace(WebIdlConversions.ToDOMString(value)));
        t.DefineProperty("length", (target, _) => ScriptValue.FromNumber(((LiveStyleDeclaration)target).Entries().Count));
        t.DefineIndexedProperty((target, _, index) =>
        {
            var entries = ((LiveStyleDeclaration)target).Entries();
            return index >= 0 && index < entries.Count ? ScriptValue.FromString(entries[index].Name) : ScriptValue.FromString("");
        });
        t.DefineNamedProperty((target, _, name) =>
        {
            var normalized = LiveStyleDeclaration.NormalizeName(name);
            var value = ((LiveStyleDeclaration)target).GetValue(normalized);
            return value.Length == 0 ? ScriptValue.Undefined : ScriptValue.FromString(value);
        },
        (target, _, name, value) => ((LiveStyleDeclaration)target).Set(name, WebIdlConversions.ToDOMString(value), ""),
        enumerator: (target, _) => ((LiveStyleDeclaration)target).Entries().Select(e => e.Name));
        t.DefineMethod("item", (target, _, args) =>
        {
            var index = args.Length == 0 ? -1 : WebIdlConversions.ToInt32(args[0]);
            var entries = ((LiveStyleDeclaration)target).Entries();
            return index >= 0 && index < entries.Count ? ScriptValue.FromString(entries[index].Name) : ScriptValue.FromString("");
        });
        t.DefineMethod("getPropertyValue", (target, _, args) =>
        {
            var name = args.Length == 0 ? "" : WebIdlConversions.ToDOMString(args[0]);
            return ScriptValue.FromString(((LiveStyleDeclaration)target).GetValue(name));
        });
        t.DefineMethod("getPropertyPriority", (target, _, args) =>
        {
            var name = args.Length == 0 ? "" : WebIdlConversions.ToDOMString(args[0]);
            var entry = ((LiveStyleDeclaration)target).Entries().FirstOrDefault(e => e.Name.Equals(LiveStyleDeclaration.NormalizeName(name), StringComparison.OrdinalIgnoreCase));
            return entry.Name is null || !entry.Important ? ScriptValue.FromString("") : ScriptValue.FromString("important");
        });
        t.DefineMethod("setProperty", (target, _, args) =>
        {
            if (args.Length == 0)
                throw WebIdlException.TypeError("setProperty requires 1 argument.");
            var name = WebIdlConversions.ToDOMString(args[0]);
            var value = args.Length > 1 ? WebIdlConversions.ToDOMString(args[1]) : "";
            var priority = args.Length > 2 ? WebIdlConversions.ToDOMString(args[2]) : "";
            ((LiveStyleDeclaration)target).Set(name, value, priority);
            return ScriptValue.Undefined;
        });
        t.DefineMethod("removeProperty", (target, _, args) =>
        {
            var name = args.Length == 0 ? "" : WebIdlConversions.ToDOMString(args[0]);
            return ScriptValue.FromString(((LiveStyleDeclaration)target).Remove(name));
        });
        return t;
    }

    private HostObjectTemplate CreateComputedStyleDeclarationTemplate()
    {
        var t = new HostObjectTemplate("CSSStyleDeclaration");
        t.DefineProperty("length", (target, _) => ScriptValue.FromNumber(((LiveComputedStyle)target).Properties.Count));
        t.DefineIndexedProperty((target, _, index) =>
        {
            var props = ((LiveComputedStyle)target).Properties;
            return index >= 0 && index < props.Count ? ScriptValue.FromString(props[index].Name) : ScriptValue.FromString("");
        });
        t.DefineNamedProperty((target, _, name) =>
        {
            var value = ((LiveComputedStyle)target).Get(name);
            return value.Length == 0 ? ScriptValue.Undefined : ScriptValue.FromString(value);
        }, enumerator: (target, _) => ((LiveComputedStyle)target).Properties.Select(p => p.Name));
        t.DefineMethod("item", (target, _, args) =>
        {
            var index = args.Length == 0 ? -1 : WebIdlConversions.ToInt32(args[0]);
            var props = ((LiveComputedStyle)target).Properties;
            return index >= 0 && index < props.Count ? ScriptValue.FromString(props[index].Name) : ScriptValue.FromString("");
        });
        t.DefineMethod("getPropertyValue", (target, _, args) =>
        {
            var name = args.Length == 0 ? "" : WebIdlConversions.ToDOMString(args[0]);
            return ScriptValue.FromString(((LiveComputedStyle)target).Get(name));
        });
        t.DefineMethod("getPropertyPriority", (_, _, _) => ScriptValue.FromString(""));
        return t;
    }

    private HostObjectTemplate CreateDomRectTemplate()
    {
        var t = new HostObjectTemplate("DOMRect");
        foreach (var name in new[] { "x", "y", "top", "right", "bottom", "left", "width", "height" })
            t.DefineProperty(name, (target, _) => ScriptValue.FromNumber(name switch
            {
                "x" => ((DomRect)target).X, "y" => ((DomRect)target).Y, "top" => ((DomRect)target).Top,
                "right" => ((DomRect)target).Right, "bottom" => ((DomRect)target).Bottom, "left" => ((DomRect)target).Left,
                "width" => ((DomRect)target).Width, _ => ((DomRect)target).Height,
            }));
        return t;
    }

    private HostObjectTemplate CreateHtmlElementTemplate(IHostObjectTemplate elementProto)
    {
        var t = new HostObjectTemplate("HTMLElement", elementProto);

        t.DefineProperty("dataset", (target, _) => _context.WrapDataset((ElementNode)target));

        t.DefineProperty("title",
            (target, _) => ScriptValue.FromString(((ElementNode)target).GetAttribute("title") ?? ""),
            (target, _, val) => ((ElementNode)target).SetAttribute("title", WebIdlConversions.ToDOMString(val)));

        t.DefineProperty("lang",
            (target, _) => ScriptValue.FromString(((ElementNode)target).GetAttribute("lang") ?? ""),
            (target, _, val) => ((ElementNode)target).SetAttribute("lang", WebIdlConversions.ToDOMString(val)));

        t.DefineProperty("dir",
            (target, _) => ScriptValue.FromString(((ElementNode)target).GetAttribute("dir") ?? ""),
            (target, _, val) => ((ElementNode)target).SetAttribute("dir", WebIdlConversions.ToDOMString(val)));

        t.DefineProperty("hidden",
            (target, _) => ScriptValue.FromBoolean(((ElementNode)target).GetAttribute("hidden") is not null),
            (target, _, val) =>
            {
                if (WebIdlConversions.ToBoolean(val))
                    ((ElementNode)target).SetAttribute("hidden", "");
                else
                    ((ElementNode)target).RemoveAttribute("hidden");
            });

        t.DefineProperty("tabIndex",
            (target, _) =>
            {
                var el = (ElementNode)target;
                var val = el.GetAttribute("tabindex");
                if (val is not null && int.TryParse(val, out var parsed))
                    return ScriptValue.FromNumber(parsed);
                return ScriptValue.FromNumber(-1);
            },
            (target, _, val) =>
            {
                var num = WebIdlConversions.ToInt32(val);
                ((ElementNode)target).SetAttribute("tabindex", num.ToString());
            });

        return t;
    }

    private static HostObjectTemplate CreateSvgElementTemplate(IHostObjectTemplate elementProto)
    {
        return new HostObjectTemplate("SVGElement", elementProto);
    }

    private HostObjectTemplate CreateCharacterDataTemplate(IHostObjectTemplate nodeProto)
    {
        var t = new HostObjectTemplate("CharacterData", nodeProto);

        t.DefineProperty("data",
            (target, _) => ScriptValue.FromString(((CharacterData)target).Data),
            (target, _, val) => ((CharacterData)target).Data = WebIdlConversions.ToDOMString(val));

        t.DefineProperty("length", (target, _) =>
            ScriptValue.FromNumber(((CharacterData)target).Data.Length));

        t.DefineProperty("previousElementSibling", (target, _) =>
        {
            for (var node = ((CharacterData)target).PreviousSibling; node is not null; node = node.PreviousSibling)
            {
                if (node is ElementNode el)
                    return _context.Wrap(el);
            }
            return ScriptValue.Null;
        });

        t.DefineProperty("nextElementSibling", (target, _) =>
        {
            for (var node = ((CharacterData)target).NextSibling; node is not null; node = node.NextSibling)
            {
                if (node is ElementNode el)
                    return _context.Wrap(el);
            }
            return ScriptValue.Null;
        });

        t.DefineMethod("substringData", (target, _, args) =>
        {
            if (args.Length < 2)
                throw WebIdlException.TypeError("substringData requires 2 arguments.");

            var cd = (CharacterData)target;
            var offset = WebIdlConversions.ToInt32(args[0]);
            var count = WebIdlConversions.ToInt32(args[1]);

            if (offset < 0 || offset > cd.Data.Length || count < 0)
                throw WebIdlException.DomException(DomExceptionNames.IndexSizeError, "Index out of bounds.");

            var len = Math.Min(count, cd.Data.Length - offset);
            return ScriptValue.FromString(cd.Data.Substring(offset, len));
        });

        t.DefineMethod("appendData", (target, _, args) =>
        {
            if (args.Length > 0)
            {
                var cd = (CharacterData)target;
                cd.Data += WebIdlConversions.ToDOMString(args[0]);
            }
            return ScriptValue.Undefined;
        });

        t.DefineMethod("insertData", (target, _, args) =>
        {
            if (args.Length < 2)
                throw WebIdlException.TypeError("insertData requires 2 arguments.");

            var cd = (CharacterData)target;
            var offset = WebIdlConversions.ToInt32(args[0]);
            var data = WebIdlConversions.ToDOMString(args[1]);

            if (offset < 0 || offset > cd.Data.Length)
                throw WebIdlException.DomException(DomExceptionNames.IndexSizeError, "Index out of bounds.");

            cd.Data = cd.Data.Insert(offset, data);
            return ScriptValue.Undefined;
        });

        t.DefineMethod("deleteData", (target, _, args) =>
        {
            if (args.Length < 2)
                throw WebIdlException.TypeError("deleteData requires 2 arguments.");

            var cd = (CharacterData)target;
            var offset = WebIdlConversions.ToInt32(args[0]);
            var count = WebIdlConversions.ToInt32(args[1]);

            if (offset < 0 || offset > cd.Data.Length || count < 0)
                throw WebIdlException.DomException(DomExceptionNames.IndexSizeError, "Index out of bounds.");

            var len = Math.Min(count, cd.Data.Length - offset);
            cd.Data = cd.Data.Remove(offset, len);
            return ScriptValue.Undefined;
        });

        t.DefineMethod("replaceData", (target, _, args) =>
        {
            if (args.Length < 3)
                throw WebIdlException.TypeError("replaceData requires 3 arguments.");

            var cd = (CharacterData)target;
            var offset = WebIdlConversions.ToInt32(args[0]);
            var count = WebIdlConversions.ToInt32(args[1]);
            var data = WebIdlConversions.ToDOMString(args[2]);

            if (offset < 0 || offset > cd.Data.Length || count < 0)
                throw WebIdlException.DomException(DomExceptionNames.IndexSizeError, "Index out of bounds.");

            var len = Math.Min(count, cd.Data.Length - offset);
            cd.Data = cd.Data.Remove(offset, len).Insert(offset, data);
            return ScriptValue.Undefined;
        });

        // ChildNode mutation
        t.DefineMethod("before", (target, _, args) =>
        {
            DomTreeMutation.Before(_context, (CharacterData)target, args);
            return ScriptValue.Undefined;
        });

        t.DefineMethod("after", (target, _, args) =>
        {
            DomTreeMutation.After(_context, (CharacterData)target, args);
            return ScriptValue.Undefined;
        });

        t.DefineMethod("replaceWith", (target, _, args) =>
        {
            DomTreeMutation.ReplaceWith(_context, (CharacterData)target, args);
            return ScriptValue.Undefined;
        });

        t.DefineMethod("remove", (target, _, _) =>
        {
            DomTreeMutation.Remove((CharacterData)target);
            return ScriptValue.Undefined;
        });

        return t;
    }

    private HostObjectTemplate CreateTextTemplate(IHostObjectTemplate charDataProto)
    {
        var t = new HostObjectTemplate("Text", charDataProto);

        t.DefineMethod("splitText", (target, _, args) =>
        {
            if (args.Length == 0)
                throw WebIdlException.TypeError("splitText requires 1 argument.");

            var text = (Text)target;
            var offset = WebIdlConversions.ToInt32(args[0]);

            if (offset < 0 || offset > text.Data.Length)
                throw WebIdlException.DomException(DomExceptionNames.IndexSizeError, "Index out of bounds.");

            var remainder = text.Data[offset..];
            text.Data = text.Data[..offset];

            var newText = text.OwnerDocument.CreateText(remainder);
            if (text.Parent is { } parent)
                parent.InsertBefore(newText, text.NextSibling);

            return _context.Wrap(newText);
        });

        return t;
    }

    private static HostObjectTemplate CreateCommentTemplate(IHostObjectTemplate charDataProto)
    {
        return new HostObjectTemplate("Comment", charDataProto);
    }

    private HostObjectTemplate CreateDocumentTypeTemplate(IHostObjectTemplate nodeProto)
    {
        var t = new HostObjectTemplate("DocumentType", nodeProto);

        t.DefineProperty("name", (target, _) => ScriptValue.FromString(((DocumentType)target).Name));
        t.DefineProperty("publicId", (target, _) => ScriptValue.FromString(((DocumentType)target).PublicId));
        t.DefineProperty("systemId", (target, _) => ScriptValue.FromString(((DocumentType)target).SystemId));

        t.DefineMethod("before", (target, _, args) =>
        {
            DomTreeMutation.Before(_context, (DocumentType)target, args);
            return ScriptValue.Undefined;
        });

        t.DefineMethod("after", (target, _, args) =>
        {
            DomTreeMutation.After(_context, (DocumentType)target, args);
            return ScriptValue.Undefined;
        });

        t.DefineMethod("replaceWith", (target, _, args) =>
        {
            DomTreeMutation.ReplaceWith(_context, (DocumentType)target, args);
            return ScriptValue.Undefined;
        });

        t.DefineMethod("remove", (target, _, _) =>
        {
            DomTreeMutation.Remove((DocumentType)target);
            return ScriptValue.Undefined;
        });

        return t;
    }

    private HostObjectTemplate CreateDocumentFragmentTemplate(IHostObjectTemplate nodeProto)
    {
        var t = new HostObjectTemplate("DocumentFragment", nodeProto);

        t.DefineProperty("children", (target, _) => _context.WrapHtmlCollection((DocumentFragment)target));
        t.DefineProperty("firstElementChild", (target, _) =>
            _context.Wrap(((DocumentFragment)target).Children.OfType<ElementNode>().FirstOrDefault()));
        t.DefineProperty("lastElementChild", (target, _) =>
            _context.Wrap(((DocumentFragment)target).Children.OfType<ElementNode>().LastOrDefault()));
        t.DefineProperty("childElementCount", (target, _) =>
            ScriptValue.FromNumber(((DocumentFragment)target).Children.OfType<ElementNode>().Count()));

        t.DefineMethod("append", (target, _, args) =>
        {
            DomTreeMutation.Append(_context, (DocumentFragment)target, args);
            return ScriptValue.Undefined;
        });

        t.DefineMethod("prepend", (target, _, args) =>
        {
            DomTreeMutation.Prepend(_context, (DocumentFragment)target, args);
            return ScriptValue.Undefined;
        });

        t.DefineMethod("replaceChildren", (target, _, args) =>
        {
            DomTreeMutation.ReplaceChildren(_context, (DocumentFragment)target, args);
            return ScriptValue.Undefined;
        });

        // ParentNode query methods
        t.DefineMethod("querySelector", (target, _, args) =>
        {
            if (args.Length == 0)
                throw WebIdlException.TypeError("querySelector requires 1 argument.");
            var el = DomSelectors.QuerySelector((DocumentFragment)target, args[0]);
            return _context.Wrap(el);
        });

        t.DefineMethod("querySelectorAll", (target, _, args) =>
        {
            if (args.Length == 0)
                throw WebIdlException.TypeError("querySelectorAll requires 1 argument.");
            var list = DomSelectors.QuerySelectorAll((DocumentFragment)target, args[0]);
            return _context.WrapStaticNodeList(list);
        });

        return t;
    }

    private HostObjectTemplate CreateDocumentTemplate(IHostObjectTemplate nodeProto)
    {
        var t = new HostObjectTemplate("Document", nodeProto);

        t.DefineProperty("documentElement", (target, _) =>
            _context.Wrap(((DocumentNode)target).DocumentElement));

        t.DefineProperty("head", (target, _) =>
        {
            var doc = (DocumentNode)target;
            var head = doc.DocumentElement?.Children.OfType<ElementNode>()
                .FirstOrDefault(e => e.Name.Namespace == Namespaces.Html && e.LocalName == "head");
            return _context.Wrap(head);
        });

        t.DefineProperty("body", (target, _) =>
        {
            var doc = (DocumentNode)target;
            var body = doc.DocumentElement?.Children.OfType<ElementNode>()
                .FirstOrDefault(e => e.Name.Namespace == Namespaces.Html && (e.LocalName == "body" || e.LocalName == "frameset"));
            return _context.Wrap(body);
        });

        t.DefineProperty("title",
            (target, _) =>
            {
                var doc = (DocumentNode)target;
                var titleEl = doc.DocumentElement?.Descendants.OfType<ElementNode>()
                    .FirstOrDefault(e => e.Name.Namespace == Namespaces.Html && e.LocalName == "title");
                return ScriptValue.FromString(titleEl?.TextContent ?? "");
            },
            (target, _, val) =>
            {
                var doc = (DocumentNode)target;
                var text = WebIdlConversions.ToDOMString(val);
                var titleEl = doc.DocumentElement?.Descendants.OfType<ElementNode>()
                    .FirstOrDefault(e => e.Name.Namespace == Namespaces.Html && e.LocalName == "title");
                if (titleEl is not null)
                {
                    while (titleEl.FirstChild is { } c)
                        titleEl.RemoveChild(c);
                    if (text.Length > 0)
                        titleEl.AppendChild(doc.CreateText(text));
                }
                else
                {
                    var head = doc.DocumentElement?.Children.OfType<ElementNode>()
                        .FirstOrDefault(e => e.Name.Namespace == Namespaces.Html && e.LocalName == "head");
                    if (head is not null)
                    {
                        var newTitle = doc.CreateElement(Namespaces.Html, "title");
                        if (text.Length > 0)
                            newTitle.AppendChild(doc.CreateText(text));
                        head.AppendChild(newTitle);
                    }
                }
            });

        t.DefineProperty("readyState", (target, _) =>
        {
            var doc = (DocumentNode)target;
            return ScriptValue.FromString(doc.ReadyState);
        });

        t.DefineProperty("currentScript", (target, _) =>
        {
            var doc = (DocumentNode)target;
            return doc.CurrentScript is not null ? _context.Wrap(doc.CurrentScript) : ScriptValue.Null;
        });

        t.DefineProperty("styleSheets", (target, _) =>
        {
            var doc = (DocumentNode)target;
            var sheets = doc.Descendants.OfType<ElementNode>()
                .Where(element => element.Name.Namespace == Namespaces.Html && element.LocalName == "style")
                .Select(element => _context.Engine.CreateHostObject(CssStyleSheetTemplate, GetStyleSheet(element)))
                .ToArray();
            return _context.Engine.CreateArray(sheets);
        });

        t.DefineProperty("activeElement", (target, _) =>
        {
            var doc = (DocumentNode)target;
            return _context.Wrap(_context.FocusedElement ?? doc.DocumentElement?.Descendants.OfType<ElementNode>().FirstOrDefault(e => e.LocalName == "body") ?? doc.DocumentElement);
        });

        // ParentNode
        t.DefineProperty("children", (target, _) => _context.WrapHtmlCollection((DocumentNode)target));
        t.DefineProperty("firstElementChild", (target, _) =>
            _context.Wrap(((DocumentNode)target).DocumentElement));
        t.DefineProperty("lastElementChild", (target, _) =>
            _context.Wrap(((DocumentNode)target).DocumentElement));
        t.DefineProperty("childElementCount", (target, _) =>
            ScriptValue.FromNumber(((DocumentNode)target).DocumentElement is not null ? 1 : 0));

        // Factory methods
        t.DefineMethod("createElement", (target, _, args) =>
        {
            if (args.Length == 0)
                throw WebIdlException.TypeError("createElement requires at least 1 argument.");

            var localName = WebIdlConversions.ToDOMString(args[0]).ToLowerInvariant();
            var doc = (DocumentNode)target;
            var el = doc.CreateElement(Namespaces.Html, localName);
            return _context.Wrap(el);
        });

        t.DefineMethod("createElementNS", (target, _, args) =>
        {
            if (args.Length < 2)
                throw WebIdlException.TypeError("createElementNS requires 2 arguments.");

            var nsStr = args[0].IsNullOrUndefined ? null : WebIdlConversions.ToDOMString(args[0]);
            var qualifiedName = WebIdlConversions.ToDOMString(args[1]);

            var doc = (DocumentNode)target;
            var nsAtom = nsStr is null ? Atom.None : doc.Intern(nsStr);
            var localName = qualifiedName;
            var colon = qualifiedName.IndexOf(':');
            if (colon >= 0)
                localName = qualifiedName[(colon + 1)..];

            var el = doc.CreateElement(nsAtom, localName);
            return _context.Wrap(el);
        });

        t.DefineMethod("createTextNode", (target, _, args) =>
        {
            var text = args.Length > 0 ? WebIdlConversions.ToDOMString(args[0]) : "";
            var doc = (DocumentNode)target;
            return _context.Wrap(doc.CreateText(text));
        });

        t.DefineMethod("createComment", (target, _, args) =>
        {
            var text = args.Length > 0 ? WebIdlConversions.ToDOMString(args[0]) : "";
            var doc = (DocumentNode)target;
            return _context.Wrap(doc.CreateComment(text));
        });

        t.DefineMethod("createDocumentFragment", (target, _, _) =>
        {
            var doc = (DocumentNode)target;
            return _context.Wrap(doc.CreateFragment());
        });

        t.DefineMethod("getElementById", (target, _, args) =>
        {
            if (args.Length == 0)
                return ScriptValue.Null;

            var id = WebIdlConversions.ToDOMString(args[0]);
            var doc = (DocumentNode)target;
            var match = doc.Descendants.OfType<ElementNode>()
                .FirstOrDefault(e => e.GetAttribute("id") == id);

            return _context.Wrap(match);
        });

        t.DefineMethod("elementFromPoint", (target, _, args) =>
        {
            if (args.Length < 2)
                throw WebIdlException.TypeError("elementFromPoint requires 2 arguments.");
            var x = (float)WebIdlConversions.ToDouble(args[0]);
            var y = (float)WebIdlConversions.ToDouble(args[1]);
            var doc = (DocumentNode)target;
            var match = doc.Descendants.OfType<ElementNode>().Reverse().FirstOrDefault(element =>
            {
                if (element.ComputedStyle()?.Box.Display == Display.None)
                    return false;
                var rect = DomGeometry.Rect(element);
                return x >= rect.Left && x <= rect.Right && y >= rect.Top && y <= rect.Bottom;
            });
            return _context.Wrap(match);
        });

        t.DefineMethod("append", (target, _, args) =>
        {
            DomTreeMutation.Append(_context, (DocumentNode)target, args);
            return ScriptValue.Undefined;
        });

        t.DefineMethod("prepend", (target, _, args) =>
        {
            DomTreeMutation.Prepend(_context, (DocumentNode)target, args);
            return ScriptValue.Undefined;
        });

        t.DefineMethod("replaceChildren", (target, _, args) =>
        {
            DomTreeMutation.ReplaceChildren(_context, (DocumentNode)target, args);
            return ScriptValue.Undefined;
        });

        // ParentNode query methods
        t.DefineMethod("querySelector", (target, _, args) =>
        {
            if (args.Length == 0)
                throw WebIdlException.TypeError("querySelector requires 1 argument.");
            var el = DomSelectors.QuerySelector((DocumentNode)target, args[0]);
            return _context.Wrap(el);
        });

        t.DefineMethod("querySelectorAll", (target, _, args) =>
        {
            if (args.Length == 0)
                throw WebIdlException.TypeError("querySelectorAll requires 1 argument.");
            var list = DomSelectors.QuerySelectorAll((DocumentNode)target, args[0]);
            return _context.WrapStaticNodeList(list);
        });

        return t;
    }

    private CssStyleSheetState GetStyleSheet(ElementNode element)
    {
        if (!_styleSheets.TryGetValue(element, out var sheet))
        {
            sheet = new CssStyleSheetState(element);
            _styleSheets[element] = sheet;
        }
        return sheet;
    }

    private HostObjectTemplate CreateNodeListTemplate()
    {
        var t = new HostObjectTemplate("NodeList");

        t.DefineProperty("length", (target, _) =>
        {
            var list = (LiveNodeList)target;
            return ScriptValue.FromNumber(list.Count);
        });

        t.DefineMethod("item", (target, _, args) =>
        {
            if (args.Length == 0)
                return ScriptValue.Null;

            var list = (LiveNodeList)target;
            var idx = WebIdlConversions.ToInt32(args[0]);
            var child = list.GetItem(idx);
            return _context.Wrap(child);
        });

        t.DefineMethod("forEach", (target, thisVal, args) =>
        {
            if (args.Length == 0 || !args[0].IsFunction)
                throw WebIdlException.TypeError("callback must be a function.");

            var callback = args[0];
            var thisArg = args.Length > 1 ? args[1] : ScriptValue.Undefined;
            var list = (LiveNodeList)target;
            var snapshot = list.ToList();

            for (var i = 0; i < snapshot.Count; i++)
            {
                var childVal = _context.Wrap(snapshot[i]);
                _context.Engine.Call(callback, thisArg, [childVal, ScriptValue.FromNumber(i), thisVal]);
            }
            return ScriptValue.Undefined;
        });

        t.DefineIndexedProperty((target, _, index) =>
        {
            var list = (LiveNodeList)target;
            var child = list.GetItem(index);
            return child is not null ? _context.Wrap(child) : ScriptValue.Undefined;
        });

        return t;
    }

    private HostObjectTemplate CreateHtmlCollectionTemplate()
    {
        var t = new HostObjectTemplate("HTMLCollection");

        t.DefineProperty("length", (target, _) =>
        {
            var col = (LiveHtmlCollection)target;
            return ScriptValue.FromNumber(col.Container.Children.OfType<ElementNode>().Count());
        });

        t.DefineMethod("item", (target, _, args) =>
        {
            if (args.Length == 0)
                return ScriptValue.Null;

            var col = (LiveHtmlCollection)target;
            var idx = WebIdlConversions.ToInt32(args[0]);
            if (idx < 0)
                return ScriptValue.Null;

            var child = col.Container.Children.OfType<ElementNode>().ElementAtOrDefault(idx);
            return _context.Wrap(child);
        });

        t.DefineMethod("namedItem", (target, _, args) =>
        {
            if (args.Length == 0)
                return ScriptValue.Null;

            var col = (LiveHtmlCollection)target;
            var name = WebIdlConversions.ToDOMString(args[0]);
            var match = col.Container.Children.OfType<ElementNode>()
                .FirstOrDefault(e => e.GetAttribute("id") == name || e.GetAttribute("name") == name);

            return _context.Wrap(match);
        });

        t.DefineIndexedProperty((target, _, index) =>
        {
            if (index < 0)
                return ScriptValue.Undefined;

            var col = (LiveHtmlCollection)target;
            var child = col.Container.Children.OfType<ElementNode>().ElementAtOrDefault(index);
            return child is not null ? _context.Wrap(child) : ScriptValue.Undefined;
        });

        t.DefineNamedProperty((target, _, name) =>
        {
            var col = (LiveHtmlCollection)target;
            var match = col.Container.Children.OfType<ElementNode>()
                .FirstOrDefault(e => e.GetAttribute("id") == name || e.GetAttribute("name") == name);

            return match is not null ? _context.Wrap(match) : ScriptValue.Undefined;
        });

        return t;
    }

    private static bool IsEqualNode(Node a, Node b)
    {
        if (ReferenceEquals(a, b))
            return true;

        if (a.GetType() != b.GetType())
            return false;

        switch (a)
        {
            case DocumentType dtA when b is DocumentType dtB:
                if (dtA.Name != dtB.Name || dtA.PublicId != dtB.PublicId || dtA.SystemId != dtB.SystemId)
                    return false;
                break;

            case ElementNode elA when b is ElementNode elB:
                if (elA.Name != elB.Name)
                    return false;
                if (elA.Attributes.Length != elB.Attributes.Length)
                    return false;
                foreach (var attr in elA.Attributes)
                {
                    if (elB.GetAttribute(elA.OwnerDocument.TextOf(attr.Name)) != attr.Value)
                        return false;
                }
                break;

            case CharacterData cdA when b is CharacterData cdB:
                if (cdA.Data != cdB.Data)
                    return false;
                break;
        }

        if (a is ContainerNode cnA && b is ContainerNode cnB)
        {
            var childrenA = cnA.Children.ToList();
            var childrenB = cnB.Children.ToList();
            if (childrenA.Count != childrenB.Count)
                return false;

            for (var i = 0; i < childrenA.Count; i++)
            {
                if (!IsEqualNode(childrenA[i], childrenB[i]))
                    return false;
            }
        }

        return true;
    }

    private static ushort CompareDocumentPosition(Node a, Node b)
    {
        if (ReferenceEquals(a, b))
            return 0;

        Node rootA = a;
        while (rootA.Parent is not null) rootA = rootA.Parent;

        Node rootB = b;
        while (rootB.Parent is not null) rootB = rootB.Parent;

        if (!ReferenceEquals(rootA, rootB))
        {
            return 0x01 | 0x20 | 0x02; // Disconnected | ImplementationSpecific | Preceding
        }

        if (a.IsInclusiveAncestorOf(b))
            return 0x10 | 0x04; // ContainedBy | Following

        if (b.IsInclusiveAncestorOf(a))
            return 0x08 | 0x02; // Contains | Preceding

        // Tree order in common ancestor root
        if (rootA is ContainerNode containerRoot)
        {
            for (var node = containerRoot.FirstChild; node is not null; node = node.NextInTree(containerRoot))
            {
                if (ReferenceEquals(node, a))
                    return 0x04; // Following (b follows a)
                if (ReferenceEquals(node, b))
                    return 0x02; // Preceding (b precedes a)
            }
        }

        return 0x01;
    }

    private static void NormalizeContainer(ContainerNode container)
    {
        for (var child = container.FirstChild; child is not null;)
        {
            var next = child.NextSibling;
            if (child is Text text)
            {
                if (text.Data.Length == 0)
                {
                    container.RemoveChild(text);
                }
                else
                {
                    while (next is Text nextText)
                    {
                        var following = nextText.NextSibling;
                        text.Data += nextText.Data;
                        container.RemoveChild(nextText);
                        next = following;
                    }
                }
            }
            else if (child is ContainerNode childContainer)
            {
                NormalizeContainer(childContainer);
            }
            child = next;
        }
    }

    private HostObjectTemplate CreateDomTokenListTemplate()
    {
        var t = new HostObjectTemplate("DOMTokenList");

        t.DefineProperty("length", (target, _) =>
        {
            var list = (LiveDomTokenList)target;
            return ScriptValue.FromNumber(list.Length);
        });

        t.DefineProperty("value",
            (target, _) => ScriptValue.FromString(((LiveDomTokenList)target).Value),
            (target, _, val) => ((LiveDomTokenList)target).Value = WebIdlConversions.ToDOMString(val));

        t.DefineMethod("item", (target, _, args) =>
        {
            if (args.Length == 0)
                return ScriptValue.Null;

            var list = (LiveDomTokenList)target;
            var idx = WebIdlConversions.ToInt32(args[0]);
            var item = list.Item(idx);
            return item is not null ? ScriptValue.FromString(item) : ScriptValue.Null;
        });

        t.DefineMethod("contains", (target, _, args) =>
        {
            if (args.Length == 0)
                return ScriptValue.FromBoolean(false);

            var list = (LiveDomTokenList)target;
            var token = WebIdlConversions.ToDOMString(args[0]);
            return ScriptValue.FromBoolean(list.Contains(token));
        });

        t.DefineMethod("add", (target, _, args) =>
        {
            var list = (LiveDomTokenList)target;
            var tokens = new string[args.Length];
            for (var i = 0; i < args.Length; i++)
                tokens[i] = WebIdlConversions.ToDOMString(args[i]);

            list.Add(tokens);
            return ScriptValue.Undefined;
        });

        t.DefineMethod("remove", (target, _, args) =>
        {
            var list = (LiveDomTokenList)target;
            var tokens = new string[args.Length];
            for (var i = 0; i < args.Length; i++)
                tokens[i] = WebIdlConversions.ToDOMString(args[i]);

            list.Remove(tokens);
            return ScriptValue.Undefined;
        });

        t.DefineMethod("toggle", (target, _, args) =>
        {
            if (args.Length == 0)
                throw WebIdlException.TypeError("Failed to execute 'toggle' on 'DOMTokenList': 1 argument required.");

            var list = (LiveDomTokenList)target;
            var token = WebIdlConversions.ToDOMString(args[0]);
            bool? force = args.Length > 1 ? WebIdlConversions.ToBoolean(args[1]) : null;

            return ScriptValue.FromBoolean(list.Toggle(token, force));
        });

        t.DefineMethod("replace", (target, _, args) =>
        {
            if (args.Length < 2)
                throw WebIdlException.TypeError("Failed to execute 'replace' on 'DOMTokenList': 2 arguments required.");

            var list = (LiveDomTokenList)target;
            var oldToken = WebIdlConversions.ToDOMString(args[0]);
            var newToken = WebIdlConversions.ToDOMString(args[1]);

            return ScriptValue.FromBoolean(list.Replace(oldToken, newToken));
        });

        t.DefineMethod("supports", (_, _, _) =>
            throw WebIdlException.TypeError("DOMTokenList has no supported tokens."));

        t.DefineIndexedProperty((target, _, index) =>
        {
            var list = (LiveDomTokenList)target;
            var item = list.Item(index);
            return item is not null ? ScriptValue.FromString(item) : ScriptValue.Undefined;
        });

        t.DefineMethod("forEach", (target, thisVal, args) =>
        {
            if (args.Length == 0 || !args[0].IsFunction)
                throw WebIdlException.TypeError("callback must be a function.");

            var callback = args[0];
            var thisArg = args.Length > 1 ? args[1] : ScriptValue.Undefined;
            var list = (LiveDomTokenList)target;
            var tokens = list.GetTokens();

            for (var i = 0; i < tokens.Count; i++)
            {
                _context.Engine.Call(callback, thisArg, [ScriptValue.FromString(tokens[i]), ScriptValue.FromNumber(i), thisVal]);
            }
            return ScriptValue.Undefined;
        });

        return t;
    }

    private static HostObjectTemplate CreateDomStringMapTemplate()
    {
        var t = new HostObjectTemplate("DOMStringMap");

        t.DefineNamedProperty(
            getter: (target, _, name) =>
            {
                var map = (LiveDomStringMap)target;
                var val = map.Get(name);
                return val is not null ? ScriptValue.FromString(val) : ScriptValue.Undefined;
            },
            setter: (target, _, name, value) =>
            {
                var map = (LiveDomStringMap)target;
                var strVal = WebIdlConversions.ToDOMString(value);
                map.Set(name, strVal);
            },
            deleter: (target, _, name) =>
            {
                var map = (LiveDomStringMap)target;
                return map.Delete(name);
            },
            enumerator: (target, _) =>
            {
                var map = (LiveDomStringMap)target;
                return map.EnumerateProperties();
            });

        return t;
    }

    private HostObjectTemplate CreateNamedNodeMapTemplate()
    {
        var t = new HostObjectTemplate("NamedNodeMap");

        t.DefineProperty("length", (target, _) =>
        {
            var map = (LiveNamedNodeMap)target;
            return ScriptValue.FromNumber(map.Length);
        });

        t.DefineMethod("item", (target, _, args) =>
        {
            if (args.Length == 0)
                return ScriptValue.Null;

            var map = (LiveNamedNodeMap)target;
            var idx = WebIdlConversions.ToInt32(args[0]);
            var name = map.GetAttributeName(idx);
            return name is not null ? _context.WrapAttr(map.Element, name) : ScriptValue.Null;
        });

        t.DefineMethod("getNamedItem", (target, _, args) =>
        {
            if (args.Length == 0)
                return ScriptValue.Null;

            var map = (LiveNamedNodeMap)target;
            var name = WebIdlConversions.ToDOMString(args[0]);
            if (map.Element.GetAttribute(name) is null)
                return ScriptValue.Null;

            return _context.WrapAttr(map.Element, name);
        });

        t.DefineMethod("setNamedItem", (target, _, args) =>
        {
            if (args.Length == 0)
                throw WebIdlException.TypeError("Failed to execute 'setNamedItem' on 'NamedNodeMap': 1 argument required.");

            if (!_context.Engine.TryGetHostTarget(args[0], out var hostTarget) || hostTarget is not LiveAttr attr)
                throw WebIdlException.TypeError("parameter 1 is not of type 'Attr'.");

            var map = (LiveNamedNodeMap)target;
            if (attr.Element is not null && !ReferenceEquals(attr.Element, map.Element))
                throw WebIdlException.DomException(DomExceptionNames.InUseAttributeError, "The attribute is already in use by another element.");

            var oldAttr = map.Element.GetAttribute(attr.Name) is not null ? _context.WrapAttr(map.Element, attr.Name) : ScriptValue.Null;
            map.Element.SetAttribute(attr.Name, attr.Value);
            attr.Element = map.Element;

            return oldAttr;
        });

        t.DefineMethod("removeNamedItem", (target, _, args) =>
        {
            if (args.Length == 0)
                throw WebIdlException.TypeError("Failed to execute 'removeNamedItem' on 'NamedNodeMap': 1 argument required.");

            var map = (LiveNamedNodeMap)target;
            var name = WebIdlConversions.ToDOMString(args[0]);
            if (map.Element.GetAttribute(name) is null)
                throw WebIdlException.DomException(DomExceptionNames.NotFoundError, "The attribute could not be found.");

            var attrNode = _context.WrapAttr(map.Element, name);
            var val = map.Element.GetAttribute(name) ?? "";
            map.Element.RemoveAttribute(name);
            if (_context.Engine.TryGetHostTarget(attrNode, out var hostTarget) && hostTarget is LiveAttr attr)
                attr.Detach(val);

            return attrNode;
        });

        t.DefineIndexedProperty((target, _, index) =>
        {
            var map = (LiveNamedNodeMap)target;
            var name = map.GetAttributeName(index);
            return name is not null ? _context.WrapAttr(map.Element, name) : ScriptValue.Undefined;
        });

        t.DefineNamedProperty(
            getter: (target, _, name) =>
            {
                var map = (LiveNamedNodeMap)target;
                if (map.Element.GetAttribute(name) is null)
                    return ScriptValue.Undefined;

                return _context.WrapAttr(map.Element, name);
            },
            enumerator: (target, _) =>
            {
                var map = (LiveNamedNodeMap)target;
                return map.EnumerateNames();
            });

        return t;
    }

    private HostObjectTemplate CreateAttrTemplate(IHostObjectTemplate nodeProto)
    {
        var t = new HostObjectTemplate("Attr", nodeProto);

        t.DefineProperty("name", (target, _) => ScriptValue.FromString(((LiveAttr)target).Name));
        t.DefineProperty("localName", (target, _) => ScriptValue.FromString(((LiveAttr)target).LocalName));
        t.DefineProperty("specified", (_, _) => ScriptValue.FromBoolean(true));

        t.DefineProperty("value",
            (target, _) => ScriptValue.FromString(((LiveAttr)target).Value),
            (target, _, val) => ((LiveAttr)target).Value = WebIdlConversions.ToDOMString(val));

        t.DefineProperty("ownerElement", (target, _) =>
        {
            var el = ((LiveAttr)target).Element;
            return el is not null ? _context.Wrap(el) : ScriptValue.Null;
        });

        return t;
    }

    private HostObjectTemplate CreateHtmlInputElementTemplate(IHostObjectTemplate htmlElementProto)
    {
        var t = new HostObjectTemplate("HTMLInputElement", htmlElementProto);

        t.DefineProperty("value",
            (target, _) =>
            {
                var el = (ElementNode)target;
                return ScriptValue.FromString(el.Value ?? el.GetAttribute("value") ?? "");
            },
            (target, _, val) =>
            {
                var el = (ElementNode)target;
                el.Value = WebIdlConversions.ToDOMString(val);
            });
        t.DefineProperty("selectionStart", (target, _) => ScriptValue.FromNumber((((ElementNode)target).Value ?? "").Length));
        t.DefineProperty("selectionEnd", (target, _) => ScriptValue.FromNumber((((ElementNode)target).Value ?? "").Length));
        t.DefineProperty("selectionDirection", (_, _) => ScriptValue.FromString("none"));
        t.DefineMethod("setSelectionRange", (_, _, _) => ScriptValue.Undefined);
        t.DefineMethod("select", (_, _, _) => ScriptValue.Undefined);
        t.DefineMethod("setRangeText", (target, _, args) => { if (args.Length > 0) ((ElementNode)target).Value = WebIdlConversions.ToDOMString(args[0]); return ScriptValue.Undefined; });

        t.DefineProperty("defaultValue", (target, _) => ScriptValue.FromString(((ElementNode)target).GetAttribute("value") ?? ""),
            (target, _, value) => ((ElementNode)target).SetAttribute("value", WebIdlConversions.ToDOMString(value)));
        t.DefineProperty("defaultChecked", (target, _) => ScriptValue.FromBoolean(((ElementNode)target).GetAttribute("checked") is not null),
            (target, _, value) => { if (WebIdlConversions.ToBoolean(value)) ((ElementNode)target).SetAttribute("checked", ""); else ((ElementNode)target).RemoveAttribute("checked"); });
        t.DefineProperty("indeterminate", (target, _) => ScriptValue.FromBoolean(((ElementNode)target).GetAttribute("indeterminate") is not null),
            (target, _, value) => { if (WebIdlConversions.ToBoolean(value)) ((ElementNode)target).SetAttribute("indeterminate", ""); else ((ElementNode)target).RemoveAttribute("indeterminate"); });

        t.DefineProperty("checked",
            (target, _) => ScriptValue.FromBoolean(((ElementNode)target).IsChecked),
            (target, _, val) =>
            {
                var el = (ElementNode)target;
                el.Checked = WebIdlConversions.ToBoolean(val);
            });

        t.DefineProperty("disabled",
            (target, _) => ScriptValue.FromBoolean(((ElementNode)target).GetAttribute("disabled") is not null),
            (target, _, val) =>
            {
                if (WebIdlConversions.ToBoolean(val))
                    ((ElementNode)target).SetAttribute("disabled", "");
                else
                    ((ElementNode)target).RemoveAttribute("disabled");
            });

        t.DefineProperty("type",
            (target, _) => ScriptValue.FromString(((ElementNode)target).GetAttribute("type") ?? "text"),
            (target, _, val) => ((ElementNode)target).SetAttribute("type", WebIdlConversions.ToDOMString(val)));

        t.DefineProperty("name",
            (target, _) => ScriptValue.FromString(((ElementNode)target).GetAttribute("name") ?? ""),
            (target, _, val) => ((ElementNode)target).SetAttribute("name", WebIdlConversions.ToDOMString(val)));

        t.DefineProperty("placeholder",
            (target, _) => ScriptValue.FromString(((ElementNode)target).GetAttribute("placeholder") ?? ""),
            (target, _, val) => ((ElementNode)target).SetAttribute("placeholder", WebIdlConversions.ToDOMString(val)));

        t.DefineProperty("readOnly",
            (target, _) => ScriptValue.FromBoolean(((ElementNode)target).GetAttribute("readonly") is not null),
            (target, _, val) =>
            {
                if (WebIdlConversions.ToBoolean(val))
                    ((ElementNode)target).SetAttribute("readonly", "");
                else
                    ((ElementNode)target).RemoveAttribute("readonly");
            });

        t.DefineProperty("required",
            (target, _) => ScriptValue.FromBoolean(((ElementNode)target).GetAttribute("required") is not null),
            (target, _, val) =>
            {
                if (WebIdlConversions.ToBoolean(val))
                    ((ElementNode)target).SetAttribute("required", "");
                else
                    ((ElementNode)target).RemoveAttribute("required");
            });

        foreach (var name in new[] { "min", "max", "step" })
            t.DefineProperty(name, (target, _) => ScriptValue.FromString(((ElementNode)target).GetAttribute(name) ?? ""),
                (target, _, value) => ((ElementNode)target).SetAttribute(name, WebIdlConversions.ToDOMString(value)));
        t.DefineProperty("valueAsNumber", (target, _) =>
        {
            return double.TryParse(((ElementNode)target).Value ?? ((ElementNode)target).GetAttribute("value"), out var number) ? ScriptValue.FromNumber(number) : ScriptValue.FromNumber(double.NaN);
        }, (target, _, value) => ((ElementNode)target).Value = WebIdlConversions.ToDouble(value).ToString(System.Globalization.CultureInfo.InvariantCulture));
        t.DefineMethod("stepUp", (target, _, args) =>
        {
            var el = (ElementNode)target; var step = double.TryParse(el.GetAttribute("step"), out var parsed) ? parsed : 1; var count = args.Length > 0 ? (int)WebIdlConversions.ToDouble(args[0]) : 1; var current = double.TryParse(el.Value ?? el.GetAttribute("value"), out var value) ? value : 0; el.Value = (current + step * count).ToString(System.Globalization.CultureInfo.InvariantCulture); return ScriptValue.Undefined;
        });
        t.DefineMethod("stepDown", (target, _, args) =>
        {
            var el = (ElementNode)target; var step = double.TryParse(el.GetAttribute("step"), out var parsed) ? parsed : 1; var count = args.Length > 0 ? (int)WebIdlConversions.ToDouble(args[0]) : 1; var current = double.TryParse(el.Value ?? el.GetAttribute("value"), out var value) ? value : 0; el.Value = (current - step * count).ToString(System.Globalization.CultureInfo.InvariantCulture); return ScriptValue.Undefined;
        });
        t.DefineProperty("selectionStart", (target, _) => ScriptValue.FromNumber((((ElementNode)target).Value ?? "").Length));
        t.DefineProperty("selectionEnd", (target, _) => ScriptValue.FromNumber((((ElementNode)target).Value ?? "").Length));
        t.DefineProperty("selectionDirection", (_, _) => ScriptValue.FromString("none"));
        t.DefineMethod("setSelectionRange", (_, _, _) => ScriptValue.Undefined);
        t.DefineMethod("select", (_, _, _) => ScriptValue.Undefined);
        t.DefineMethod("setRangeText", (target, _, args) => { if (args.Length > 0) ((ElementNode)target).Value = WebIdlConversions.ToDOMString(args[0]); return ScriptValue.Undefined; });
        t.DefineMethod("click", (target, _, _) => { var el = (ElementNode)target; if (el.GetAttribute("type")?.Equals("checkbox", StringComparison.OrdinalIgnoreCase) == true) el.Checked = !el.IsChecked; return ScriptValue.Undefined; });

        t.DefineProperty("willValidate", (target, _) => ScriptValue.FromBoolean(((ElementNode)target).GetAttribute("disabled") is null && ((ElementNode)target).GetAttribute("readonly") is null));
        t.DefineProperty("validationMessage", (target, _) =>
        {
            var el = (ElementNode)target;
            var value = el.Value ?? el.GetAttribute("value") ?? "";
            if (el.GetAttribute("required") is not null && value.Length == 0) return ScriptValue.FromString("Please fill out this field.");
            if (el.GetAttribute("type")?.Equals("email", StringComparison.OrdinalIgnoreCase) == true && value.Length > 0 && !value.Contains('@')) return ScriptValue.FromString("Please enter an email address.");
            return ScriptValue.FromString("");
        });
        t.DefineMethod("checkValidity", (target, _, _) =>
        {
            var el = (ElementNode)target;
            var value = el.Value ?? el.GetAttribute("value") ?? "";
            var valid = el.GetAttribute("disabled") is not null || el.GetAttribute("readonly") is not null ||
                        (el.GetAttribute("required") is null || value.Length > 0) &&
                        (el.GetAttribute("type")?.Equals("email", StringComparison.OrdinalIgnoreCase) != true || value.Length == 0 || value.Contains('@'));
            return ScriptValue.FromBoolean(valid);
        });
        t.DefineMethod("reportValidity", (target, _, _) =>
        {
            var el = (ElementNode)target;
            var value = el.Value ?? el.GetAttribute("value") ?? "";
            var valid = el.GetAttribute("required") is null || value.Length > 0;
            return ScriptValue.FromBoolean(valid);
        });

        return t;
    }

    private HostObjectTemplate CreateHtmlButtonElementTemplate(IHostObjectTemplate htmlElementProto)
    {
        var t = new HostObjectTemplate("HTMLButtonElement", htmlElementProto);

        t.DefineProperty("disabled",
            (target, _) => ScriptValue.FromBoolean(((ElementNode)target).GetAttribute("disabled") is not null),
            (target, _, val) =>
            {
                if (WebIdlConversions.ToBoolean(val))
                    ((ElementNode)target).SetAttribute("disabled", "");
                else
                    ((ElementNode)target).RemoveAttribute("disabled");
            });

        t.DefineProperty("type",
            (target, _) => ScriptValue.FromString(((ElementNode)target).GetAttribute("type") ?? "submit"),
            (target, _, val) => ((ElementNode)target).SetAttribute("type", WebIdlConversions.ToDOMString(val)));

        t.DefineProperty("name",
            (target, _) => ScriptValue.FromString(((ElementNode)target).GetAttribute("name") ?? ""),
            (target, _, val) => ((ElementNode)target).SetAttribute("name", WebIdlConversions.ToDOMString(val)));

        t.DefineProperty("value",
            (target, _) => ScriptValue.FromString(((ElementNode)target).GetAttribute("value") ?? ""),
            (target, _, val) => ((ElementNode)target).SetAttribute("value", WebIdlConversions.ToDOMString(val)));
        t.DefineMethod("click", (target, _, _) => ScriptValue.Undefined);

        return t;
    }

    private HostObjectTemplate CreateHtmlAnchorElementTemplate(IHostObjectTemplate htmlElementProto)
    {
        var t = new HostObjectTemplate("HTMLAnchorElement", htmlElementProto);

        t.DefineProperty("href",
            (target, _) => ScriptValue.FromString(((ElementNode)target).GetAttribute("href") ?? ""),
            (target, _, val) => ((ElementNode)target).SetAttribute("href", WebIdlConversions.ToDOMString(val)));

        t.DefineProperty("target",
            (target, _) => ScriptValue.FromString(((ElementNode)target).GetAttribute("target") ?? ""),
            (target, _, val) => ((ElementNode)target).SetAttribute("target", WebIdlConversions.ToDOMString(val)));

        t.DefineProperty("rel",
            (target, _) => ScriptValue.FromString(((ElementNode)target).GetAttribute("rel") ?? ""),
            (target, _, val) => ((ElementNode)target).SetAttribute("rel", WebIdlConversions.ToDOMString(val)));

        return t;
    }

    private HostObjectTemplate CreateHtmlImageElementTemplate(IHostObjectTemplate htmlElementProto)
    {
        var t = new HostObjectTemplate("HTMLImageElement", htmlElementProto);

        t.DefineProperty("src",
            (target, _) => ScriptValue.FromString(((ElementNode)target).GetAttribute("src") ?? ""),
            (target, _, val) => ((ElementNode)target).SetAttribute("src", WebIdlConversions.ToDOMString(val)));

        t.DefineProperty("alt",
            (target, _) => ScriptValue.FromString(((ElementNode)target).GetAttribute("alt") ?? ""),
            (target, _, val) => ((ElementNode)target).SetAttribute("alt", WebIdlConversions.ToDOMString(val)));

        t.DefineProperty("width",
            (target, _) =>
            {
                var val = ((ElementNode)target).GetAttribute("width");
                return ScriptValue.FromNumber(val is not null && int.TryParse(val, out var w) ? w : 0);
            },
            (target, _, val) =>
            {
                var num = WebIdlConversions.ToUint32(val);
                ((ElementNode)target).SetAttribute("width", num.ToString());
            });

        t.DefineProperty("height",
            (target, _) =>
            {
                var val = ((ElementNode)target).GetAttribute("height");
                return ScriptValue.FromNumber(val is not null && int.TryParse(val, out var h) ? h : 0);
            },
            (target, _, val) =>
            {
                var num = WebIdlConversions.ToUint32(val);
                ((ElementNode)target).SetAttribute("height", num.ToString());
            });

        return t;
    }

    private HostObjectTemplate CreateHtmlOptionElementTemplate(IHostObjectTemplate htmlElementProto)
    {
        var t = new HostObjectTemplate("HTMLOptionElement", htmlElementProto);

        t.DefineProperty("value",
            (target, _) =>
            {
                var el = (ElementNode)target;
                return ScriptValue.FromString(el.GetAttribute("value") ?? el.TextContent);
            },
            (target, _, val) => ((ElementNode)target).SetAttribute("value", WebIdlConversions.ToDOMString(val)));

        t.DefineProperty("selected",
            (target, _) => ScriptValue.FromBoolean(((ElementNode)target).IsSelected),
            (target, _, val) =>
            {
                var el = (ElementNode)target;
                el.Selected = WebIdlConversions.ToBoolean(val);
            });

        t.DefineProperty("disabled",
            (target, _) => ScriptValue.FromBoolean(((ElementNode)target).GetAttribute("disabled") is not null),
            (target, _, val) =>
            {
                if (WebIdlConversions.ToBoolean(val))
                    ((ElementNode)target).SetAttribute("disabled", "");
                else
                    ((ElementNode)target).RemoveAttribute("disabled");
            });

        t.DefineProperty("text",
            (target, _) => ScriptValue.FromString(((ElementNode)target).TextContent),
            (target, _, val) =>
            {
                var el = (ElementNode)target;
                var text = WebIdlConversions.ToDOMString(val);
                while (el.FirstChild is { } c)
                    el.RemoveChild(c);
                if (text.Length > 0)
                    el.AppendChild(el.OwnerDocument.CreateText(text));
            });

        return t;
    }

    private HostObjectTemplate CreateHtmlSelectElementTemplate(IHostObjectTemplate htmlElementProto)
    {
        var t = new HostObjectTemplate("HTMLSelectElement", htmlElementProto);

        t.DefineProperty("value",
            (target, _) =>
            {
                var select = (ElementNode)target;
                var options = select.Descendants.OfType<ElementNode>().Where(e => e.LocalName == "option").ToList();
                var selected = options.FirstOrDefault(o => o.IsSelected) ?? options.FirstOrDefault();
                if (selected is null)
                    return ScriptValue.FromString("");

                return ScriptValue.FromString(selected.GetAttribute("value") ?? selected.TextContent);
            },
            (target, _, val) =>
            {
                var select = (ElementNode)target;
                var strVal = WebIdlConversions.ToDOMString(val);
                var options = select.Descendants.OfType<ElementNode>().Where(e => e.LocalName == "option").ToList();
                foreach (var opt in options)
                {
                    var optVal = opt.GetAttribute("value") ?? opt.TextContent;
                    opt.Selected = optVal == strVal;
                }
            });

        t.DefineProperty("selectedIndex",
            (target, _) =>
            {
                var select = (ElementNode)target;
                var options = select.Descendants.OfType<ElementNode>().Where(e => e.LocalName == "option").ToList();
                for (var i = 0; i < options.Count; i++)
                {
                    if (options[i].IsSelected)
                        return ScriptValue.FromNumber(i);
                }
                return ScriptValue.FromNumber(options.Count > 0 ? 0 : -1);
            },
            (target, _, val) =>
            {
                var select = (ElementNode)target;
                var idx = WebIdlConversions.ToInt32(val);
                var options = select.Descendants.OfType<ElementNode>().Where(e => e.LocalName == "option").ToList();
                for (var i = 0; i < options.Count; i++)
                {
                    options[i].Selected = i == idx;
                }
            });
        t.DefineProperty("options", (target, _) => _context.WrapHtmlCollection((ElementNode)target));
        t.DefineProperty("length", (target, _) => ScriptValue.FromNumber(((ElementNode)target).Descendants.OfType<ElementNode>().Count(e => e.LocalName == "option")));
        t.DefineProperty("multiple", (target, _) => ScriptValue.FromBoolean(((ElementNode)target).GetAttribute("multiple") is not null),
            (target, _, value) => { if (WebIdlConversions.ToBoolean(value)) ((ElementNode)target).SetAttribute("multiple", ""); else ((ElementNode)target).RemoveAttribute("multiple"); });
        t.DefineMethod("remove", (target, _, args) =>
        {
            var options = ((ElementNode)target).Descendants.OfType<ElementNode>().Where(e => e.LocalName == "option").ToList();
            if (args.Length > 0 && int.TryParse(WebIdlConversions.ToDOMString(args[0]), out var index) && index >= 0 && index < options.Count) options[index].Parent?.RemoveChild(options[index]);
            return ScriptValue.Undefined;
        });

        t.DefineProperty("disabled",
            (target, _) => ScriptValue.FromBoolean(((ElementNode)target).GetAttribute("disabled") is not null),
            (target, _, val) =>
            {
                if (WebIdlConversions.ToBoolean(val))
                    ((ElementNode)target).SetAttribute("disabled", "");
                else
                    ((ElementNode)target).RemoveAttribute("disabled");
            });

        t.DefineProperty("name",
            (target, _) => ScriptValue.FromString(((ElementNode)target).GetAttribute("name") ?? ""),
            (target, _, val) => ((ElementNode)target).SetAttribute("name", WebIdlConversions.ToDOMString(val)));

        return t;
    }

    private HostObjectTemplate CreateHtmlTextAreaElementTemplate(IHostObjectTemplate htmlElementProto)
    {
        var t = new HostObjectTemplate("HTMLTextAreaElement", htmlElementProto);

        t.DefineProperty("value",
            (target, _) =>
            {
                var el = (ElementNode)target;
                return ScriptValue.FromString(el.Value ?? el.TextContent);
            },
            (target, _, val) =>
            {
                var el = (ElementNode)target;
                el.Value = WebIdlConversions.ToDOMString(val);
            });
        t.DefineProperty("defaultValue", (target, _) => ScriptValue.FromString(((ElementNode)target).TextContent ?? ((ElementNode)target).GetAttribute("value") ?? ""),
            (target, _, value) => { var el = (ElementNode)target; while (el.FirstChild is { } child) el.RemoveChild(child); el.AppendChild(el.OwnerDocument.CreateText(WebIdlConversions.ToDOMString(value))); });

        t.DefineProperty("disabled",
            (target, _) => ScriptValue.FromBoolean(((ElementNode)target).GetAttribute("disabled") is not null),
            (target, _, val) =>
            {
                if (WebIdlConversions.ToBoolean(val))
                    ((ElementNode)target).SetAttribute("disabled", "");
                else
                    ((ElementNode)target).RemoveAttribute("disabled");
            });

        t.DefineProperty("name",
            (target, _) => ScriptValue.FromString(((ElementNode)target).GetAttribute("name") ?? ""),
            (target, _, val) => ((ElementNode)target).SetAttribute("name", WebIdlConversions.ToDOMString(val)));

        t.DefineProperty("placeholder",
            (target, _) => ScriptValue.FromString(((ElementNode)target).GetAttribute("placeholder") ?? ""),
            (target, _, val) => ((ElementNode)target).SetAttribute("placeholder", WebIdlConversions.ToDOMString(val)));

        t.DefineProperty("rows",
            (target, _) =>
            {
                var val = ((ElementNode)target).GetAttribute("rows");
                return ScriptValue.FromNumber(val is not null && uint.TryParse(val, out var r) ? r : 2);
            },
            (target, _, val) =>
            {
                var num = WebIdlConversions.ToUint32(val);
                ((ElementNode)target).SetAttribute("rows", num.ToString());
            });

        t.DefineProperty("cols",
            (target, _) =>
            {
                var val = ((ElementNode)target).GetAttribute("cols");
                return ScriptValue.FromNumber(val is not null && uint.TryParse(val, out var c) ? c : 20);
            },
            (target, _, val) =>
            {
                var num = WebIdlConversions.ToUint32(val);
                ((ElementNode)target).SetAttribute("cols", num.ToString());
            });

        return t;
    }

    private HostObjectTemplate CreateHtmlTemplateElementTemplate(IHostObjectTemplate htmlElementProto)
    {
        var t = new HostObjectTemplate("HTMLTemplateElement", htmlElementProto);

        t.DefineProperty("content", (target, _) =>
        {
            var template = (TemplateElement)target;
            return _context.Wrap(template.Content);
        });

        return t;
    }

    private static HostObjectTemplate CreateHtmlScriptElementTemplate(IHostObjectTemplate htmlElementProto)
    {
        var t = new HostObjectTemplate("HTMLScriptElement", htmlElementProto);

        t.DefineProperty("src",
            (target, _) => ScriptValue.FromString(((ElementNode)target).GetAttribute("src") ?? ""),
            (target, _, val) => ((ElementNode)target).SetAttribute("src", WebIdlConversions.ToDOMString(val)));

        t.DefineProperty("type",
            (target, _) => ScriptValue.FromString(((ElementNode)target).GetAttribute("type") ?? ""),
            (target, _, val) => ((ElementNode)target).SetAttribute("type", WebIdlConversions.ToDOMString(val)));

        t.DefineProperty("defer",
            (target, _) => ScriptValue.FromBoolean(((ElementNode)target).GetAttribute("defer") is not null),
            (target, _, val) =>
            {
                var el = (ElementNode)target;
                if (WebIdlConversions.ToBoolean(val))
                    el.SetAttribute("defer", "");
                else
                    el.RemoveAttribute("defer");
            });

        t.DefineProperty("async",
            (target, _) => ScriptValue.FromBoolean(((ElementNode)target).GetAttribute("async") is not null),
            (target, _, val) =>
            {
                var el = (ElementNode)target;
                if (WebIdlConversions.ToBoolean(val))
                    el.SetAttribute("async", "");
                else
                    el.RemoveAttribute("async");
            });

        t.DefineProperty("text",
            (target, _) => ScriptValue.FromString(((ElementNode)target).TextContent ?? ""),
            (target, _, val) =>
            {
                var el = (ElementNode)target;
                var text = WebIdlConversions.ToDOMString(val);
                while (el.FirstChild is { } c)
                    el.RemoveChild(c);
                if (text.Length > 0)
                    el.AppendChild(el.OwnerDocument.CreateText(text));
            });

        return t;
    }
}

