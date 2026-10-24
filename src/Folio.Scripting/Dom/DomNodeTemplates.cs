using Folio.Scripting.WebGl;
using System.Buffers.Binary;
using System.Text;
using Folio.Dom;
using Folio.Painting;
using Folio.Style;

namespace Folio.Scripting.Dom;

/// <summary>
/// Defines host object templates for DOM interfaces conforming to W3C / WHATWG WebIDL specifications (study 02, #451).
/// </summary>
internal sealed class DomNodeTemplates
{
    private readonly DomContext _context;

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
            .DefineProperty("previousSibling", (target, _) => _context.Wrap(((MutationRecordState)target).PreviousSibling))
            .DefineProperty("nextSibling", (target, _) => _context.Wrap(((MutationRecordState)target).NextSibling))
            .DefineProperty("attributeNamespace", (_, _) => ScriptValue.Null);
        MutationObserverTemplate = new HostObjectTemplate("MutationObserver")
            // The script-visible MutationObserver is a class in the bindings' script; it checks and normalises the options and
            // calls these with (target, childList, subtree, attributes, attributeOldValue, characterData, characterDataOldValue, filter).
            .DefineMethod("observe", (target, _, args) =>
            {
                if (args.Length < 8 || !_context.Engine.TryGetHostTarget(args[0], out var node) || node is not Node observed)
                    throw WebIdlException.TypeError("Failed to execute 'observe' on 'MutationObserver': parameter 1 is not of type 'Node'.");
                IReadOnlyList<string>? filter = null;
                if (args[7].IsArray && _context.Engine.TryGetArrayItems(args[7], out var items))
                    filter = items.Select(item => item.AsString() ?? "").ToArray();
                _context.MutationObservers.Observe((MutationObserverState)target, observed,
                    new MutationObserverOptions(args[1].AsBoolean(), args[2].AsBoolean(), args[3].AsBoolean(), args[4].AsBoolean(), args[5].AsBoolean(), args[6].AsBoolean(), filter));
                return ScriptValue.Undefined;
            })
            .DefineMethod("disconnect", (target, _, _) => { _context.MutationObservers.Disconnect((MutationObserverState)target); return ScriptValue.Undefined; })
            .DefineMethod("takeRecords", (target, _, _) => _context.Engine.CreateArray(_context.MutationObservers.Take((MutationObserverState)target).Select(_context.WrapMutationRecord).ToArray()));
        MessageEventTemplate = new HostObjectTemplate("MessageEvent")
            .DefineProperty("data", (target, _) => ((MessageEventState)target).Data)
            .DefineProperty("origin", (target, _) => ScriptValue.FromString(((MessageEventState)target).Origin))
            .DefineProperty("source", (target, _) => ((MessageEventState)target).Source)
            .DefineProperty("type", (target, _) => ScriptValue.FromString(((MessageEventState)target).Type))
            .DefineProperty("target", (target, _) => ((MessageEventState)target).Target)
            .DefineProperty("ports", (target, _) => ((MessageEventState)target).Ports);
        MediaEventTemplate = new HostObjectTemplate("Event")
            .DefineProperty("type", (target, _) => ScriptValue.FromString(((MediaEventState)target).Type));

        HtmlInputElementTemplate = CreateHtmlInputElementTemplate(HtmlElementTemplate);
        HtmlButtonElementTemplate = CreateHtmlButtonElementTemplate(HtmlElementTemplate);
        HtmlAnchorElementTemplate = CreateHtmlAnchorElementTemplate(HtmlElementTemplate);
        HtmlImageElementTemplate = CreateHtmlImageElementTemplate(HtmlElementTemplate);
        HtmlOptionElementTemplate = CreateHtmlOptionElementTemplate(HtmlElementTemplate);
        HtmlSelectElementTemplate = CreateHtmlSelectElementTemplate(HtmlElementTemplate);
        HtmlTextAreaElementTemplate = CreateHtmlTextAreaElementTemplate(HtmlElementTemplate);
        ValidityStateTemplate = FormBindings.CreateValidityStateTemplate();
        FormControlsCollectionTemplate = FormBindings.CreateControlsCollectionTemplate(_context);
        HtmlFormElementTemplate = FormBindings.CreateFormTemplate(HtmlElementTemplate, _context, FormControlsCollectionTemplate);
        FormBindings.DefineClick(HtmlElementTemplate, _context);
        foreach (var control in new[] { HtmlInputElementTemplate, HtmlButtonElementTemplate, HtmlSelectElementTemplate, HtmlTextAreaElementTemplate })
            FormBindings.DefineControlMembers(control, _context, ValidityStateTemplate);
        FormBindings.DefineTextControlMembers(HtmlInputElementTemplate, _context);
        FormBindings.DefineTextControlMembers(HtmlTextAreaElementTemplate, _context);
        CssomViewBindings.DefineElementMembers(ElementTemplate, _context);
        CssomViewBindings.DefineHtmlElementMembers(HtmlElementTemplate, _context);
        CssomViewBindings.DefineDocumentMembers(DocumentTemplate, _context);
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
            .DefineProperty("text", (target, _) => ScriptValue.FromString(((TextTrackCueState)target).Text))
            .DefineProperty("id", (target, _) => ScriptValue.FromString(((TextTrackCueState)target).Id))
            .DefineProperty("onenter", (target, _) => ((TextTrackCueState)target).OnEnter,
                (target, _, value) => ((TextTrackCueState)target).OnEnter = value)
            .DefineProperty("onexit", (target, _) => ((TextTrackCueState)target).OnExit,
                (target, _, value) => ((TextTrackCueState)target).OnExit = value);
        TimeRangesTemplate = new HostObjectTemplate("TimeRanges")
            .DefineProperty("length", (target, _) => ScriptValue.FromNumber(((MediaTimeRangesState)target).Ranges.Count))
            .DefineMethod("start", (target, _, args) => GetTimeRange((MediaTimeRangesState)target, args, start: true))
            .DefineMethod("end", (target, _, args) => GetTimeRange((MediaTimeRangesState)target, args, start: false));
        TextTrackTemplate = new HostObjectTemplate("TextTrack")
            .DefineProperty("kind", (target, _) => ScriptValue.FromString(((MediaTextTrackState)target).Kind))
            .DefineProperty("label", (target, _) => ScriptValue.FromString(((MediaTextTrackState)target).Label))
            .DefineProperty("language", (target, _) => ScriptValue.FromString(((MediaTextTrackState)target).Language))
            .DefineProperty("mode", (target, _) => ScriptValue.FromString(((MediaTextTrackState)target).Mode),
                (target, _, value) =>
                {
                    var state = (MediaTextTrackState)target;
                    var before = state.ActiveCues.ToArray();
                    state.Mode = WebIdlConversions.ToDOMString(value);
                    var after = state.ActiveCues;
                    if (!before.SequenceEqual(after)) DispatchCueChange(state);
                })
            .DefineProperty("cues", (target, _) => _context.Engine.CreateArray(((MediaTextTrackState)target).Cues.Select(cue => _context.Engine.CreateHostObject(TextTrackCueTemplate, cue)).ToArray()))
            .DefineProperty("activeCues", (target, _) => _context.Engine.CreateArray(((MediaTextTrackState)target).ActiveCues.Select(cue => _context.Engine.CreateHostObject(TextTrackCueTemplate, cue)).ToArray()))
            .DefineProperty("oncuechange", (target, _) => ((MediaTextTrackState)target).OnCueChange,
                (target, _, value) => ((MediaTextTrackState)target).OnCueChange = value)
            .DefineMethod("addEventListener", (target, _, args) => { if (args.Length > 1 && WebIdlConversions.ToDOMString(args[0]) == "cuechange" && args[1].IsFunction && !((MediaTextTrackState)target).CueChangeListeners.Contains(args[1])) ((MediaTextTrackState)target).CueChangeListeners.Add(args[1]); return ScriptValue.Undefined; })
            .DefineMethod("removeEventListener", (target, _, args) => { if (args.Length > 1 && WebIdlConversions.ToDOMString(args[0]) == "cuechange") ((MediaTextTrackState)target).CueChangeListeners.Remove(args[1]); return ScriptValue.Undefined; })
            .DefineMethod("addCue", (target, _, args) =>
            {
                if (args.Length < 3) throw WebIdlException.TypeError("addCue requires start time, end time, and text.");
                var state = (MediaTextTrackState)target;
                var before = state.ActiveCues.ToArray();
                var start = WebIdlConversions.ToDouble(args[0]);
                var end = WebIdlConversions.ToDouble(args[1]);
                if (double.IsNaN(start) || double.IsInfinity(start) || double.IsNaN(end) || double.IsInfinity(end) || end < start)
                    throw WebIdlException.DomException(DomExceptionNames.IndexSizeError, "The cue end time must be finite and at least its start time.");
                state.Cues.Add(new TextTrackCueState(start, end, WebIdlConversions.ToDOMString(args[2])));
                if (!before.SequenceEqual(state.ActiveCues)) DispatchCueChange(state);
                return ScriptValue.Undefined;
            })
            .DefineMethod("removeCue", (target, _, args) =>
            {
                if (args.Length < 1 || !args[0].IsObject) throw WebIdlException.TypeError("removeCue requires a TextTrackCue.");
                var state = (MediaTextTrackState)target;
                var cue = args[0];
                var start = WebIdlConversions.ToDouble(_context.Engine.GetProperty(cue, "startTime"));
                var end = WebIdlConversions.ToDouble(_context.Engine.GetProperty(cue, "endTime"));
                var text = WebIdlConversions.ToDOMString(_context.Engine.GetProperty(cue, "text"));
                var index = state.Cues.FindIndex(item => item.StartTime == start && item.EndTime == end && item.Text == text);
                if (index < 0) throw WebIdlException.DomException(DomExceptionNames.NotFoundError, "The cue is not in this text track.");
                var before = state.ActiveCues.ToArray();
                var removed = state.Cues[index];
                if (before.Contains(removed) && removed.OnExit.IsFunction)
                    _context.Engine.Call(removed.OnExit, ScriptValue.Undefined, [_context.Engine.CreateHostObject(MediaEventTemplate, new MediaEventState("exit"))]);
                state.Cues.RemoveAt(index);
                if (!before.SequenceEqual(state.ActiveCues)) DispatchCueChange(state);
                return ScriptValue.Undefined;
            });
        Canvas = new CanvasTemplates(_context);
        WebGlRenderingContextTemplate = CreateWebGlRenderingContextTemplate();
        WebGlLoseContextTemplate = new HostObjectTemplate("WEBGLLoseContext")
            .DefineMethod("loseContext", (target, _, _) => { ((WebGlContextState)target).Lose(); return ScriptValue.Undefined; })
            .DefineMethod("restoreContext", (target, _, _) => { if (_context.CheckCapability("WebGL", _context.Capabilities.WebGl)) ((WebGlContextState)target).Restore(); return ScriptValue.Undefined; });
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
    public HostObjectTemplate HtmlFormElementTemplate { get; }
    public HostObjectTemplate ValidityStateTemplate { get; }
    public HostObjectTemplate FormControlsCollectionTemplate { get; }
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
    public CanvasTemplates Canvas { get; }
    public HostObjectTemplate WebGlRenderingContextTemplate { get; }
    public HostObjectTemplate WebGlLoseContextTemplate { get; }

    private HostObjectTemplate CreateHtmlCanvasElementTemplate(IHostObjectTemplate htmlElementProto)
    {
        var t = new HostObjectTemplate("HTMLCanvasElement", htmlElementProto);
        // Setting either size, to the value it has or any other, empties the bitmap and resets the context (HTML §4.12.5).
        t.DefineProperty("width", (target, _) => ScriptValue.FromNumber(_context.GetCanvas((ElementNode)target).Width), (target, _, value) =>
        {
            ((ElementNode)target).SetAttribute("width", WebIdlConversions.ToUint32(value).ToString());
            _context.GetCanvas((ElementNode)target).Restart();
        });
        t.DefineProperty("height", (target, _) => ScriptValue.FromNumber(_context.GetCanvas((ElementNode)target).Height), (target, _, value) =>
        {
            ((ElementNode)target).SetAttribute("height", WebIdlConversions.ToUint32(value).ToString());
            _context.GetCanvas((ElementNode)target).Restart();
        });
        t.DefineMethod("getContext", (target, _, args) =>
        {
            if (args.Length == 0) return ScriptValue.Null;
            var kind = WebIdlConversions.ToDOMString(args[0]);
            var canvas = _context.GetCanvas((ElementNode)target);
            if (string.Equals(kind, "2d", StringComparison.Ordinal))
            {
                var alpha = true;
                if (args.Length > 1 && args[1].IsObject && _context.Engine.GetProperty(args[1], "alpha") is { IsUndefined: false } flag)
                    alpha = WebIdlConversions.ToBoolean(flag);
                if (canvas.GetContext2D(alpha, () => new Folio.Painting.Rgba(0, 0, 0, 1)) is null)
                    return ScriptValue.Null;
                // The same object every time, so contexts compare equal.
                return _context.WrapperCache.GetOrCreate(_context.Engine, Canvas.Context, canvas);
            }
            if (string.Equals(kind, "webgl", StringComparison.Ordinal) || string.Equals(kind, "webgl2", StringComparison.Ordinal) || string.Equals(kind, "experimental-webgl", StringComparison.Ordinal))
            {
                if (canvas.Context2D is not null) return ScriptValue.Null;
                canvas.AttemptWebGl();
                if (!_context.CheckCapability("WebGL", _context.Capabilities.WebGl))
                {
                    canvas.MarkWebGlDenied();
                    return ScriptValue.Null;
                }
                canvas.ProbeGraphics();
                if (canvas.WebGlContext is { } existing)
                    return _context.Engine.CreateHostObject(WebGlRenderingContextTemplate, existing);
                canvas.ClearFallback();
                var state = new WebGlContextState(_context.Engine, canvas, _context.MaxGpuBytes, _context.MaxShaderExecutionTime);
                state.InitializeViewport();
                if (args.Length > 1 && args[1].IsObject)
                {
                    var opt = args[1];
                    if (_context.Engine.GetProperty(opt, "preserveDrawingBuffer") is { IsUndefined: false } pdb)
                        state.PreserveDrawingBuffer = WebIdlConversions.ToBoolean(pdb);
                    if (_context.Engine.GetProperty(opt, "alpha") is { IsUndefined: false } a)
                        state.Alpha = WebIdlConversions.ToBoolean(a);
                    if (_context.Engine.GetProperty(opt, "depth") is { IsUndefined: false } d)
                        state.Depth = WebIdlConversions.ToBoolean(d);
                    if (_context.Engine.GetProperty(opt, "stencil") is { IsUndefined: false } s)
                        state.Stencil = WebIdlConversions.ToBoolean(s);
                    if (_context.Engine.GetProperty(opt, "antialias") is { IsUndefined: false } aa)
                        state.Antialias = WebIdlConversions.ToBoolean(aa);
                    if (_context.Engine.GetProperty(opt, "premultipliedAlpha") is { IsUndefined: false } pa)
                        state.PremultipliedAlpha = WebIdlConversions.ToBoolean(pa);
                }
                return _context.Engine.CreateHostObject(WebGlRenderingContextTemplate, canvas.WebGlContext = state);
            }
            return ScriptValue.Null;
        });
        t.DefineMethod("toDataURL", (target, _, args) =>
        {
            var canvas = _context.GetCanvas((ElementNode)target);
            var type = args.Length > 0 && args[0].IsString ? args[0].AsString()!.ToLowerInvariant() : "image/png";
            var quality = args.Length > 1 && args[1].IsNumber ? args[1].AsNumber() : 0.92;
            // A canvas without pixels gives the empty image, as one of size 0 does.
            if (canvas.Width == 0 || canvas.Height == 0 || canvas.Surface() is not { } surface)
                return ScriptValue.FromString("data:,");
            canvas.Sync();
            var bytes = surface.Encode(type, quality);
            if (bytes is null)
            {
                type = "image/png";
                bytes = surface.Encode(type, 1);
            }
            return ScriptValue.FromString(bytes is null ? "data:," : $"data:{type};base64,{Convert.ToBase64String(bytes)}");
        });
        t.DefineMethod("toBlob", (target, _, args) =>
        {
            var canvas = _context.GetCanvas((ElementNode)target);
            if (args.Length == 0 || !args[0].IsFunction)
                throw WebIdlException.TypeError("toBlob requires a callback function.");
            var callback = args[0];
            var type = args.Length > 1 && args[1].IsString ? args[1].AsString()!.ToLowerInvariant() : "image/png";
            var quality = args.Length > 2 && args[2].IsNumber ? args[2].AsNumber() : 0.92;
            if (canvas.Width == 0 || canvas.Height == 0 || canvas.Surface() is not { } surface)
            {
                _context.Engine.Call(callback, ScriptValue.Undefined, [ScriptValue.Null]);
                return ScriptValue.Undefined;
            }
            canvas.Sync();
            var bytes = surface.Encode(type, quality) ?? surface.Encode("image/png", 1);
            if (bytes is null)
            {
                _context.Engine.Call(callback, ScriptValue.Undefined, [ScriptValue.Null]);
                return ScriptValue.Undefined;
            }
            var buffer = _context.Engine.CreateArrayBuffer(bytes);
            var blobFunc = _context.Engine.Evaluate("(buf, t) => new Blob([buf], { type: t })");
            var blob = _context.Engine.Call(blobFunc, ScriptValue.Undefined, [buffer, ScriptValue.FromString(type)]);
            _context.Engine.Call(callback, ScriptValue.Undefined, [blob]);
            return ScriptValue.Undefined;
        });
        return t;
    }

    private void MediaAttributeChanged(ElementNode element, string name)
    {
        if (element.Name.Namespace != Namespaces.Html || element.LocalName is not ("audio" or "video")) return;
        if (name.Equals("src", StringComparison.OrdinalIgnoreCase)) _context.GetMedia(element).Load();
        else if (name.Equals("poster", StringComparison.OrdinalIgnoreCase)) _context.GetMedia(element).RefreshPoster();
    }

    private static string AttributeName(ElementNode element, string name) => element.Name.Namespace != Namespaces.Html ? name
        : string.Create(name.Length, name, static (result, source) =>
        {
            for (var i = 0; i < source.Length; i++) result[i] = source[i] is >= 'A' and <= 'Z' ? (char)(source[i] + ('a' - 'A')) : source[i];
        });

    private HostObjectTemplate CreateHtmlMediaElementTemplate(IHostObjectTemplate htmlElementProto, string interfaceName = "HTMLMediaElement")
    {
        var t = new HostObjectTemplate(interfaceName, htmlElementProto);
        if (interfaceName == "HTMLVideoElement")
            t.DefineProperty("poster", (target, _) => ScriptValue.FromString(((ElementNode)target).GetAttribute("poster") ?? ""),
                (target, _, value) => { var element = (ElementNode)target; element.SetAttribute("poster", WebIdlConversions.ToDOMString(value)); _context.GetMedia(element).RefreshPoster(); });
        t.DefineProperty("src", (target, _) => ScriptValue.FromString(((ElementNode)target).GetAttribute("src") ?? ""),
            (target, _, value) => ((ElementNode)target).SetAttribute("src", WebIdlConversions.ToDOMString(value)));
        t.DefineProperty("currentSrc", (target, _) => ScriptValue.FromString(((ElementNode)target).GetAttribute("src") ?? ""));
        t.DefineProperty("srcObject", (target, _) => _context.GetMedia((ElementNode)target).SrcObject,
            (target, _, value) => _context.GetMedia((ElementNode)target).SetSrcObject(value));
        t.DefineProperty("currentTime", (target, _) => ScriptValue.FromNumber(_context.GetMedia((ElementNode)target).CurrentTime),
            (target, _, value) => _context.GetMedia((ElementNode)target).Seek(WebIdlConversions.ToDouble(value)));
        t.DefineProperty("src", (target, _) => ScriptValue.FromString(((ElementNode)target).GetAttribute("src") ?? ""),
            (target, _, value) => { var element = (ElementNode)target; element.SetAttribute("src", WebIdlConversions.ToDOMString(value)); _context.GetMedia(element).Load(); });
        t.DefineProperty("currentSrc", (target, _) => ScriptValue.FromString(((ElementNode)target).GetAttribute("src") ?? ""));
        t.DefineProperty("duration", (target, _) => ScriptValue.FromNumber(_context.GetMedia((ElementNode)target).Duration));
        t.DefineProperty("videoWidth", (target, _) => ScriptValue.FromNumber(_context.GetMedia((ElementNode)target).Width));
        t.DefineProperty("videoHeight", (target, _) => ScriptValue.FromNumber(_context.GetMedia((ElementNode)target).Height));
        t.DefineProperty("paused", (target, _) => ScriptValue.FromBoolean(_context.GetMedia((ElementNode)target).Paused));
        t.DefineProperty("ended", (target, _) => ScriptValue.FromBoolean(_context.GetMedia((ElementNode)target).Ended));
        t.DefineProperty("volume", (target, _) => ScriptValue.FromNumber(_context.GetMedia((ElementNode)target).Volume),
            (target, _, value) => _context.GetMedia((ElementNode)target).SetVolume(WebIdlConversions.ToDouble(value)));
        t.DefineProperty("muted", (target, _) => ScriptValue.FromBoolean(_context.GetMedia((ElementNode)target).Muted),
            (target, _, value) => _context.GetMedia((ElementNode)target).SetMuted(WebIdlConversions.ToBoolean(value)));
        t.DefineProperty("autoplay", (target, _) => ScriptValue.FromBoolean(_context.GetMedia((ElementNode)target).Autoplay),
            (target, _, value) => _context.GetMedia((ElementNode)target).Autoplay = WebIdlConversions.ToBoolean(value));
        t.DefineProperty("loop", (target, _) => ScriptValue.FromBoolean(_context.GetMedia((ElementNode)target).Loop),
            (target, _, value) => _context.GetMedia((ElementNode)target).Loop = WebIdlConversions.ToBoolean(value));
        t.DefineProperty("controls", (target, _) => ScriptValue.FromBoolean(_context.GetMedia((ElementNode)target).Controls),
            (target, _, value) => _context.GetMedia((ElementNode)target).Controls = WebIdlConversions.ToBoolean(value));
        t.DefineProperty("textTracks", (target, _) => _context.Engine.CreateArray(_context.GetMedia((ElementNode)target).TextTracks.Select(track => _context.Engine.CreateHostObject(TextTrackTemplate, track)).ToArray()));
        t.DefineProperty("preload", (target, _) => ScriptValue.FromString(_context.GetMedia((ElementNode)target).Preload),
            (target, _, value) => _context.GetMedia((ElementNode)target).Preload = WebIdlConversions.ToDOMString(value));
        t.DefineProperty("defaultMuted", (target, _) => ScriptValue.FromBoolean(_context.GetMedia((ElementNode)target).DefaultMuted),
            (target, _, value) => _context.GetMedia((ElementNode)target).DefaultMuted = WebIdlConversions.ToBoolean(value));
        t.DefineProperty("crossOrigin", (target, _) => ScriptValue.FromString(_context.GetMedia((ElementNode)target).CrossOrigin),
            (target, _, value) => _context.GetMedia((ElementNode)target).CrossOrigin = WebIdlConversions.ToDOMString(value));
        t.DefineProperty("playbackRate", (target, _) => ScriptValue.FromNumber(_context.GetMedia((ElementNode)target).PlaybackRate),
            (target, _, value) => { var state = _context.GetMedia((ElementNode)target); state.PlaybackRate = WebIdlConversions.ToDouble(value); state.Dispatch(state.OnRateChange, "ratechange"); });
        t.DefineProperty("defaultPlaybackRate", (target, _) => ScriptValue.FromNumber(_context.GetMedia((ElementNode)target).DefaultPlaybackRate),
            (target, _, value) => _context.GetMedia((ElementNode)target).DefaultPlaybackRate = WebIdlConversions.ToDouble(value));
        t.DefineProperty("preservesPitch", (target, _) => ScriptValue.FromBoolean(_context.GetMedia((ElementNode)target).PreservesPitch),
            (target, _, value) => _context.GetMedia((ElementNode)target).PreservesPitch = WebIdlConversions.ToBoolean(value));
        t.DefineProperty("readyState", (target, _) => ScriptValue.FromNumber(_context.GetMedia((ElementNode)target).ReadyState));
        t.DefineProperty("networkState", (target, _) => ScriptValue.FromNumber(_context.GetMedia((ElementNode)target).NetworkState));
        t.DefineProperty("error", (target, _) => _context.GetMedia((ElementNode)target).Error is { } error ? ScriptValue.FromString(error) : ScriptValue.Null);
        t.DefineProperty("onloadedmetadata", (target, _) => _context.GetMedia((ElementNode)target).OnLoadedMetadata,
            (target, _, value) => _context.GetMedia((ElementNode)target).OnLoadedMetadata = value);
        t.DefineProperty("onloadeddata", (target, _) => _context.GetMedia((ElementNode)target).OnLoadedData,
            (target, _, value) => _context.GetMedia((ElementNode)target).OnLoadedData = value);
        t.DefineProperty("oncanplay", (target, _) => _context.GetMedia((ElementNode)target).OnCanPlay,
            (target, _, value) => _context.GetMedia((ElementNode)target).OnCanPlay = value);
        t.DefineProperty("onplay", (target, _) => _context.GetMedia((ElementNode)target).OnPlay,
            (target, _, value) => _context.GetMedia((ElementNode)target).OnPlay = value);
        t.DefineProperty("onplaying", (target, _) => _context.GetMedia((ElementNode)target).OnPlaying,
            (target, _, value) => _context.GetMedia((ElementNode)target).OnPlaying = value);
        t.DefineProperty("onpause", (target, _) => _context.GetMedia((ElementNode)target).OnPause,
            (target, _, value) => _context.GetMedia((ElementNode)target).OnPause = value);
        t.DefineProperty("onended", (target, _) => _context.GetMedia((ElementNode)target).OnEnded,
            (target, _, value) => _context.GetMedia((ElementNode)target).OnEnded = value);
        t.DefineProperty("onvolumechange", (target, _) => _context.GetMedia((ElementNode)target).OnVolumeChange,
            (target, _, value) => _context.GetMedia((ElementNode)target).OnVolumeChange = value);
        t.DefineProperty("onerror", (target, _) => _context.GetMedia((ElementNode)target).OnError,
            (target, _, value) => _context.GetMedia((ElementNode)target).OnError = value);
        t.DefineProperty("ontimeupdate", (target, _) => _context.GetMedia((ElementNode)target).OnTimeUpdate,
            (target, _, value) => _context.GetMedia((ElementNode)target).OnTimeUpdate = value);
        t.DefineProperty("onratechange", (target, _) => _context.GetMedia((ElementNode)target).OnRateChange,
            (target, _, value) => _context.GetMedia((ElementNode)target).OnRateChange = value);
        t.DefineMethod("addEventListener", (target, _, args) => { if (args.Length > 1) _context.GetMedia((ElementNode)target).AddEventListener(WebIdlConversions.ToDOMString(args[0]), args[1]); return ScriptValue.Undefined; });
        t.DefineMethod("removeEventListener", (target, _, args) => { if (args.Length > 1) _context.GetMedia((ElementNode)target).RemoveEventListener(WebIdlConversions.ToDOMString(args[0]), args[1]); return ScriptValue.Undefined; });
        t.DefineProperty("buffered", (target, _) => CreateMediaRanges((ElementNode)target, buffered: true));
        t.DefineProperty("seekable", (target, _) => CreateMediaRanges((ElementNode)target, buffered: false));
        t.DefineProperty("played", (target, _) => CreateMediaRanges((ElementNode)target, buffered: null));
        t.DefineMethod("play", (target, _, _) =>
        {
            var state = _context.GetMedia((ElementNode)target);
            var started = state.Play();
            return started
                ? _context.Engine.Evaluate("Promise.resolve()")
                : _context.Engine.Evaluate($"Promise.reject(new DOMException('Media playback is unavailable.', '{(state.Error?.Contains("autoplay", StringComparison.OrdinalIgnoreCase) == true ? "NotAllowedError" : "NotSupportedError")}'))");
        });
        t.DefineMethod("pause", (target, _, _) => { _context.GetMedia((ElementNode)target).Pause(); return ScriptValue.Undefined; });
        t.DefineMethod("fastSeek", (target, _, args) =>
        {
            if (args.Length > 0) _context.GetMedia((ElementNode)target).Seek(Math.Round(WebIdlConversions.ToDouble(args[0])));
            return ScriptValue.Undefined;
        });
        t.DefineMethod("setSinkId", (target, _, args) => _context.Engine.Evaluate("Promise.resolve()"));
        t.DefineMethod("captureStream", (target, _, _) => _context.Engine.Evaluate("new MediaStream([])"));
        t.DefineMethod("load", (target, _, _) => { _context.GetMedia((ElementNode)target).Load(); return ScriptValue.Undefined; });
        t.DefineMethod("canPlayType", (target, _, args) =>
        {
            if (args.Length == 0) return ScriptValue.FromString("");
            return ScriptValue.FromString(_context.GetMedia((ElementNode)target).CanPlayType(WebIdlConversions.ToDOMString(args[0])));
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

    private void DispatchCueChange(MediaTextTrackState state)
    {
        var cueEvent = _context.Engine.CreateHostObject(MediaEventTemplate, new MediaEventState("cuechange"));
        if (state.OnCueChange.IsFunction) _context.Engine.Call(state.OnCueChange, ScriptValue.Undefined, [cueEvent]);
        foreach (var listener in state.CueChangeListeners.ToArray())
            if (listener.IsFunction) _context.Engine.Call(listener, ScriptValue.Undefined, [cueEvent]);
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
            .DefineProperty("document", (target, _) =>
            {
                var state = (IframeState)target;
                state.LoadInlineDocument();
                if (!state.CanAccessDocument) return ScriptValue.Null;
                return state.ChildDocument is { } child ? _context.Wrap(child) : ScriptValue.Null;
            })
            .DefineProperty("onmessage", (target, _) => ((IframeState)target).OnMessage, (target, _, value) => ((IframeState)target).OnMessage = value)
            .DefineMethod("addEventListener", (target, _, args) => { if (args.Length > 1) ((IframeState)target).AddMessageListener(args[1]); return ScriptValue.Undefined; })
            .DefineMethod("removeEventListener", (target, _, args) => { if (args.Length > 1) ((IframeState)target).RemoveMessageListener(args[1]); return ScriptValue.Undefined; })
            .DefineMethod("postMessage", (target, _, args) => { if (args.Length > 0) ((IframeState)target).PostMessage(args[0], args.Length > 1 ? WebIdlConversions.ToDOMString(args[1]) : "*", args.Length > 2 ? args[2] : ScriptValue.Undefined); return ScriptValue.Undefined; })
            .DefineMethod("close", (target, _, _) => { ((IframeState)target).Close(); return ScriptValue.Undefined; });
        var t = new HostObjectTemplate("HTMLIFrameElement", htmlElementProto);
        t.DefineProperty("src", (target, _) => ScriptValue.FromString(((ElementNode)target).GetAttribute("src") ?? ""),
            (target, _, value) => { var element = (ElementNode)target; element.SetAttribute("src", WebIdlConversions.ToDOMString(value)); _context.GetIframe(element).Invalidate(); });
        t.DefineProperty("srcdoc", (target, _) => ScriptValue.FromString(((ElementNode)target).GetAttribute("srcdoc") ?? ""),
            (target, _, value) => { var element = (ElementNode)target; element.SetAttribute("srcdoc", WebIdlConversions.ToDOMString(value)); _context.GetIframe(element).Invalidate(); });
        t.DefineProperty("sandbox", (target, _) => ScriptValue.FromString(((ElementNode)target).GetAttribute("sandbox") ?? ""),
            (target, _, value) =>
            {
                var element = (ElementNode)target;
                element.SetAttribute("sandbox", WebIdlConversions.ToDOMString(value));
                _context.GetIframe(element).Invalidate();
            });
        t.DefineProperty("allow", (target, _) => ScriptValue.FromString(((ElementNode)target).GetAttribute("allow") ?? ""),
            (target, _, value) => { var element = (ElementNode)target; element.SetAttribute("allow", WebIdlConversions.ToDOMString(value)); _context.GetIframe(element).Invalidate(); });
        t.DefineProperty("onload", (target, _) => _context.GetIframe((ElementNode)target).OnLoad,
            (target, _, value) => { var state = _context.GetIframe((ElementNode)target); state.ElementObject = _context.Wrap((ElementNode)target); state.OnLoad = value; });
        t.DefineProperty("onerror", (target, _) => _context.GetIframe((ElementNode)target).OnError,
            (target, _, value) => { var state = _context.GetIframe((ElementNode)target); state.ElementObject = _context.Wrap((ElementNode)target); state.OnError = value; });
        t.DefineProperty("contentWindow", (target, _) =>
        {
            _context.CheckCapability("Iframes", _context.Capabilities.Iframes);
            var state = _context.GetIframe((ElementNode)target);
            state.ElementObject = _context.Wrap((ElementNode)target);
            if (!state.Window.IsObject)
                state.Window = _context.Engine.CreateHostObject(windowTemplate, state);
            return state.Window;
        });
        t.DefineProperty("contentDocument", (target, _) =>
        {
            _context.CheckCapability("Iframes", _context.Capabilities.Iframes);
            var state = _context.GetIframe((ElementNode)target);
            state.ElementObject = _context.Wrap((ElementNode)target);
            state.LoadInlineDocument();
            if (!state.CanAccessDocument) return ScriptValue.Null;
            return state.ChildDocument is { } child ? _context.Wrap(child) : ScriptValue.Null;
        });
        return t;
    }

    private HostObjectTemplate CreateWebGlRenderingContextTemplate()
    {
        var t = new HostObjectTemplate("WebGL2RenderingContext");
        foreach (var (constantName, constantValue) in GlConstants.All.Concat(GlConstants.Generated()))
        {
            var constant = ScriptValue.FromNumber(constantValue);
            t.DefineProperty(constantName, (_, _) => constant);
        }
        t.DefineProperty("drawingBufferWidth", (target, _) => ScriptValue.FromNumber(ReadCanvasDimension(((WebGlContextState)target).Canvas, "width", 300)));
        t.DefineProperty("drawingBufferHeight", (target, _) => ScriptValue.FromNumber(ReadCanvasDimension(((WebGlContextState)target).Canvas, "height", 150)));
        t.DefineProperty("canvas", (target, _) => _context.WrapperCache.GetOrCreate(_context.Engine, HtmlCanvasElementTemplate, ((WebGlContextState)target).Canvas.Element));
        t.DefineProperty("oncontextlost", (target, _) => ((WebGlContextState)target).OnContextLost, (target, _, value) => ((WebGlContextState)target).OnContextLost = value);
        t.DefineProperty("oncontextrestored", (target, _) => ((WebGlContextState)target).OnContextRestored, (target, _, value) => ((WebGlContextState)target).OnContextRestored = value);
        t.DefineMethod("getParameter", (target, _, args) =>
        {
            if (args.Length == 0) return ScriptValue.Null;
            var state = (WebGlContextState)target;
            return WebIdlConversions.ToUint32(args[0]) switch
            {
                0x0D33 => ScriptValue.FromNumber(4096),
                0x8869 => ScriptValue.FromNumber(16),
                0x8B4D => ScriptValue.FromNumber(16),
                0x1F02 => ScriptValue.FromString("WebGL 2.0 Folio"),
                0x1F00 => ScriptValue.FromString("Folio"),
                0x1F01 => ScriptValue.FromString("Folio bounded renderer"),
                0x8B8C => ScriptValue.FromString("WebGL GLSL ES 3.00"),
                0x0BA2 => _context.Engine.Evaluate($"[{state.ViewportX}, {state.ViewportY}, {state.ViewportWidth}, {state.ViewportHeight}]"),
                0x0C10 => _context.Engine.Evaluate($"[{state.ScissorX}, {state.ScissorY}, {state.ScissorWidth}, {state.ScissorHeight}]"),
                0x80C9 => ScriptValue.FromNumber(state.BlendSourceFactor),
                0x80CB => ScriptValue.FromNumber(state.BlendSourceAlphaFactor),
                0x80C8 => ScriptValue.FromNumber(state.BlendDestinationFactor),
                0x80CA => ScriptValue.FromNumber(state.BlendDestinationAlphaFactor),
                0x8009 => ScriptValue.FromNumber(state.BlendEquation),
                0x883D => ScriptValue.FromNumber(state.BlendAlphaEquation),
                0x0C23 => _context.Engine.Evaluate($"[{state.ColorMaskRed.ToString().ToLowerInvariant()}, {state.ColorMaskGreen.ToString().ToLowerInvariant()}, {state.ColorMaskBlue.ToString().ToLowerInvariant()}, {state.ColorMaskAlpha.ToString().ToLowerInvariant()}]"),
                0x0B72 => ScriptValue.FromBoolean(state.DepthMask),
                0x0B71 => ScriptValue.FromBoolean(state.EnabledCapabilities.Contains(0x0B71)),
                0x0C11 => ScriptValue.FromBoolean(state.EnabledCapabilities.Contains(0x0C11)),
                0x0BE2 => ScriptValue.FromBoolean(state.EnabledCapabilities.Contains(0x0BE2)),
                0x8824 => ScriptValue.FromNumber(4),
                0x8825 => ScriptValue.FromNumber(state.DrawBuffers.Count > 0 ? state.DrawBuffers[0] : 0x0405),
                0x85B5 => state.BoundVertexArray is { } va ? ScriptValue.FromNumber(va) : ScriptValue.Null,
                0x8894 => state.BoundArrayBuffer is { } ab ? ScriptValue.FromNumber(ab) : ScriptValue.Null,
                0x8895 => state.BoundElementArrayBuffer is { } eb ? ScriptValue.FromNumber(eb) : ScriptValue.Null,
                0x8B8D => state.CurrentProgram is { } cp ? ScriptValue.FromNumber(cp) : ScriptValue.Null,
                0x9240 => ScriptValue.FromBoolean(state.UnpackFlipY),
                0x9241 => ScriptValue.FromBoolean(state.UnpackPremultiplyAlpha),
                0x0CF5 => ScriptValue.FromNumber(state.UnpackAlignment),
                0x0D05 => ScriptValue.FromNumber(state.PackAlignment),
                0x8CAA => state.BoundReadFramebuffer is { } rfb ? ScriptValue.FromNumber(rfb) : (state.BoundFramebuffer is { } fb ? ScriptValue.FromNumber(fb) : ScriptValue.Null),
                0x8CA6 => state.BoundDrawFramebuffer is { } dfb ? ScriptValue.FromNumber(dfb) : (state.BoundFramebuffer is { } fb2 ? ScriptValue.FromNumber(fb2) : ScriptValue.Null),
                0x8069 => state.BoundTexture is { } bt ? ScriptValue.FromNumber(bt) : ScriptValue.Null,
                0x8CA7 => state.BoundFramebuffer is { } fbb ? ScriptValue.FromNumber(fbb) : ScriptValue.Null,
                0x8D40 => state.BoundRenderbuffer is { } rbb ? ScriptValue.FromNumber(rbb) : ScriptValue.Null,
                0x0B74 => ScriptValue.FromNumber(state.DepthFunction),
                0x0B45 => ScriptValue.FromNumber(state.CullFaceMode),
                0x0B46 => ScriptValue.FromNumber(state.FrontFaceMode),
                0x0B21 => ScriptValue.FromNumber(state.LineWidth),
                0x0B73 => ScriptValue.FromNumber(state.ClearDepth),
                0x84E0 => ScriptValue.FromNumber(state.ActiveTextureUnit),
                0x0B70 => _context.Engine.CreateArray([ScriptValue.FromNumber(state.DepthRangeNear), ScriptValue.FromNumber(state.DepthRangeFar)]),
                0x0D3A => _context.Engine.CreateArray([ScriptValue.FromNumber(4096), ScriptValue.FromNumber(4096)]),
                0x846D => _context.Engine.CreateArray([ScriptValue.FromNumber(1), ScriptValue.FromNumber(1024)]),
                0x846E => _context.Engine.CreateArray([ScriptValue.FromNumber(1), ScriptValue.FromNumber(1)]),
                0x0C22 => _context.Engine.CreateArray([ScriptValue.FromNumber(state.ClearR), ScriptValue.FromNumber(state.ClearG), ScriptValue.FromNumber(state.ClearB), ScriptValue.FromNumber(state.ClearA)]),
                0x8DFB or 0x8DFD => ScriptValue.FromNumber(1024),
                0x8DFC => ScriptValue.FromNumber(30),
                0x8B4C or 0x8872 => ScriptValue.FromNumber(16),
                0x851C or 0x84E8 => ScriptValue.FromNumber(4096),
                0x0D52 or 0x0D53 or 0x0D54 => ScriptValue.FromNumber(8),
                0x0D55 => ScriptValue.FromNumber(state.Alpha ? 8 : 0),
                0x0D56 => ScriptValue.FromNumber(state.Depth ? 24 : 0),
                0x0D57 => ScriptValue.FromNumber(state.Stencil ? 8 : 0),
                0x80A9 => ScriptValue.FromNumber(state.Antialias ? 4 : 0),
                0x80A8 => ScriptValue.FromNumber(state.Antialias ? 1 : 0),
                0x0B44 or 0x0B90 or 0x8037 => ScriptValue.FromBoolean(state.EnabledCapabilities.Contains(WebIdlConversions.ToUint32(args[0]))),
                _ => ScriptValue.Null
            };
        });
        t.DefineMethod("getShaderPrecisionFormat", (_, _, _) => _context.Engine.Evaluate("({ rangeMin: 127, rangeMax: 127, precision: 23 })"));
        t.DefineMethod("viewport", (target, _, args) => { if (args.Length > 3) { var state = (WebGlContextState)target; state.ViewportX = WebIdlConversions.ToInt32(args[0]); state.ViewportY = WebIdlConversions.ToInt32(args[1]); state.ViewportWidth = Math.Max(0, WebIdlConversions.ToInt32(args[2])); state.ViewportHeight = Math.Max(0, WebIdlConversions.ToInt32(args[3])); } return ScriptValue.Undefined; });
        t.DefineMethod("scissor", (target, _, args) => { if (args.Length > 3) { var state = (WebGlContextState)target; state.ScissorX = WebIdlConversions.ToInt32(args[0]); state.ScissorY = WebIdlConversions.ToInt32(args[1]); state.ScissorWidth = Math.Max(0, WebIdlConversions.ToInt32(args[2])); state.ScissorHeight = Math.Max(0, WebIdlConversions.ToInt32(args[3])); } return ScriptValue.Undefined; });
        t.DefineMethod("getSupportedExtensions", (target, _, _) => _context.Engine.CreateArray([ScriptValue.FromString("WEBGL_lose_context")]));
        t.DefineMethod("getContextAttributes", (target, _, _) =>
        {
            var state = (WebGlContextState)target;
            return _context.Engine.Evaluate($$"""
                ({
                    alpha: {{(state.Alpha ? "true" : "false")}},
                    depth: {{(state.Depth ? "true" : "false")}},
                    stencil: {{(state.Stencil ? "true" : "false")}},
                    antialias: {{(state.Antialias ? "true" : "false")}},
                    premultipliedAlpha: {{(state.PremultipliedAlpha ? "true" : "false")}},
                    preserveDrawingBuffer: {{(state.PreserveDrawingBuffer ? "true" : "false")}}
                })
                """);
        });
        t.DefineMethod("pixelStorei", (target, _, args) =>
        {
            var state = (WebGlContextState)target;
            if (args.Length < 2) { state.SetError(0x0502); return ScriptValue.Undefined; }
            var pname = WebIdlConversions.ToUint32(args[0]);
            var param = WebIdlConversions.ToInt32(args[1]);
            switch (pname)
            {
                case 0x9240:
                    state.UnpackFlipY = param != 0;
                    break;
                case 0x9241:
                    state.UnpackPremultiplyAlpha = param != 0;
                    break;
                case 0x0CF5:
                    if (param is 1 or 2 or 4 or 8) state.UnpackAlignment = param;
                    else state.SetError(0x0501);
                    break;
                case 0x0D05:
                    if (param is 1 or 2 or 4 or 8) state.PackAlignment = param;
                    else state.SetError(0x0501);
                    break;
                case 0x9243:
                    state.UnpackColorspaceConversion = (uint)param;
                    break;
                default:
                    break;
            }
            return ScriptValue.Undefined;
        });
        t.DefineMethod("createBuffer", (target, _, _) => { var state = (WebGlContextState)target; var handle = state.CreateHandle(); state.Buffers[handle] = []; return ScriptValue.FromNumber(handle); });
        t.DefineMethod("bindBuffer", (target, _, args) => { var state = (WebGlContextState)target; var binding = args.Length > 1 && !args[1].IsNullOrUndefined ? (uint?)WebIdlConversions.ToUint32(args[1]) : null; if (args.Length > 0 && WebIdlConversions.ToUint32(args[0]) == 0x8893) state.BoundElementArrayBuffer = binding; else state.BoundArrayBuffer = binding; return ScriptValue.Undefined; });
        t.DefineMethod("bufferData", (target, _, args) =>
        {
            var state = (WebGlContextState)target;
            var handle = args.Length > 0 && WebIdlConversions.ToUint32(args[0]) == 0x8893 ? state.BoundElementArrayBuffer : state.BoundArrayBuffer;
            if (handle is not { } bufferHandle || args.Length < 2) { state.SetError(0x0502); return ScriptValue.Undefined; }
            const int MaxBufferBytes = 16 * 1024 * 1024;
            byte[]? data = null;
            int size;
            if (args[1].IsNumber)
            {
                size = WebIdlConversions.ToInt32(args[1]);
                if (size < 0) { state.SetError(0x0501); return ScriptValue.Undefined; }
            }
            else if (TryTypedBytes(_context.Engine, args[1], out var typed))
            {
                data = typed;
                size = typed.Length;
            }
            else if (_context.Engine.TryGetArrayItems(args[1], out var items))
            {
                data = new byte[items.Count * 4];
                for (var i = 0; i < items.Count; i++) BitConverter.TryWriteBytes(data.AsSpan(i * 4, 4), (float)WebIdlConversions.ToDouble(items[i]));
                size = data.Length;
            }
            else { state.SetError(0x0501); return ScriptValue.Undefined; }
            size = Math.Min(size, MaxBufferBytes);
            if (state.ReplaceBuffer(bufferHandle, size) && data is not null && state.Buffers.TryGetValue(bufferHandle, out var buffer))
                Array.Copy(data, buffer, Math.Min(size, data.Length));
            return ScriptValue.Undefined;
        });
        t.DefineMethod("bufferSubData", (target, _, args) =>
        {
            var state = (WebGlContextState)target;
            var handle = args.Length > 0 && WebIdlConversions.ToUint32(args[0]) == 0x8893 ? state.BoundElementArrayBuffer : state.BoundArrayBuffer;
            if (handle is not { } bufferHandle || args.Length < 3 || !state.Buffers.TryGetValue(bufferHandle, out var buffer)) { state.SetError(0x0502); return ScriptValue.Undefined; }
            var offset = WebIdlConversions.ToInt32(args[1]);
            if (offset < 0) { state.SetError(0x0501); return ScriptValue.Undefined; }
            if (args[2].IsNumber)
            {
                var sizeBytes = Math.Max(0, WebIdlConversions.ToInt32(args[2]));
                if (offset > buffer.Length || sizeBytes > buffer.Length - offset) state.SetError(0x0501);
                return ScriptValue.Undefined;
            }
            byte[] data;
            if (TryTypedBytes(_context.Engine, args[2], out var typed)) data = typed;
            else if (_context.Engine.TryGetArrayItems(args[2], out var items))
            {
                data = new byte[items.Count * 4];
                for (var i = 0; i < items.Count; i++) BitConverter.TryWriteBytes(data.AsSpan(i * 4, 4), (float)WebIdlConversions.ToDouble(items[i]));
            }
            else { state.SetError(0x0502); return ScriptValue.Undefined; }
            if (offset > buffer.Length || data.Length > buffer.Length - offset) { state.SetError(0x0501); return ScriptValue.Undefined; }
            Array.Copy(data, 0, buffer, offset, data.Length);
            return ScriptValue.Undefined;
        });
        t.DefineMethod("getBufferSubData", (target, _, args) => { var state = (WebGlContextState)target; if (state.BoundArrayBuffer is not { } handle || args.Length < 3 || !state.Buffers.TryGetValue(handle, out var buffer) || !_context.Engine.TryGetArrayItems(args[2], out var destination)) { state.SetError(0x0502); return ScriptValue.Undefined; } var offset = Math.Max(0, WebIdlConversions.ToInt32(args[1])); if (offset > buffer.Length || destination.Count > buffer.Length - offset) { state.SetError(0x0501); return ScriptValue.Undefined; } for (var i = 0; i < destination.Count; i++) _context.Engine.TrySetArrayItem(args[2], i, ScriptValue.FromNumber(buffer[offset + i])); return ScriptValue.Undefined; });
        t.DefineMethod("deleteBuffer", (target, _, args) => { if (args.Length > 0) ((WebGlContextState)target).DeleteBuffer(WebIdlConversions.ToUint32(args[0])); return ScriptValue.Undefined; });
        t.DefineMethod("isBuffer", (target, _, args) => ScriptValue.FromBoolean(args.Length > 0 && ((WebGlContextState)target).Buffers.ContainsKey(WebIdlConversions.ToUint32(args[0]))));
        t.DefineMethod("createShader", (target, _, args) => { var state = (WebGlContextState)target; var handle = state.CreateHandle(); state.Shaders[handle] = (args.Length > 0 ? WebIdlConversions.ToInt32(args[0]) : 0, "", false, ""); return ScriptValue.FromNumber(handle); });
        t.DefineMethod("shaderSource", (target, _, args) =>
        {
            var state = (WebGlContextState)target;
            if (args.Length > 1)
            {
                var handle = WebIdlConversions.ToUint32(args[0]);
                var type = state.Shaders.TryGetValue(handle, out var s) ? s.Type : 0;
                state.Shaders[handle] = (type, WebIdlConversions.ToDOMString(args[1]), false, "");
            }
            return ScriptValue.Undefined;
        });
        t.DefineMethod("compileShader", (target, _, args) =>
        {
            var state = (WebGlContextState)target;
            if (args.Length == 0) { state.SetError(0x0502); return ScriptValue.Undefined; }
            var handle = WebIdlConversions.ToUint32(args[0]);
            if (!state.Shaders.ContainsKey(handle)) { state.SetError(0x0501); return ScriptValue.Undefined; }
            state.CompileShader(handle);
            return ScriptValue.Undefined;
        });
        t.DefineMethod("deleteShader", (target, _, args) => { if (args.Length > 0) ((WebGlContextState)target).DeleteShader(WebIdlConversions.ToUint32(args[0])); return ScriptValue.Undefined; });
        t.DefineMethod("isShader", (target, _, args) => ScriptValue.FromBoolean(args.Length > 0 && ((WebGlContextState)target).Shaders.ContainsKey(WebIdlConversions.ToUint32(args[0]))));
        t.DefineMethod("getShaderParameter", (target, _, args) =>
        {
            if (args.Length > 1 && ((WebGlContextState)target).Shaders.TryGetValue(WebIdlConversions.ToUint32(args[0]), out var shader))
            {
                return WebIdlConversions.ToUint32(args[1]) switch
                {
                    0x8B81 => ScriptValue.FromBoolean(shader.Compiled),
                    0x8B4F => ScriptValue.FromNumber(shader.Type),
                    0x8B80 => ScriptValue.FromBoolean(false),
                    _ => ScriptValue.Null
                };
            }
            return ScriptValue.Null;
        });
        t.DefineMethod("getShaderInfoLog", (target, _, args) =>
        {
            if (args.Length > 0 && ((WebGlContextState)target).Shaders.TryGetValue(WebIdlConversions.ToUint32(args[0]), out var shader))
                return ScriptValue.FromString(shader.InfoLog);
            return ScriptValue.FromString("");
        });
        t.DefineMethod("getShaderSource", (target, _, args) => args.Length > 0 && ((WebGlContextState)target).Shaders.TryGetValue(WebIdlConversions.ToUint32(args[0]), out var shader) ? ScriptValue.FromString(shader.Source) : ScriptValue.Null);
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
            if (args.Length == 0) { state.SetError(0x0502); return ScriptValue.Undefined; }
            state.LinkProgram(WebIdlConversions.ToUint32(args[0]));
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
        t.DefineMethod("getProgramParameter", (target, _, args) =>
        {
            var state = (WebGlContextState)target;
            if (args.Length > 1)
            {
                var program = WebIdlConversions.ToUint32(args[0]);
                return WebIdlConversions.ToUint32(args[1]) switch
                {
                    0x8B82 => ScriptValue.FromBoolean(state.LinkedPrograms.Contains(program)),
                    0x8B80 => ScriptValue.FromBoolean(false),
                    0x8B85 => ScriptValue.FromNumber(state.ProgramShaders.TryGetValue(program, out var s) ? s.Count : 0),
                    0x8B86 => ScriptValue.FromNumber(state.LinkedProgramObjects.TryGetValue(program, out var linkedUniforms) ? linkedUniforms.Uniforms.Count : 0),
                    0x8B83 => ScriptValue.FromBoolean(state.LinkedPrograms.Contains(program)),
                    0x8B89 => ScriptValue.FromNumber(state.LinkedProgramObjects.TryGetValue(program, out var linkedAttributes) ? linkedAttributes.Attributes.Count : 0),
                    _ => ScriptValue.Null
                };
            }
            return ScriptValue.Null;
        });
        t.DefineMethod("getProgramInfoLog", (target, _, args) =>
        {
            if (args.Length > 0 && ((WebGlContextState)target).ProgramInfoLogs.TryGetValue(WebIdlConversions.ToUint32(args[0]), out var log))
                return ScriptValue.FromString(log);
            return ScriptValue.FromString("");
        });
        t.DefineMethod("getAttachedShaders", (target, _, args) => { var state = (WebGlContextState)target; var shaders = args.Length > 0 && state.ProgramShaders.TryGetValue(WebIdlConversions.ToUint32(args[0]), out var attached) ? attached : []; return _context.Engine.CreateArray(shaders.Select(handle => ScriptValue.FromNumber(handle)).ToArray()); });
        t.DefineMethod("useProgram", (target, _, args) => { ((WebGlContextState)target).CurrentProgram = args.Length > 0 && !args[0].IsNullOrUndefined ? WebIdlConversions.ToUint32(args[0]) : null; return ScriptValue.Undefined; });
        t.DefineMethod("bindAttribLocation", (target, _, args) =>
        {
            var state = (WebGlContextState)target;
            if (args.Length < 3) { state.SetError(0x0502); return ScriptValue.Undefined; }
            var program = WebIdlConversions.ToUint32(args[0]);
            var index = WebIdlConversions.ToInt32(args[1]);
            var name = WebIdlConversions.ToDOMString(args[2]);
            if (index < 0 || index >= 16) { state.SetError(0x0501); return ScriptValue.Undefined; }
            if (name.StartsWith("gl_", StringComparison.Ordinal)) { state.SetError(0x0502); return ScriptValue.Undefined; }
            if (!state.BoundAttributes.TryGetValue(program, out var bound)) state.BoundAttributes[program] = bound = [];
            bound[name] = index;
            return ScriptValue.Undefined;
        });
        t.DefineMethod("getAttribLocation", (target, _, args) =>
        {
            var state = (WebGlContextState)target;
            if (args.Length < 2 || !state.LinkedProgramObjects.TryGetValue(WebIdlConversions.ToUint32(args[0]), out var program)) return ScriptValue.FromNumber(-1);
            var name = WebIdlConversions.ToDOMString(args[1]);
            return ScriptValue.FromNumber(program.Attributes.FirstOrDefault(a => a.Name == name)?.Location ?? -1);
        });
        t.DefineMethod("getUniformLocation", (target, _, args) =>
        {
            var state = (WebGlContextState)target;
            if (args.Length < 2 || !state.LinkedProgramObjects.TryGetValue(WebIdlConversions.ToUint32(args[0]), out var program)) return ScriptValue.Null;
            var uniform = program.FindUniform(WebIdlConversions.ToDOMString(args[1]), out var element);
            // Locations count from one so that none is the falsy 0.
            return uniform is null ? ScriptValue.Null : ScriptValue.FromNumber(uniform.Location + element + 1);
        });
        t.DefineMethod("getActiveUniform", (target, _, args) =>
        {
            var state = (WebGlContextState)target;
            if (args.Length < 2 || !state.LinkedProgramObjects.TryGetValue(WebIdlConversions.ToUint32(args[0]), out var program)) { state.SetError(0x0501); return ScriptValue.Null; }
            var index = WebIdlConversions.ToInt32(args[1]);
            if (index < 0 || index >= program.Uniforms.Count) { state.SetError(0x0501); return ScriptValue.Null; }
            var uniform = program.Uniforms[index];
            return _context.Engine.Evaluate($"({{ name: {System.Text.Json.JsonSerializer.Serialize(uniform.Name)}, size: {uniform.Size}, type: {GlLinkedProgram.TypeEnum(uniform.Type)} }})");
        });
        t.DefineMethod("getActiveAttrib", (target, _, args) =>
        {
            var state = (WebGlContextState)target;
            if (args.Length < 2 || !state.LinkedProgramObjects.TryGetValue(WebIdlConversions.ToUint32(args[0]), out var program)) { state.SetError(0x0501); return ScriptValue.Null; }
            var index = WebIdlConversions.ToInt32(args[1]);
            if (index < 0 || index >= program.Attributes.Count) { state.SetError(0x0501); return ScriptValue.Null; }
            var attribute = program.Attributes[index];
            return _context.Engine.Evaluate($"({{ name: {System.Text.Json.JsonSerializer.Serialize(attribute.Name)}, size: 1, type: {GlLinkedProgram.TypeEnum(attribute.Type)} }})");
        });
        t.DefineMethod("validateProgram", (_, _, _) => ScriptValue.Undefined);
        t.DefineMethod("enableVertexAttribArray", (target, _, args) => { if (args.Length > 0) ((WebGlContextState)target).EnabledVertexAttributes.Add(WebIdlConversions.ToInt32(args[0])); return ScriptValue.Undefined; });
        t.DefineMethod("disableVertexAttribArray", (target, _, args) => { if (args.Length > 0) ((WebGlContextState)target).EnabledVertexAttributes.Remove(WebIdlConversions.ToInt32(args[0])); return ScriptValue.Undefined; });
        t.DefineMethod("vertexAttribPointer", (target, _, args) => { var state = (WebGlContextState)target; if (args.Length < 6) { state.SetError(0x0502); return ScriptValue.Undefined; } var index = WebIdlConversions.ToInt32(args[0]); var size = WebIdlConversions.ToInt32(args[1]); if (index < 0 || size < 1 || size > 4) { state.SetError(0x0501); return ScriptValue.Undefined; } state.VertexAttribPointers[index] = (size, WebIdlConversions.ToUint32(args[2]), WebIdlConversions.ToBoolean(args[3]), WebIdlConversions.ToInt32(args[4]), WebIdlConversions.ToInt32(args[5])); if (state.BoundArrayBuffer is { } buffer) state.VertexAttribBuffers[index] = buffer; else state.VertexAttribBuffers.Remove(index); return ScriptValue.Undefined; });
        t.DefineMethod("vertexAttribDivisor", (target, _, args) => { if (args.Length > 1) ((WebGlContextState)target).VertexAttribDivisors[WebIdlConversions.ToInt32(args[0])] = WebIdlConversions.ToUint32(args[1]); return ScriptValue.Undefined; });
        t.DefineMethod("getVertexAttrib", (target, _, args) =>
        {
            var state = (WebGlContextState)target;
            if (args.Length < 2) return ScriptValue.Null;
            var index = WebIdlConversions.ToInt32(args[0]);
            var pname = WebIdlConversions.ToUint32(args[1]);
            var enabled = state.EnabledVertexAttributes.Contains(index);
            if (pname == 0x8622) return ScriptValue.FromBoolean(enabled);
            if (pname == 0x88FE) return ScriptValue.FromNumber(state.VertexAttribDivisors.TryGetValue(index, out var divisor) ? divisor : 0);
            if (pname == 0x889F) return ScriptValue.FromNumber(state.VertexAttribBuffers.TryGetValue(index, out var bufferHandle) ? bufferHandle : 0);
            if (!state.VertexAttribPointers.TryGetValue(index, out var pointer)) return pname == 0x8623 ? ScriptValue.FromNumber(4) : ScriptValue.FromNumber(0);
            return pname switch
            {
                0x8623 => ScriptValue.FromNumber(pointer.Size),
                0x8624 => ScriptValue.FromNumber(pointer.Stride),
                0x8625 => ScriptValue.FromNumber(pointer.Type),
                0x886A => ScriptValue.FromBoolean(pointer.Normalized),
                _ => ScriptValue.Null
            };
        });
        for (var vertexAttribComponents = 1; vertexAttribComponents <= 4; vertexAttribComponents++)
        {
            var components = vertexAttribComponents;
            t.DefineMethod($"vertexAttrib{components}f", (target, _, args) =>
            {
                var state = (WebGlContextState)target;
                if (args.Length < components + 1) { state.SetError(0x0502); return ScriptValue.Undefined; }
                var index = WebIdlConversions.ToInt32(args[0]);
                if (index < 0 || index >= 16) { state.SetError(0x0501); return ScriptValue.Undefined; }
                var v = new System.Numerics.Vector4(0, 0, 0, 1);
                for (var i = 0; i < components; i++) SetLane(ref v, i, (float)WebIdlConversions.ToDouble(args[i + 1]));
                state.GenericAttributes[index] = v;
                return ScriptValue.Undefined;
            });
            t.DefineMethod($"vertexAttrib{components}fv", (target, _, args) =>
            {
                var state = (WebGlContextState)target;
                if (args.Length < 2) { state.SetError(0x0502); return ScriptValue.Undefined; }
                var index = WebIdlConversions.ToInt32(args[0]);
                if (index < 0 || index >= 16 || !_context.Engine.TryGetArrayItems(args[1], out var items) || items.Count < components) { state.SetError(0x0501); return ScriptValue.Undefined; }
                var v = new System.Numerics.Vector4(0, 0, 0, 1);
                for (var i = 0; i < components; i++) SetLane(ref v, i, (float)WebIdlConversions.ToDouble(items[i]));
                state.GenericAttributes[index] = v;
                return ScriptValue.Undefined;
            });
            t.DefineMethod($"uniform{components}f", (target, _, args) => UniformScalars((WebGlContextState)target, args, components, integer: false));
            t.DefineMethod($"uniform{components}i", (target, _, args) => UniformScalars((WebGlContextState)target, args, components, integer: true));
            t.DefineMethod($"uniform{components}fv", (target, _, args) => UniformVector(_context.Engine, (WebGlContextState)target, args, components, integer: false));
            t.DefineMethod($"uniform{components}iv", (target, _, args) => UniformVector(_context.Engine, (WebGlContextState)target, args, components, integer: true));
        }
        for (var matrixSize = 2; matrixSize <= 4; matrixSize++)
        {
            var size = matrixSize;
            t.DefineMethod($"uniformMatrix{size}fv", (target, _, args) => UniformMatrix(_context.Engine, (WebGlContextState)target, args, size));
        }
        t.DefineMethod("getUniform", (target, _, args) =>
        {
            var state = (WebGlContextState)target;
            if (args.Length < 2 || !state.LinkedProgramObjects.TryGetValue(WebIdlConversions.ToUint32(args[0]), out var program) || args[1].IsNullOrUndefined) { state.SetError(0x0502); return ScriptValue.Null; }
            if (!program.TryGetUniform(WebIdlConversions.ToInt32(args[1]) - 1, out var uniform, out var element)) { state.SetError(0x0502); return ScriptValue.Null; }
            var values = state.ReadUniform(program, uniform, element);
            var type = uniform.Type;
            if (type.IsScalar || type.IsSampler)
                return type.Base == GlslBase.Bool ? ScriptValue.FromBoolean(values[0] != 0) : ScriptValue.FromNumber(values[0]);
            return _context.Engine.CreateArray(values.Select(v => type.ScalarKind == GlslBase.Bool ? ScriptValue.FromBoolean(v != 0) : ScriptValue.FromNumber(v)).ToArray());
        });
        t.DefineMethod("cullFace", (target, _, args) => { if (args.Length > 0) ((WebGlContextState)target).CullFaceMode = WebIdlConversions.ToUint32(args[0]); return ScriptValue.Undefined; });
        t.DefineMethod("frontFace", (target, _, args) => { if (args.Length > 0) ((WebGlContextState)target).FrontFaceMode = WebIdlConversions.ToUint32(args[0]); return ScriptValue.Undefined; });
        t.DefineMethod("depthRange", (target, _, args) => { if (args.Length > 1) { var state = (WebGlContextState)target; state.DepthRangeNear = Math.Clamp((float)WebIdlConversions.ToDouble(args[0]), 0, 1); state.DepthRangeFar = Math.Clamp((float)WebIdlConversions.ToDouble(args[1]), 0, 1); } return ScriptValue.Undefined; });
        t.DefineMethod("blendColor", (target, _, args) => { if (args.Length > 3) ((WebGlContextState)target).BlendColor = new System.Numerics.Vector4((float)WebIdlConversions.ToDouble(args[0]), (float)WebIdlConversions.ToDouble(args[1]), (float)WebIdlConversions.ToDouble(args[2]), (float)WebIdlConversions.ToDouble(args[3])); return ScriptValue.Undefined; });
        t.DefineMethod("stencilMask", (target, _, args) => { if (args.Length > 0) ((WebGlContextState)target).StencilWriteMask = WebIdlConversions.ToUint32(args[0]); return ScriptValue.Undefined; });
        t.DefineMethod("stencilFuncSeparate", (target, _, args) => { if (args.Length > 3 && WebIdlConversions.ToUint32(args[0]) is 0x0404 or 0x0408) { var state = (WebGlContextState)target; state.StencilFunction = WebIdlConversions.ToUint32(args[1]); state.StencilReference = WebIdlConversions.ToInt32(args[2]); state.StencilMask = WebIdlConversions.ToUint32(args[3]); } return ScriptValue.Undefined; });
        t.DefineMethod("stencilOpSeparate", (target, _, args) => { if (args.Length > 3 && WebIdlConversions.ToUint32(args[0]) is 0x0404 or 0x0408) { var state = (WebGlContextState)target; state.StencilFail = WebIdlConversions.ToUint32(args[1]); state.StencilDepthFail = WebIdlConversions.ToUint32(args[2]); state.StencilDepthPass = WebIdlConversions.ToUint32(args[3]); } return ScriptValue.Undefined; });
        t.DefineMethod("stencilMaskSeparate", (target, _, args) => { if (args.Length > 1 && WebIdlConversions.ToUint32(args[0]) is 0x0404 or 0x0408) ((WebGlContextState)target).StencilWriteMask = WebIdlConversions.ToUint32(args[1]); return ScriptValue.Undefined; });
        t.DefineMethod("lineWidth", (target, _, args) => { if (args.Length > 0) ((WebGlContextState)target).LineWidth = (float)WebIdlConversions.ToDouble(args[0]); return ScriptValue.Undefined; });
        t.DefineMethod("polygonOffset", (target, _, args) => { if (args.Length > 1) { var state = (WebGlContextState)target; state.PolygonOffsetFactor = (float)WebIdlConversions.ToDouble(args[0]); state.PolygonOffsetUnits = (float)WebIdlConversions.ToDouble(args[1]); } return ScriptValue.Undefined; });
        t.DefineMethod("isEnabled", (target, _, args) => ScriptValue.FromBoolean(args.Length > 0 && ((WebGlContextState)target).EnabledCapabilities.Contains(WebIdlConversions.ToUint32(args[0]))));
        t.DefineMethod("sampleCoverage", (_, _, _) => ScriptValue.Undefined);
        t.DefineMethod("hint", (_, _, _) => ScriptValue.Undefined);
        t.DefineMethod("flush", (_, _, _) => ScriptValue.Undefined);
        t.DefineMethod("finish", (_, _, _) => ScriptValue.Undefined);
        t.DefineMethod("enable", (target, _, args) => { if (args.Length > 0) ((WebGlContextState)target).EnabledCapabilities.Add(WebIdlConversions.ToUint32(args[0])); return ScriptValue.Undefined; });
        t.DefineMethod("disable", (target, _, args) => { if (args.Length > 0) ((WebGlContextState)target).EnabledCapabilities.Remove(WebIdlConversions.ToUint32(args[0])); return ScriptValue.Undefined; });
        t.DefineMethod("clearDepth", (target, _, args) => { if (args.Length > 0) ((WebGlContextState)target).ClearDepth = Math.Clamp((float)WebIdlConversions.ToDouble(args[0]), 0, 1); return ScriptValue.Undefined; });
        t.DefineMethod("depthFunc", (target, _, args) => { if (args.Length > 0) ((WebGlContextState)target).DepthFunction = WebIdlConversions.ToUint32(args[0]); return ScriptValue.Undefined; });
        t.DefineMethod("depthMask", (target, _, args) => { if (args.Length > 0) ((WebGlContextState)target).DepthMask = WebIdlConversions.ToBoolean(args[0]); return ScriptValue.Undefined; });
        t.DefineMethod("blendFunc", (target, _, args) => { if (args.Length > 1) { var state = (WebGlContextState)target; var source = WebIdlConversions.ToUint32(args[0]); var destination = WebIdlConversions.ToUint32(args[1]); state.BlendSourceFactor = source; state.BlendDestinationFactor = destination; state.BlendSourceAlphaFactor = source; state.BlendDestinationAlphaFactor = destination; } return ScriptValue.Undefined; });
        t.DefineMethod("blendFuncSeparate", (target, _, args) => { if (args.Length > 3) { var state = (WebGlContextState)target; state.BlendSourceFactor = WebIdlConversions.ToUint32(args[0]); state.BlendDestinationFactor = WebIdlConversions.ToUint32(args[1]); state.BlendSourceAlphaFactor = WebIdlConversions.ToUint32(args[2]); state.BlendDestinationAlphaFactor = WebIdlConversions.ToUint32(args[3]); } return ScriptValue.Undefined; });
        t.DefineMethod("blendEquation", (target, _, args) => { if (args.Length > 0) { var state = (WebGlContextState)target; var equation = WebIdlConversions.ToUint32(args[0]); state.BlendEquation = equation; state.BlendAlphaEquation = equation; } return ScriptValue.Undefined; });
        t.DefineMethod("blendEquationSeparate", (target, _, args) => { if (args.Length > 1) { var state = (WebGlContextState)target; state.BlendEquation = WebIdlConversions.ToUint32(args[0]); state.BlendAlphaEquation = WebIdlConversions.ToUint32(args[1]); } return ScriptValue.Undefined; });
        t.DefineMethod("colorMask", (target, _, args) => { if (args.Length > 3) { var state = (WebGlContextState)target; state.ColorMaskRed = WebIdlConversions.ToBoolean(args[0]); state.ColorMaskGreen = WebIdlConversions.ToBoolean(args[1]); state.ColorMaskBlue = WebIdlConversions.ToBoolean(args[2]); state.ColorMaskAlpha = WebIdlConversions.ToBoolean(args[3]); } return ScriptValue.Undefined; });
        t.DefineMethod("stencilFunc", (target, _, args) => { if (args.Length > 2) { var state = (WebGlContextState)target; state.StencilFunction = WebIdlConversions.ToUint32(args[0]); state.StencilReference = WebIdlConversions.ToInt32(args[1]); state.StencilMask = WebIdlConversions.ToUint32(args[2]); } return ScriptValue.Undefined; });
        t.DefineMethod("stencilOp", (target, _, args) => { if (args.Length > 2) { var state = (WebGlContextState)target; state.StencilFail = WebIdlConversions.ToUint32(args[0]); state.StencilDepthFail = WebIdlConversions.ToUint32(args[1]); state.StencilDepthPass = WebIdlConversions.ToUint32(args[2]); } return ScriptValue.Undefined; });
        t.DefineMethod("clearStencil", (target, _, args) => { if (args.Length > 0) ((WebGlContextState)target).ClearStencil = WebIdlConversions.ToInt32(args[0]); return ScriptValue.Undefined; });
        t.DefineMethod("drawArrays", (target, _, args) => { if (args.Length > 2) { var state = (WebGlContextState)target; state.DrawArrays(WebIdlConversions.ToUint32(args[0]), WebIdlConversions.ToInt32(args[1]), WebIdlConversions.ToInt32(args[2])); } return ScriptValue.Undefined; });
        t.DefineMethod("drawElements", (target, _, args) => { if (args.Length > 3) { var state = (WebGlContextState)target; state.DrawElements(WebIdlConversions.ToUint32(args[0]), WebIdlConversions.ToUint32(args[2]), WebIdlConversions.ToInt32(args[1]), WebIdlConversions.ToInt32(args[3])); } return ScriptValue.Undefined; });
        t.DefineMethod("drawArraysInstanced", (target, _, args) => { if (args.Length > 3) { var state = (WebGlContextState)target; state.DrawArraysInstanced(WebIdlConversions.ToUint32(args[0]), WebIdlConversions.ToInt32(args[1]), WebIdlConversions.ToInt32(args[2]), WebIdlConversions.ToInt32(args[3])); } return ScriptValue.Undefined; });
        t.DefineMethod("drawElementsInstanced", (target, _, args) => { if (args.Length > 4) { var state = (WebGlContextState)target; state.DrawElementsInstanced(WebIdlConversions.ToUint32(args[0]), WebIdlConversions.ToUint32(args[2]), WebIdlConversions.ToInt32(args[1]), WebIdlConversions.ToInt32(args[3]), WebIdlConversions.ToInt32(args[4])); } return ScriptValue.Undefined; });
        t.DefineMethod("fenceSync", (target, _, args) => { var state = (WebGlContextState)target; if (args.Length < 2 || WebIdlConversions.ToUint32(args[0]) != 0x9117) { state.SetError(0x0500); return ScriptValue.Null; } var handle = state.CreateHandle(); state.Syncs.Add(handle); return ScriptValue.FromNumber(handle); });
        t.DefineMethod("clientWaitSync", (target, _, args) => { var state = (WebGlContextState)target; return args.Length > 0 && state.Syncs.Contains(WebIdlConversions.ToUint32(args[0])) ? ScriptValue.FromNumber(0x911C) : ScriptValue.FromNumber(0x911D); });
        t.DefineMethod("deleteSync", (target, _, args) => { if (args.Length > 0) ((WebGlContextState)target).Syncs.Remove(WebIdlConversions.ToUint32(args[0])); return ScriptValue.Undefined; });
        t.DefineMethod("isSync", (target, _, args) => ScriptValue.FromBoolean(args.Length > 0 && ((WebGlContextState)target).Syncs.Contains(WebIdlConversions.ToUint32(args[0]))));
        t.DefineMethod("createQuery", (target, _, _) => { var state = (WebGlContextState)target; var handle = state.CreateHandle(); state.Queries.Add(handle); state.QueryResults[handle] = false; return ScriptValue.FromNumber(handle); });
        t.DefineMethod("beginQuery", (target, _, args) => { var state = (WebGlContextState)target; if (args.Length < 2 || !state.Queries.Contains(WebIdlConversions.ToUint32(args[1]))) { state.SetError(0x0502); return ScriptValue.Undefined; } state.ActiveQuery = WebIdlConversions.ToUint32(args[1]); state.QueryResults[state.ActiveQuery.Value] = false; return ScriptValue.Undefined; });
        t.DefineMethod("endQuery", (target, _, args) => { var state = (WebGlContextState)target; if (args.Length == 0 || state.ActiveQuery is not { } query) { state.SetError(0x0502); return ScriptValue.Undefined; } state.QueryResults[query] = true; state.ActiveQuery = null; return ScriptValue.Undefined; });
        t.DefineMethod("deleteQuery", (target, _, args) => { if (args.Length > 0) { var state = (WebGlContextState)target; var handle = WebIdlConversions.ToUint32(args[0]); state.Queries.Remove(handle); state.QueryResults.Remove(handle); if (state.ActiveQuery == handle) state.ActiveQuery = null; } return ScriptValue.Undefined; });
        t.DefineMethod("isQuery", (target, _, args) => ScriptValue.FromBoolean(args.Length > 0 && ((WebGlContextState)target).Queries.Contains(WebIdlConversions.ToUint32(args[0]))));
        t.DefineMethod("getQuery", (target, _, args) => { var state = (WebGlContextState)target; if (args.Length < 2 || !state.Queries.Contains(WebIdlConversions.ToUint32(args[0]))) return ScriptValue.Null; return ScriptValue.FromNumber(0); });
        t.DefineMethod("getQueryParameter", (target, _, args) => { var state = (WebGlContextState)target; if (args.Length < 2 || !state.QueryResults.TryGetValue(WebIdlConversions.ToUint32(args[0]), out var available)) return ScriptValue.Null; return WebIdlConversions.ToUint32(args[1]) switch { 0x8867 => ScriptValue.FromBoolean(available), 0x8866 => ScriptValue.FromNumber(available ? 1 : 0), _ => ScriptValue.Null }; });
        t.DefineMethod("createTexture", (target, _, _) => { var state = (WebGlContextState)target; var handle = state.CreateHandle(); state.Textures[handle] = (0, 0, []); state.TextureInternalFormats[handle] = 0x1908; state.TextureParameters[handle] = []; return ScriptValue.FromNumber(handle); });
        t.DefineMethod("activeTexture", (target, _, args) => { if (args.Length > 0) ((WebGlContextState)target).ActiveTextureUnit = WebIdlConversions.ToUint32(args[0]); return ScriptValue.Undefined; });
        t.DefineMethod("bindTexture", (target, _, args) => { var state = (WebGlContextState)target; state.BoundTexture = args.Length > 1 ? (uint?)WebIdlConversions.ToUint32(args[1]) : null; return ScriptValue.Undefined; });
        t.DefineMethod("texImage2D", (target, _, args) =>
        {
            var state = (WebGlContextState)target;
            if (state.BoundTexture is not { } handle) { state.SetError(0x0502); return ScriptValue.Undefined; }

            // 6-argument overload: texImage2D(target, level, internalformat, format, type, source)
            if (args.Length == 6)
            {
                var internalFormat = WebIdlConversions.ToUint32(args[2]);
                var format = WebIdlConversions.ToUint32(args[3]);
                var type = WebIdlConversions.ToUint32(args[4]);
                var source = args[5];

                if (!TryExtractImageSource(source, out var width, out var height, out var sourcePixels) || sourcePixels is null)
                {
                    state.SetError(0x0502);
                    return ScriptValue.Undefined;
                }

                var bytes = width * height * 4;
                if (!state.ReplaceTexture(handle, width, height, bytes))
                    return ScriptValue.Undefined;

                state.CopyTexturePixels(handle, 0, 0, width, height, sourcePixels, state.UnpackFlipY, state.UnpackPremultiplyAlpha);
                state.TextureInternalFormats[handle] = internalFormat;
                return ScriptValue.Undefined;
            }

            // 9-argument (or WebGL2 10-argument) overload
            if (args.Length >= 9)
            {
                var width = Math.Clamp(WebIdlConversions.ToInt32(args[3]), 0, 4096);
                var height = Math.Clamp(WebIdlConversions.ToInt32(args[4]), 0, 4096);
                var internalFormat = WebIdlConversions.ToUint32(args[2]);
                var format = WebIdlConversions.ToUint32(args[6]);
                var type = WebIdlConversions.ToUint32(args[7]);

                var bytes = Math.Min(width * height * 4, 64 * 1024 * 1024);
                if (!state.ReplaceTexture(handle, width, height, bytes))
                    return ScriptValue.Undefined;

                state.TextureInternalFormats[handle] = internalFormat;

                if (!args[8].IsNullOrUndefined && _context.Engine.TryGetArrayItems(args[8], out var items))
                {
                    var srcOffset = args.Length > 9 ? Math.Max(0, WebIdlConversions.ToInt32(args[9])) : 0;
                    var pixelBytes = ConvertToRgbaBytes(items, srcOffset, width, height, format, type);
                    state.CopyTexturePixels(handle, 0, 0, width, height, pixelBytes, state.UnpackFlipY, state.UnpackPremultiplyAlpha);
                }
                return ScriptValue.Undefined;
            }

            state.SetError(0x0502);
            return ScriptValue.Undefined;
        });
        t.DefineMethod("texStorage2D", (target, _, args) =>
        {
            var state = (WebGlContextState)target;
            if (state.BoundTexture is not { } handle || args.Length < 5)
            {
                state.SetError(0x0502);
                return ScriptValue.Undefined;
            }
            var levels = Math.Clamp(WebIdlConversions.ToInt32(args[1]), 1, 16);
            var internalFormat = WebIdlConversions.ToUint32(args[2]);
            var width = Math.Clamp(WebIdlConversions.ToInt32(args[3]), 0, 4096);
            var height = Math.Clamp(WebIdlConversions.ToInt32(args[4]), 0, 4096);

            var totalBytes = 0;
            var curW = width;
            var curH = height;
            for (var l = 0; l < levels; l++)
            {
                totalBytes += curW * curH * 4;
                curW = Math.Max(1, curW / 2);
                curH = Math.Max(1, curH / 2);
            }
            if (!state.ReplaceTexture(handle, width, height, totalBytes))
                return ScriptValue.Undefined;

            state.TextureInternalFormats[handle] = internalFormat;
            return ScriptValue.Undefined;
        });
        t.DefineMethod("texStorage3D", (target, _, args) =>
        {
            var state = (WebGlContextState)target;
            if (state.BoundTexture is not { } handle || args.Length < 6)
            {
                state.SetError(0x0502);
                return ScriptValue.Undefined;
            }
            var levels = Math.Clamp(WebIdlConversions.ToInt32(args[1]), 1, 16);
            var internalFormat = WebIdlConversions.ToUint32(args[2]);
            var width = Math.Clamp(WebIdlConversions.ToInt32(args[3]), 0, 2048);
            var height = Math.Clamp(WebIdlConversions.ToInt32(args[4]), 0, 2048);
            var depth = Math.Clamp(WebIdlConversions.ToInt32(args[5]), 0, 256);

            var totalBytes = 0;
            var curW = width;
            var curH = height;
            var curD = depth;
            for (var l = 0; l < levels; l++)
            {
                totalBytes += curW * curH * curD * 4;
                curW = Math.Max(1, curW / 2);
                curH = Math.Max(1, curH / 2);
                curD = Math.Max(1, curD / 2);
            }
            if (!state.ReplaceTexture(handle, width, height * depth, totalBytes))
                return ScriptValue.Undefined;

            state.TextureInternalFormats[handle] = internalFormat;
            return ScriptValue.Undefined;
        });
        t.DefineMethod("texSubImage2D", (target, _, args) =>
        {
            var state = (WebGlContextState)target;
            if (state.BoundTexture is not { } handle || !state.Textures.ContainsKey(handle))
            {
                state.SetError(0x0502);
                return ScriptValue.Undefined;
            }

            // 7-argument overload
            if (args.Length == 7)
            {
                var x = Math.Max(0, WebIdlConversions.ToInt32(args[2]));
                var y = Math.Max(0, WebIdlConversions.ToInt32(args[3]));
                var source = args[6];
                if (!TryExtractImageSource(source, out var width, out var height, out var sourcePixels) || sourcePixels is null)
                {
                    state.SetError(0x0502);
                    return ScriptValue.Undefined;
                }
                state.CopyTexturePixels(handle, x, y, width, height, sourcePixels, state.UnpackFlipY, state.UnpackPremultiplyAlpha);
                return ScriptValue.Undefined;
            }

            // 9-argument overload
            if (args.Length >= 9)
            {
                var x = Math.Max(0, WebIdlConversions.ToInt32(args[2]));
                var y = Math.Max(0, WebIdlConversions.ToInt32(args[3]));
                var width = Math.Max(0, WebIdlConversions.ToInt32(args[4]));
                var height = Math.Max(0, WebIdlConversions.ToInt32(args[5]));
                var format = WebIdlConversions.ToUint32(args[6]);
                var type = WebIdlConversions.ToUint32(args[7]);

                if (!args[8].IsNullOrUndefined && _context.Engine.TryGetArrayItems(args[8], out var items))
                {
                    var srcOffset = args.Length > 9 ? Math.Max(0, WebIdlConversions.ToInt32(args[9])) : 0;
                    var pixelBytes = ConvertToRgbaBytes(items, srcOffset, width, height, format, type);
                    state.CopyTexturePixels(handle, x, y, width, height, pixelBytes, state.UnpackFlipY, state.UnpackPremultiplyAlpha);
                }
                return ScriptValue.Undefined;
            }

            state.SetError(0x0502);
            return ScriptValue.Undefined;
        });
        t.DefineMethod("generateMipmap", (target, _, args) =>
        {
            var state = (WebGlContextState)target;
            if (args.Length == 0 || (WebIdlConversions.ToUint32(args[0]) is not (0x0DE1 or 0x8513)))
            {
                state.SetError(0x0500);
                return ScriptValue.Undefined;
            }
            if (state.BoundTexture is not { } handle || !state.Textures.ContainsKey(handle))
            {
                state.SetError(0x0502);
                return ScriptValue.Undefined;
            }
            if (!state.GenerateMipmaps(handle))
            {
                if (state.Error == 0) state.SetError(0x0502);
            }
            return ScriptValue.Undefined;
        });
        t.DefineMethod("texParameteri", (target, _, args) => { var state = (WebGlContextState)target; if (state.BoundTexture is not { } handle || args.Length < 3) { state.SetError(0x0502); return ScriptValue.Undefined; } if (!state.TextureParameters.TryGetValue(handle, out var parameters)) state.TextureParameters[handle] = parameters = []; parameters[WebIdlConversions.ToUint32(args[1])] = WebIdlConversions.ToInt32(args[2]); return ScriptValue.Undefined; });
        t.DefineMethod("getTexParameter", (target, _, args) => { var state = (WebGlContextState)target; if (state.BoundTexture is not { } handle || args.Length < 2 || !state.TextureParameters.TryGetValue(handle, out var parameters) || !parameters.TryGetValue(WebIdlConversions.ToUint32(args[1]), out var value)) return ScriptValue.Null; return ScriptValue.FromNumber(value); });
        t.DefineMethod("getTexLevelParameter", (target, _, args) => { var state = (WebGlContextState)target; if (state.BoundTexture is not { } handle || args.Length < 3 || WebIdlConversions.ToUint32(args[0]) != 0x0DE1 || WebIdlConversions.ToInt32(args[1]) != 0 || !state.Textures.TryGetValue(handle, out var texture)) return ScriptValue.Null; return WebIdlConversions.ToUint32(args[2]) switch { 0x1000 => ScriptValue.FromNumber(texture.Width), 0x1001 => ScriptValue.FromNumber(texture.Height), 0x1003 => ScriptValue.FromNumber(state.TextureInternalFormats.TryGetValue(handle, out var format) ? format : 0x1908u), _ => ScriptValue.Null }; });
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
        t.DefineMethod("createRenderbuffer", (target, _, _) => { var state = (WebGlContextState)target; var handle = state.CreateHandle(); state.Renderbuffers[handle] = (0, 0, []); return ScriptValue.FromNumber(handle); });
        t.DefineMethod("bindRenderbuffer", (target, _, args) => { var state = (WebGlContextState)target; if (args.Length < 2 || WebIdlConversions.ToUint32(args[0]) != 0x8D41) { state.SetError(0x0500); return ScriptValue.Undefined; } state.BoundRenderbuffer = args[1].IsNullOrUndefined ? null : WebIdlConversions.ToUint32(args[1]); return ScriptValue.Undefined; });
        t.DefineMethod("renderbufferStorage", (target, _, args) =>
        {
            var state = (WebGlContextState)target;
            if (state.BoundRenderbuffer is not { } handle || args.Length < 4 || WebIdlConversions.ToUint32(args[0]) != 0x8D41)
            {
                state.SetError(0x0502);
                return ScriptValue.Undefined;
            }
            var format = WebIdlConversions.ToUint32(args[1]);
            if (format is not (0x8056 or 0x8058 or 0x81A5)) { state.SetError(0x0500); return ScriptValue.Undefined; }
            var width = WebIdlConversions.ToInt32(args[2]);
            var height = WebIdlConversions.ToInt32(args[3]);
            if (width < 0 || height < 0 || width > 4096 || height > 4096 || (long)width * height * 4 > 64 * 1024 * 1024) { state.SetError(0x0501); return ScriptValue.Undefined; }
            if (state.ReplaceRenderbuffer(handle, width, height, width * height * 4))
                state.RenderbufferFormats[handle] = format;
            return ScriptValue.Undefined;
        });
        t.DefineMethod("deleteRenderbuffer", (target, _, args) => { if (args.Length > 0) ((WebGlContextState)target).DeleteRenderbuffer(WebIdlConversions.ToUint32(args[0])); return ScriptValue.Undefined; });
        t.DefineMethod("isRenderbuffer", (target, _, args) => ScriptValue.FromBoolean(args.Length > 0 && ((WebGlContextState)target).Renderbuffers.ContainsKey(WebIdlConversions.ToUint32(args[0]))));
        t.DefineMethod("getRenderbufferParameter", (target, _, args) => { var state = (WebGlContextState)target; if (state.BoundRenderbuffer is not { } handle || args.Length < 2 || WebIdlConversions.ToUint32(args[0]) != 0x8D41 || !state.Renderbuffers.TryGetValue(handle, out var renderbuffer)) return ScriptValue.Null; return WebIdlConversions.ToUint32(args[1]) switch { 0x8D42 => ScriptValue.FromNumber(renderbuffer.Width), 0x8D43 => ScriptValue.FromNumber(renderbuffer.Height), 0x8D44 => ScriptValue.FromNumber(state.RenderbufferFormats.GetValueOrDefault(handle)), _ => ScriptValue.Null }; });
        t.DefineMethod("createVertexArray", (target, _, _) => { var state = (WebGlContextState)target; var handle = state.CreateHandle(); state.VertexArrays.Add(handle); return ScriptValue.FromNumber(handle); });
        t.DefineMethod("bindVertexArray", (target, _, args) => { var state = (WebGlContextState)target; state.BoundVertexArray = args.Length > 0 && !args[0].IsNullOrUndefined ? WebIdlConversions.ToUint32(args[0]) : null; return ScriptValue.Undefined; });
        t.DefineMethod("deleteVertexArray", (target, _, args) => { if (args.Length > 0) { var state = (WebGlContextState)target; var handle = WebIdlConversions.ToUint32(args[0]); state.VertexArrays.Remove(handle); state.ForgetVertexArray(handle); if (state.BoundVertexArray == handle) state.BoundVertexArray = null; } return ScriptValue.Undefined; });
        t.DefineMethod("isVertexArray", (target, _, args) => ScriptValue.FromBoolean(args.Length > 0 && ((WebGlContextState)target).VertexArrays.Contains(WebIdlConversions.ToUint32(args[0]))));
        t.DefineMethod("bindFramebuffer", (target, _, args) =>
        {
            var state = (WebGlContextState)target;
            if (args.Length < 2) { state.SetError(0x0502); return ScriptValue.Undefined; }
            var fbTarget = WebIdlConversions.ToUint32(args[0]);
            var handle = args[1].IsNullOrUndefined ? (uint?)null : WebIdlConversions.ToUint32(args[1]);
            if (handle is { } h && h != 0 && !state.Framebuffers.Contains(h)) { state.SetError(0x0502); return ScriptValue.Undefined; }
            var bindHandle = handle == 0 ? null : handle;
            if (fbTarget == 0x8CA8 /* READ_FRAMEBUFFER */)
                state.BoundReadFramebuffer = bindHandle;
            else if (fbTarget == 0x8CA9 /* DRAW_FRAMEBUFFER */)
                state.BoundDrawFramebuffer = bindHandle;
            else if (fbTarget == 0x8D40 /* FRAMEBUFFER */)
            {
                state.BoundFramebuffer = bindHandle;
                state.BoundReadFramebuffer = bindHandle;
                state.BoundDrawFramebuffer = bindHandle;
            }
            else state.SetError(0x0500);
            return ScriptValue.Undefined;
        });
        t.DefineMethod("framebufferTexture2D", (target, _, args) => { var state = (WebGlContextState)target; if (state.BoundFramebuffer is not { } framebuffer || args.Length < 5) { state.SetError(0x0502); return ScriptValue.Undefined; } var texture = args[3].IsNullOrUndefined ? 0u : WebIdlConversions.ToUint32(args[3]); if (texture != 0 && !state.Textures.ContainsKey(texture)) state.SetError(0x0502); else if (texture == 0) state.FramebufferColorAttachments.Remove(framebuffer); else state.FramebufferColorAttachments[framebuffer] = texture; return ScriptValue.Undefined; });
        t.DefineMethod("framebufferRenderbuffer", (target, _, args) => { var state = (WebGlContextState)target; if (state.BoundFramebuffer is not { } framebuffer || args.Length < 4 || (WebIdlConversions.ToUint32(args[1]) is not (0x8CE0 or 0x8D00)) || WebIdlConversions.ToUint32(args[2]) != 0x8D41) { state.SetError(0x0502); return ScriptValue.Undefined; } var attachment = WebIdlConversions.ToUint32(args[1]); var renderbuffer = args[3].IsNullOrUndefined ? 0u : WebIdlConversions.ToUint32(args[3]); if (renderbuffer != 0 && !state.Renderbuffers.ContainsKey(renderbuffer)) state.SetError(0x0502); else if (attachment == 0x8D00) { if (renderbuffer == 0) state.FramebufferDepthRenderbufferAttachments.Remove(framebuffer); else state.FramebufferDepthRenderbufferAttachments[framebuffer] = renderbuffer; } else if (renderbuffer == 0) state.FramebufferRenderbufferAttachments.Remove(framebuffer); else state.FramebufferRenderbufferAttachments[framebuffer] = renderbuffer; return ScriptValue.Undefined; });
        t.DefineMethod("checkFramebufferStatus", (target, _, _) => { var state = (WebGlContextState)target; return ScriptValue.FromNumber(state.BoundFramebuffer is { } framebuffer && (state.FramebufferColorAttachments.ContainsKey(framebuffer) || state.FramebufferRenderbufferAttachments.ContainsKey(framebuffer) || state.FramebufferDepthRenderbufferAttachments.ContainsKey(framebuffer)) ? 0x8CD5 : 0x8CD6); });
        t.DefineMethod("getFramebufferAttachmentParameter", (target, _, args) => { var state = (WebGlContextState)target; if (state.BoundFramebuffer is not { } framebuffer || args.Length < 3) return ScriptValue.Null; var attachment = WebIdlConversions.ToUint32(args[1]); var renderbuffer = attachment == 0x8D00 ? state.FramebufferDepthRenderbufferAttachments.GetValueOrDefault(framebuffer) : state.FramebufferRenderbufferAttachments.GetValueOrDefault(framebuffer); if (renderbuffer != 0) return WebIdlConversions.ToUint32(args[2]) switch { 0x8CD0 => ScriptValue.FromNumber(0x8D41), 0x8CD1 => ScriptValue.FromNumber(renderbuffer), _ => ScriptValue.Null }; if (!state.FramebufferColorAttachments.TryGetValue(framebuffer, out var texture)) return ScriptValue.Null; return WebIdlConversions.ToUint32(args[2]) switch { 0x8CD0 => ScriptValue.FromNumber(0x1702), 0x8CD1 => ScriptValue.FromNumber(texture), _ => ScriptValue.Null }; });
        t.DefineMethod("blitFramebuffer", (target, _, args) =>
        {
            var state = (WebGlContextState)target;
            if (args.Length < 10)
            {
                state.SetError(0x0502);
                return ScriptValue.Undefined;
            }
            var srcX0 = WebIdlConversions.ToInt32(args[0]);
            var srcY0 = WebIdlConversions.ToInt32(args[1]);
            var srcX1 = WebIdlConversions.ToInt32(args[2]);
            var srcY1 = WebIdlConversions.ToInt32(args[3]);
            var dstX0 = WebIdlConversions.ToInt32(args[4]);
            var dstY0 = WebIdlConversions.ToInt32(args[5]);
            var dstX1 = WebIdlConversions.ToInt32(args[6]);
            var dstY1 = WebIdlConversions.ToInt32(args[7]);
            var mask = WebIdlConversions.ToUint32(args[8]);
            var filter = WebIdlConversions.ToUint32(args[9]);
            state.BlitFramebuffer(srcX0, srcY0, srcX1, srcY1, dstX0, dstY0, dstX1, dstY1, mask, filter);
            return ScriptValue.Undefined;
        });
        t.DefineMethod("createSampler", (target, _, _) =>
        {
            var state = (WebGlContextState)target;
            var handle = state.CreateHandle();
            state.Samplers.Add(handle);
            return ScriptValue.FromNumber(handle);
        });
        t.DefineMethod("deleteSampler", (target, _, args) =>
        {
            if (args.Length > 0)
            {
                var state = (WebGlContextState)target;
                var handle = WebIdlConversions.ToUint32(args[0]);
                state.Samplers.Remove(handle);
                state.SamplerParameters.Remove(handle);
                foreach (var unit in state.BoundSamplers.Where(p => p.Value == handle).Select(p => p.Key).ToArray())
                    state.BoundSamplers[unit] = null;
            }
            return ScriptValue.Undefined;
        });
        t.DefineMethod("isSampler", (target, _, args) =>
            ScriptValue.FromBoolean(args.Length > 0 && ((WebGlContextState)target).Samplers.Contains(WebIdlConversions.ToUint32(args[0]))));
        t.DefineMethod("bindSampler", (target, _, args) =>
        {
            var state = (WebGlContextState)target;
            if (args.Length < 2) { state.SetError(0x0502); return ScriptValue.Undefined; }
            var unit = WebIdlConversions.ToUint32(args[0]);
            var sampler = args[1].IsNullOrUndefined ? (uint?)null : WebIdlConversions.ToUint32(args[1]);
            if (sampler is { } s && !state.Samplers.Contains(s)) { state.SetError(0x0502); return ScriptValue.Undefined; }
            state.BoundSamplers[unit] = sampler;
            return ScriptValue.Undefined;
        });
        t.DefineMethod("samplerParameteri", (target, _, args) =>
        {
            var state = (WebGlContextState)target;
            if (args.Length < 3 || !state.Samplers.Contains(WebIdlConversions.ToUint32(args[0]))) { state.SetError(0x0502); return ScriptValue.Undefined; }
            var s = WebIdlConversions.ToUint32(args[0]);
            if (!state.SamplerParameters.TryGetValue(s, out var p)) state.SamplerParameters[s] = p = [];
            p[WebIdlConversions.ToUint32(args[1])] = WebIdlConversions.ToInt32(args[2]);
            return ScriptValue.Undefined;
        });
        t.DefineMethod("samplerParameterf", (target, _, args) =>
        {
            var state = (WebGlContextState)target;
            if (args.Length < 3 || !state.Samplers.Contains(WebIdlConversions.ToUint32(args[0]))) { state.SetError(0x0502); return ScriptValue.Undefined; }
            var s = WebIdlConversions.ToUint32(args[0]);
            if (!state.SamplerParameters.TryGetValue(s, out var p)) state.SamplerParameters[s] = p = [];
            p[WebIdlConversions.ToUint32(args[1])] = WebIdlConversions.ToDouble(args[2]);
            return ScriptValue.Undefined;
        });
        t.DefineMethod("getSamplerParameter", (target, _, args) =>
        {
            var state = (WebGlContextState)target;
            if (args.Length < 2 || !state.Samplers.Contains(WebIdlConversions.ToUint32(args[0])) || !state.SamplerParameters.TryGetValue(WebIdlConversions.ToUint32(args[0]), out var p) || !p.TryGetValue(WebIdlConversions.ToUint32(args[1]), out var val))
                return ScriptValue.Null;
            return ScriptValue.FromNumber(val);
        });
        t.DefineMethod("readPixels", (target, _, args) =>
        {
            var state = (WebGlContextState)target;
            if (args.Length < 7) { state.SetError(0x0502); return ScriptValue.Undefined; }
            var x = WebIdlConversions.ToInt32(args[0]);
            var y = WebIdlConversions.ToInt32(args[1]);
            var width = WebIdlConversions.ToInt32(args[2]);
            var height = WebIdlConversions.ToInt32(args[3]);
            var format = WebIdlConversions.ToUint32(args[4]);
            var type = WebIdlConversions.ToUint32(args[5]);

            if (width < 0 || height < 0 || width > 4096 || height > 4096 || !_context.Engine.TryGetArrayItems(args[6], out var destination))
            {
                state.SetError(0x0501);
                return ScriptValue.Undefined;
            }

            if (format == 0x1902 /* DEPTH_COMPONENT */)
            {
                if (type is not (0x1406 /* FLOAT */ or 0x1403 /* UNSIGNED_SHORT */))
                {
                    state.SetError(0x0500);
                    return ScriptValue.Undefined;
                }
                var neededCount = (long)width * height;
                if (destination.Count < neededCount)
                {
                    state.SetError(0x0501);
                    return ScriptValue.Undefined;
                }
                var depthSource = (state.BoundReadFramebuffer ?? state.BoundFramebuffer) is { } depthFramebuffer && state.FramebufferDepthRenderbufferAttachments.TryGetValue(depthFramebuffer, out var depthHandle) && state.Renderbuffers.TryGetValue(depthHandle, out var depthPixels) ? depthPixels : default;
                for (var row = 0; row < height; row++)
                    for (var column = 0; column < width; column++)
                    {
                        var sourceX = x + column;
                        var sourceY = y + row;
                        var sourceIndex = sourceX >= 0 && sourceY >= 0 && sourceX < depthSource.Width && sourceY < depthSource.Height ? (sourceY * depthSource.Width + sourceX) * 4 : -1;
                        var depth = sourceIndex >= 0 && sourceIndex + 4 <= depthSource.Pixels.Length ? BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(depthSource.Pixels.AsSpan(sourceIndex, 4))) : 1f;
                        if (type == 0x1406)
                            _context.Engine.TrySetArrayItem(args[6], row * width + column, ScriptValue.FromNumber(depth));
                        else
                            _context.Engine.TrySetArrayItem(args[6], row * width + column, ScriptValue.FromNumber((ushort)Math.Clamp(MathF.Round(depth * 65535f), 0, 65535)));
                    }
                return ScriptValue.Undefined;
            }

            var isRgba = format == 0x1908;
            var isRed = format is (0x1903 or 0x8229);
            var isRgbaInt = format == 0x8D99;

            if (!isRgba && !isRed && !isRgbaInt)
            {
                state.SetError(0x0500);
                return ScriptValue.Undefined;
            }

            var channels = isRed ? 1 : 4;
            var count = (long)width * height * channels;
            if (count > 16 * 1024 * 1024 || destination.Count < count)
            {
                state.SetError(0x0501);
                return ScriptValue.Undefined;
            }

            byte[]? sourcePixels = null;
            int sourceWidth = 0, sourceHeight = 0;
            if (state.ReadSource() is { } readSource)
                (sourcePixels, sourceWidth, sourceHeight) = readSource;

            for (var row = 0; row < height; row++)
                for (var column = 0; column < width; column++)
                {
                    var sourceX = x + column;
                    var sourceY = y + row;
                    var sourceIndex = sourcePixels is not null && sourceX >= 0 && sourceY >= 0 && sourceX < sourceWidth && sourceY < sourceHeight
                        ? (sourceY * sourceWidth + sourceX) * 4 : -1;

                    var destPixelBase = (row * width + column) * channels;
                    for (var ch = 0; ch < channels; ch++)
                    {
                        var b = sourceIndex >= 0 && sourceIndex + ch < sourcePixels!.Length ? sourcePixels[sourceIndex + ch] : (byte)0;
                        if (type == 0x1406 /* FLOAT */)
                            _context.Engine.TrySetArrayItem(args[6], destPixelBase + ch, ScriptValue.FromNumber(b / 255.0));
                        else
                            _context.Engine.TrySetArrayItem(args[6], destPixelBase + ch, ScriptValue.FromNumber(b));
                    }
                }
            return ScriptValue.Undefined;
        });
        t.DefineMethod("clearColor", (target, _, args) => { if (args.Length > 3) ((WebGlContextState)target).SetClearColor((float)WebIdlConversions.ToDouble(args[0]), (float)WebIdlConversions.ToDouble(args[1]), (float)WebIdlConversions.ToDouble(args[2]), (float)WebIdlConversions.ToDouble(args[3])); return ScriptValue.Undefined; });
        t.DefineMethod("clear", (target, _, args) => { if (args.Length > 0) ((WebGlContextState)target).Clear(WebIdlConversions.ToUint32(args[0])); return ScriptValue.Undefined; });
        t.DefineMethod("drawBuffers", (target, _, args) => { var state = (WebGlContextState)target; if (args.Length == 0 || !_context.Engine.TryGetArrayItems(args[0], out var buffers)) { state.SetError(0x0502); return ScriptValue.Undefined; } state.DrawBuffers.Clear(); foreach (var buffer in buffers.Take(4)) state.DrawBuffers.Add(WebIdlConversions.ToUint32(buffer)); return ScriptValue.Undefined; });
        t.DefineMethod("readBuffer", (target, _, args) => { if (args.Length > 0) ((WebGlContextState)target).ReadBuffer = WebIdlConversions.ToUint32(args[0]); return ScriptValue.Undefined; });
        t.DefineMethod("getError", (target, _, _) => ScriptValue.FromNumber(((WebGlContextState)target).TakeError()));
        t.DefineMethod("isContextLost", (target, _, _) => ScriptValue.FromBoolean(((WebGlContextState)target).Lost));
        t.DefineMethod("getExtension", (target, _, args) =>
            args.Length > 0 && string.Equals(WebIdlConversions.ToDOMString(args[0]), "WEBGL_lose_context", StringComparison.OrdinalIgnoreCase)
                ? _context.Engine.CreateHostObject(WebGlLoseContextTemplate, target)
                : ScriptValue.Null);
        // Retained contexts must consult the host after creation too. In a lost
        // context, resource and draw operations are inert until an allowed restore.
        foreach (var name in t.MethodNames.ToArray())
        {
            if (name is "getError" or "isContextLost") continue;
            if (!t.TryGetMethod(name, out var callback)) continue;
            t.DefineMethod(name, (target, receiver, args) =>
            {
                if (!_context.CheckCapability("WebGL", _context.Capabilities.WebGl) || ((WebGlContextState)target).Lost)
                    return name.StartsWith("get", StringComparison.Ordinal) || name.StartsWith("create", StringComparison.Ordinal)
                        ? ScriptValue.Null : ScriptValue.Undefined;
                return callback(target, receiver, args);
            });
        }
        return t;

        static uint ReadCanvasDimension(CanvasElementState canvas, string name, uint fallback) =>
            uint.TryParse(canvas.Element.GetAttribute(name), out var value) ? value : fallback;

        static void SetLane(ref System.Numerics.Vector4 v, int lane, float value)
        {
            switch (lane)
            {
                case 0: v.X = value; break;
                case 1: v.Y = value; break;
                case 2: v.Z = value; break;
                default: v.W = value; break;
            }
        }

        static ScriptValue UniformScalars(WebGlContextState state, ReadOnlySpan<ScriptValue> args, int components, bool integer)
        {
            if (args.Length < components + 1) { state.SetError(0x0502); return ScriptValue.Undefined; }
            if (args[0].IsNullOrUndefined) return ScriptValue.Undefined;
            Span<float> data = stackalloc float[4];
            for (var i = 0; i < components; i++) data[i] = (float)WebIdlConversions.ToDouble(args[i + 1]);
            state.SetUniform(WebIdlConversions.ToInt32(args[0]) - 1, components, integer, data[..components], 1);
            return ScriptValue.Undefined;
        }

        static ScriptValue UniformVector(IScriptEngine engine, WebGlContextState state, ReadOnlySpan<ScriptValue> args, int components, bool integer)
        {
            if (args.Length < 2) { state.SetError(0x0502); return ScriptValue.Undefined; }
            if (args[0].IsNullOrUndefined) return ScriptValue.Undefined;
            if (!engine.TryGetArrayItems(args[1], out var items) || items.Count < components || items.Count % components != 0)
            {
                state.SetError(0x0501);
                return ScriptValue.Undefined;
            }
            var data = new float[items.Count];
            for (var i = 0; i < data.Length; i++) data[i] = (float)WebIdlConversions.ToDouble(items[i]);
            state.SetUniform(WebIdlConversions.ToInt32(args[0]) - 1, components, integer, data, items.Count / components);
            return ScriptValue.Undefined;
        }

        static ScriptValue UniformMatrix(IScriptEngine engine, WebGlContextState state, ReadOnlySpan<ScriptValue> args, int size)
        {
            if (args.Length < 3) { state.SetError(0x0502); return ScriptValue.Undefined; }
            if (args[0].IsNullOrUndefined) return ScriptValue.Undefined;
            var cells = size * size;
            if (!engine.TryGetArrayItems(args[2], out var items) || items.Count < cells || items.Count % cells != 0)
            {
                state.SetError(0x0501);
                return ScriptValue.Undefined;
            }
            var data = new float[items.Count];
            for (var i = 0; i < data.Length; i++) data[i] = (float)WebIdlConversions.ToDouble(items[i]);
            state.SetUniformMatrix(WebIdlConversions.ToInt32(args[0]) - 1, size, data, items.Count / cells, WebIdlConversions.ToBoolean(args[1]));
            return ScriptValue.Undefined;
        }

        // The bytes a typed array (or an ArrayBuffer) views, whatever its element type.
        static bool TryTypedBytes(IScriptEngine engine, ScriptValue value, out byte[] bytes)
        {
            if (engine.TryGetArrayBufferBytes(value, out bytes)) return true;
            var buffer = engine.GetProperty(value, "buffer");
            if (!buffer.IsUndefined && engine.TryGetArrayBufferBytes(buffer, out var all))
            {
                var offset = engine.GetProperty(value, "byteOffset") is { IsUndefined: false } o ? WebIdlConversions.ToInt32(o) : 0;
                var length = engine.GetProperty(value, "byteLength") is { IsUndefined: false } l ? WebIdlConversions.ToInt32(l) : all.Length - offset;
                if (offset >= 0 && length >= 0 && offset + length <= all.Length)
                {
                    bytes = all.AsSpan(offset, length).ToArray();
                    return true;
                }
            }
            bytes = [];
            return false;
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
                    container.ReplaceAll(text.Length > 0 ? container.OwnerDocument.CreateText(text) : null);
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
        // A style or link element's CSSStyleSheet; null for any other element.
        t.DefineProperty("sheet", (target, _) => _context.Engine.Call(_context.CssomSheetOf, ScriptValue.Undefined, [_context.Wrap((ElementNode)target)]));
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
        // The setters take null as the empty string.
        static string TextArgument(ScriptValue value) => value.Kind == ScriptValueKind.Null ? "" : WebIdlConversions.ToDOMString(value);
        t.DefineProperty("innerText",
            (target, _) => ScriptValue.FromString(DomText.InnerText((ElementNode)target)),
            (target, _, value) => DomText.SetInnerText((ElementNode)target, TextArgument(value)));
        t.DefineProperty("outerText",
            (target, _) => ScriptValue.FromString(DomText.InnerText((ElementNode)target)),
            (target, _, value) => DomText.SetOuterText((ElementNode)target, TextArgument(value)));

        // ParentNode members
        t.DefineProperty("children", (target, _) => _context.WrapHtmlCollection((ElementNode)target));
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
            var name = AttributeName((ElementNode)target, WebIdlConversions.ToDOMString(args[0]));
            var val = ((ElementNode)target).GetAttribute(name);
            return val is null ? ScriptValue.Null : ScriptValue.FromString(val);
        });

        t.DefineMethod("setAttribute", (target, _, args) =>
        {
            if (args.Length < 2)
                throw WebIdlException.TypeError("setAttribute requires 2 arguments.");
            var name = AttributeName((ElementNode)target, WebIdlConversions.ToDOMString(args[0]));
            var val = WebIdlConversions.ToDOMString(args[1]);
            var element = (ElementNode)target;
            element.SetAttribute(name, val);
            if (element.LocalName == "iframe" && (string.Equals(name, "sandbox", StringComparison.OrdinalIgnoreCase) || string.Equals(name, "src", StringComparison.OrdinalIgnoreCase) || string.Equals(name, "srcdoc", StringComparison.OrdinalIgnoreCase) || string.Equals(name, "allow", StringComparison.OrdinalIgnoreCase)))
                _context.GetIframe(element).Invalidate();
            MediaAttributeChanged(element, name);
            return ScriptValue.Undefined;
        });

        t.DefineMethod("hasAttribute", (target, _, args) =>
        {
            if (args.Length == 0)
                return ScriptValue.FromBoolean(false);
            var name = AttributeName((ElementNode)target, WebIdlConversions.ToDOMString(args[0]));
            return ScriptValue.FromBoolean(((ElementNode)target).GetAttribute(name) is not null);
        });

        t.DefineMethod("removeAttribute", (target, _, args) =>
        {
            if (args.Length > 0)
            {
                var name = AttributeName((ElementNode)target, WebIdlConversions.ToDOMString(args[0]));
                var element = (ElementNode)target;
                var oldValue = element.GetAttribute(name);
                element.RemoveAttribute(name);
                if (element.LocalName == "iframe" && (string.Equals(name, "sandbox", StringComparison.OrdinalIgnoreCase) || string.Equals(name, "src", StringComparison.OrdinalIgnoreCase) || string.Equals(name, "srcdoc", StringComparison.OrdinalIgnoreCase) || string.Equals(name, "allow", StringComparison.OrdinalIgnoreCase)) && oldValue is not null)
                    _context.GetIframe(element).Invalidate();
                if (oldValue is not null) MediaAttributeChanged(element, name);
            }
            return ScriptValue.Undefined;
        });

        t.DefineMethod("toggleAttribute", (target, _, args) =>
        {
            if (args.Length == 0)
                throw WebIdlException.TypeError("Failed to execute 'toggleAttribute' on 'Element': 1 argument required.");

            var el = (ElementNode)target;
            var name = AttributeName((ElementNode)target, WebIdlConversions.ToDOMString(args[0]));
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
                }
                if (el.LocalName == "iframe" && (string.Equals(name, "sandbox", StringComparison.OrdinalIgnoreCase) || string.Equals(name, "src", StringComparison.OrdinalIgnoreCase) || string.Equals(name, "srcdoc", StringComparison.OrdinalIgnoreCase) || string.Equals(name, "allow", StringComparison.OrdinalIgnoreCase)))
                    _context.GetIframe(el).Invalidate();
                if (!has) MediaAttributeChanged(el, name);
                return ScriptValue.FromBoolean(true);
            }
            else
            {
                if (has)
                {
                    el.RemoveAttribute(name);
                }
                if (el.LocalName == "iframe" && (string.Equals(name, "sandbox", StringComparison.OrdinalIgnoreCase) || string.Equals(name, "src", StringComparison.OrdinalIgnoreCase) || string.Equals(name, "srcdoc", StringComparison.OrdinalIgnoreCase) || string.Equals(name, "allow", StringComparison.OrdinalIgnoreCase)))
                    _context.GetIframe(el).Invalidate();
                if (has) MediaAttributeChanged(el, name);
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
        (target, _, name, value) =>
        {
            // The named setter sees every assignment, cssText included: that one replaces the whole block.
            var declaration = (LiveStyleDeclaration)target;
            if (name == "cssText")
                declaration.Replace(WebIdlConversions.ToDOMString(value));
            else
                declaration.Set(name, WebIdlConversions.ToDOMString(value), "");
        },
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
            return value.Length == 0 && !LiveComputedStyle.Knows(name) ? ScriptValue.Undefined : ScriptValue.FromString(value);
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

        t.DefineProperty("styleSheets", (_, _) => _context.Engine.Call(_context.CssomStyleSheets, ScriptValue.Undefined, ReadOnlySpan<ScriptValue>.Empty));

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

        t.DefineProperty("naturalWidth", (target, _) =>
        {
            var el = (ElementNode)target;
            var img = _context.ImageOf(el);
            return ScriptValue.FromNumber(img?.Width ?? 0);
        });

        t.DefineProperty("naturalHeight", (target, _) =>
        {
            var el = (ElementNode)target;
            var img = _context.ImageOf(el);
            return ScriptValue.FromNumber(img?.Height ?? 0);
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

    private bool TryExtractImageSource(ScriptValue source, out int width, out int height, out byte[]? pixels)
    {
        width = 0; height = 0; pixels = null;
        if (!source.IsObject) return false;

        if (_context.Engine.TryGetHostTarget(source, out var target))
        {
            if (target is CanvasImageData imageData)
            {
                width = imageData.Width;
                height = imageData.Height;
                pixels = _context.Templates.Canvas.PixelsOf(imageData);
                return true;
            }
            if (target is ElementNode element)
            {
                if (element.LocalName == "canvas")
                {
                    var canvas = _context.GetCanvas(element);
                    width = canvas.Width;
                    height = canvas.Height;
                    if (width <= 0 || height <= 0) return false;
                    pixels = canvas.Surface()?.ReadPixels(0, 0, width, height);
                    return pixels is not null;
                }
                if (element.LocalName == "img")
                {
                    var imageHandle = _context.ImageOf(element);
                    if (imageHandle is not null)
                    {
                        width = imageHandle.Width;
                        height = imageHandle.Height;
                        pixels = imageHandle.Pixels.ToArray();
                        return true;
                    }
                }
            }
        }
        var wVal = _context.Engine.GetProperty(source, "width");
        var hVal = _context.Engine.GetProperty(source, "height");
        var dataVal = _context.Engine.GetProperty(source, "data");
        if (!wVal.IsUndefined && !hVal.IsUndefined && !dataVal.IsUndefined)
        {
            width = WebIdlConversions.ToInt32(wVal);
            height = WebIdlConversions.ToInt32(hVal);
            if (width > 0 && height > 0 && _context.Engine.TryGetArrayItems(dataVal, out var items))
            {
                pixels = new byte[items.Count];
                for (var i = 0; i < items.Count; i++)
                    pixels[i] = (byte)Math.Clamp(WebIdlConversions.ToInt32(items[i]), 0, 255);
                return true;
            }
        }
        return false;
    }

    private static byte[] ConvertToRgbaBytes(IReadOnlyList<ScriptValue> items, int offset, int width, int height, uint format, uint type)
    {
        var pixelCount = width * height;
        var result = new byte[pixelCount * 4];
        if (format is 0x1908 or 0x8058 /* RGBA / RGBA8 */)
        {
            for (var i = 0; i < pixelCount; i++)
            {
                var srcIdx = offset + i * 4;
                var dstIdx = i * 4;
                if (srcIdx + 3 < items.Count)
                {
                    result[dstIdx] = (byte)Math.Clamp(WebIdlConversions.ToInt32(items[srcIdx]), 0, 255);
                    result[dstIdx + 1] = (byte)Math.Clamp(WebIdlConversions.ToInt32(items[srcIdx + 1]), 0, 255);
                    result[dstIdx + 2] = (byte)Math.Clamp(WebIdlConversions.ToInt32(items[srcIdx + 2]), 0, 255);
                    result[dstIdx + 3] = (byte)Math.Clamp(WebIdlConversions.ToInt32(items[srcIdx + 3]), 0, 255);
                }
            }
            return result;
        }
        if (format is 0x1907 or 0x8051 /* RGB / RGB8 */)
        {
            for (var i = 0; i < pixelCount; i++)
            {
                var srcIdx = offset + i * 3;
                var dstIdx = i * 4;
                if (srcIdx + 2 < items.Count)
                {
                    result[dstIdx] = (byte)Math.Clamp(WebIdlConversions.ToInt32(items[srcIdx]), 0, 255);
                    result[dstIdx + 1] = (byte)Math.Clamp(WebIdlConversions.ToInt32(items[srcIdx + 1]), 0, 255);
                    result[dstIdx + 2] = (byte)Math.Clamp(WebIdlConversions.ToInt32(items[srcIdx + 2]), 0, 255);
                    result[dstIdx + 3] = 255;
                }
            }
            return result;
        }
        if (format == 0x1909 /* LUMINANCE */)
        {
            for (var i = 0; i < pixelCount; i++)
            {
                var srcIdx = offset + i;
                var dstIdx = i * 4;
                if (srcIdx < items.Count)
                {
                    var lum = (byte)Math.Clamp(WebIdlConversions.ToInt32(items[srcIdx]), 0, 255);
                    result[dstIdx] = lum;
                    result[dstIdx + 1] = lum;
                    result[dstIdx + 2] = lum;
                    result[dstIdx + 3] = 255;
                }
            }
            return result;
        }
        if (format == 0x1906 /* ALPHA */)
        {
            for (var i = 0; i < pixelCount; i++)
            {
                var srcIdx = offset + i;
                var dstIdx = i * 4;
                if (srcIdx < items.Count)
                {
                    result[dstIdx + 3] = (byte)Math.Clamp(WebIdlConversions.ToInt32(items[srcIdx]), 0, 255);
                }
            }
            return result;
        }
        if (format == 0x190A /* LUMINANCE_ALPHA */)
        {
            for (var i = 0; i < pixelCount; i++)
            {
                var srcIdx = offset + i * 2;
                var dstIdx = i * 4;
                if (srcIdx + 1 < items.Count)
                {
                    var lum = (byte)Math.Clamp(WebIdlConversions.ToInt32(items[srcIdx]), 0, 255);
                    var a = (byte)Math.Clamp(WebIdlConversions.ToInt32(items[srcIdx + 1]), 0, 255);
                    result[dstIdx] = lum;
                    result[dstIdx + 1] = lum;
                    result[dstIdx + 2] = lum;
                    result[dstIdx + 3] = a;
                }
            }
            return result;
        }
        for (var i = 0; i < pixelCount; i++)
        {
            var srcIdx = offset + i * 4;
            var dstIdx = i * 4;
            if (srcIdx + 3 < items.Count)
            {
                result[dstIdx] = (byte)Math.Clamp(WebIdlConversions.ToInt32(items[srcIdx]), 0, 255);
                result[dstIdx + 1] = (byte)Math.Clamp(WebIdlConversions.ToInt32(items[srcIdx + 1]), 0, 255);
                result[dstIdx + 2] = (byte)Math.Clamp(WebIdlConversions.ToInt32(items[srcIdx + 2]), 0, 255);
                result[dstIdx + 3] = (byte)Math.Clamp(WebIdlConversions.ToInt32(items[srcIdx + 3]), 0, 255);
            }
        }
        return result;
    }

}
