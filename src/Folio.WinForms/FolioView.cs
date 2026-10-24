using System.ComponentModel;
using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Folio.Painting;
using Folio.Skia;
using Folio.Scripting.Ipc;
using SkiaSharp;

namespace Folio.WinForms;

/// <summary>A link the reader activated; set <see cref="Handled"/> to stop the default action (opening the system browser).</summary>
public sealed class LinkActivatedEventArgs(Uri uri, KeyModifiers modifiers = KeyModifiers.None) : EventArgs
{
    public Uri Uri { get; } = uri;
    public KeyModifiers Modifiers { get; } = modifiers;
    public bool Handled { get; set; }
}

/// <summary>A document's diagnostics after it was loaded.</summary>
public sealed class DiagnosticsEventArgs(IReadOnlyList<Diagnostic> diagnostics) : EventArgs
{
    public IReadOnlyList<Diagnostic> Diagnostics { get; } = diagnostics;
}

/// <summary>Rendering failed; the control shows its background instead of the page.</summary>
public sealed class RenderFailedEventArgs(Exception exception) : EventArgs
{
    public Exception Exception { get; } = exception;
}

public sealed class ScriptStoppedEventArgs(string reason) : EventArgs
{
    public string Reason { get; } = reason;
}

/// <summary>
/// Shows an HTML document (docs/architecture.md, WinForms control): lays it out at the control's width, relays it out
/// on resize and zoom, scrolls the page with the wheel and a scroll bar, passes link clicks to the host, and plays CSS
/// animations at the display's refresh rate while the control is shown. The document reports
/// <c>prefers-reduced-motion: reduce</c> when the host's options ask for it or Windows has animations turned off.
/// </summary>
// ponytail: each paint replays the whole display list; invalidation by rectangle and hover states come in M3.
public class FolioView : Control
{
    private readonly VScrollBar _scrollBar = new() { Dock = DockStyle.Right, Visible = false, SmallChange = 40 };
    private Document? _document;
    private DisplayList? _list;
    private float _contentHeight;
    private float _laidOutWidth = -1;
    private float _zoom = 1f;

    // Animation frames: the document timeline runs while the control is shown and stops while it is hidden.
    private readonly System.Windows.Forms.Timer _frames = new();
    private readonly Stopwatch _timeline = new();
    private readonly ToolTip _toolTip = new();
    private readonly Label _mediaCaptureIndicator = new()
    {
        AutoSize = true,
        BackColor = Color.FromArgb(220, 30, 30, 30),
        ForeColor = Color.White,
        Padding = new Padding(6, 3, 6, 3),
        Text = "Camera or microphone in use",
        Visible = false,
    };
    private FolioViewAccessibleObject? _accessibleObject;
    protected internal ContentProcessClient? _contentProcess;
    private Func<ContentMessage, CancellationToken, ValueTask>? _contentMessageHandler;
    private CanvasFrameStore? _contentFrameStore;
    private Bitmap? _contentBitmap;
    private string? _contentFramePath;
    private string? _scriptStoppedReason;
    private (ContentProcessClient Client, long Generation, int Width, int Height, int Stride)? _pendingContentFrame;
    private (object? Sender, long Generation, string Reason)? _pendingScriptStopped;
    private (ContentProcessClient Client, long Generation, bool Active)? _pendingMediaActivity;
    private FeatureCapabilities _capabilities = FeatureCapabilities.Default;
    private readonly object _contentStateGate = new();
    private ContentCommandQueue _contentCommands = new();
    private readonly DisplayListContext _displayListContext = new();
    private long _contentGeneration;

    public FolioView()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        _scrollBar.ValueChanged += (_, _) => Invalidate();
        Controls.Add(_scrollBar);
        Controls.Add(_mediaCaptureIndicator);
        _mediaCaptureIndicator.BringToFront();
        _frames.Tick += (_, _) => NextFrame();
    }

    internal float ScrollTop => _scrollBar.Visible ? _scrollBar.Value / PixelScale : 0;

    protected override AccessibleObject CreateAccessibilityInstance()
    {
        _accessibleObject = new FolioViewAccessibleObject(this);
        _accessibleObject.SyncTree(_document?.AccessibilityTree());
        return _accessibleObject;
    }


    /// <summary>The options new documents are parsed with.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public FolioOptions Options { get; set; } = new();

    /// <summary>The active color scheme (light or dark mode) for the view and its loaded document.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public ColorScheme ColorScheme
    {
        get => _document?.ColorScheme ?? Options.ColorScheme;
        set
        {
            Options.ColorScheme = value;
            if (_document is not null)
                _document.ColorScheme = value;
            Invalidate();
        }
    }

    /// <summary>Whether reduced motion is requested for animations.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool ReducedMotion
    {
        get => _document?.ReducedMotion ?? Options.ReducedMotion;
        set
        {
            Options.ReducedMotion = value;
            if (_document is not null)
                _document.ReducedMotion = value;
            Invalidate();
        }
    }

    /// <summary>The capabilities currently requested for this view's content artifact.</summary>
    [Browsable(false)]
    public FeatureCapabilities Capabilities { get { lock (_contentStateGate) return _capabilities; } }

    /// <summary>The document shown, or null before <see cref="LoadHtml"/>.</summary>
    [Browsable(false)]
    public Document? Document => _document;

    /// <summary>CSS pixels per device-independent pixel, on top of the display's scale.</summary>
    [DefaultValue(1f)]
    public float ZoomFactor
    {
        get => _zoom;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 0.1f);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, 10f);
            _zoom = value;
            _laidOutWidth = -1;
            Invalidate();
        }
    }

    /// <summary>The plain text of the active selection, or empty.</summary>
    [Browsable(false)]
    public string SelectedText => _document?.SelectedText ?? "";

    /// <summary>The HTML fragment of the active selection, or empty.</summary>
    [Browsable(false)]
    public string SelectedHtml => _document?.SelectedHtml ?? "";

    /// <summary>The active display list painted for this view.</summary>
    [Browsable(false)]
    internal DisplayList? DisplayList => _list ?? _document?.DisplayList;

    /// <summary>Selects all selectable content in the document.</summary>
    public void SelectAll() => _document?.SelectAll();

    /// <summary>Clears the current selection.</summary>
    public void ClearSelection() => _document?.ClearSelection();

    /// <summary>Copies the active selection to the system clipboard as both plain text and HTML.</summary>
    public void CopySelection()
    {
        var text = SelectedText;
        var html = SelectedHtml;
        if (string.IsNullOrEmpty(text) && string.IsNullOrEmpty(html))
            return;

        var data = new DataObject();
        if (!string.IsNullOrEmpty(text))
            data.SetData(DataFormats.UnicodeText, true, text);
        if (!string.IsNullOrEmpty(html))
            data.SetData(DataFormats.Html, true, BuildWindowsClipboardHtml(html));

        try
        {
            Clipboard.SetDataObject(data, true);
        }
        catch (ExternalException)
        {
            // Clipboard may be locked by another application
        }
    }

    public event EventHandler? Rendered;
    public event EventHandler<DiagnosticsEventArgs>? DiagnosticsChanged;
    public event EventHandler<RenderFailedEventArgs>? RenderFailed;
    public event EventHandler<ScriptStoppedEventArgs>? ScriptStopped;

    /// <summary>A link was clicked. Unless handled, http, https and mailto links open in the system browser.</summary>
    public event EventHandler<LinkActivatedEventArgs>? LinkActivated;

    /// <summary>A button was clicked or activated.</summary>
    public event EventHandler<ButtonClickedEventArgs>? ButtonClicked;

    /// <summary>A form was submitted.</summary>
    public event EventHandler<FormSubmittedEventArgs>? FormSubmitted;

    /// <summary>Raised when a document starts or stops using camera or microphone capture.</summary>
    public event Action<bool>? MediaCaptureActivityChanged;

    /// <summary>Raised when a &lt;select&gt; drop-down popup is opened by the content process.</summary>
    public event EventHandler<SelectDropdownEventArgs>? SelectDropdownOpened;

    /// <summary>Picks an option for an open or targeted &lt;select&gt; in the content process.</summary>
    public void PickSelectOption(string? selectId, int optionIndex)
    {
        if (_contentProcess is not null)
        {
            SendContentInput(new ContentInputEvent("selectoption", Button: optionIndex, Key: selectId));
        }
    }

    /// <summary>Connects the control to a content process and displays a visible stopped notice if it is terminated.</summary>
    public void AttachContentProcess(ContentProcessClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        lock (_contentStateGate)
            if (ReferenceEquals(_contentProcess, client))
                return;
        var previous = RetireAttachedContentProcess(clearFrame: false);
        if (previous is not null && !ReferenceEquals(previous, client))
            DisposeRetiredContentProcess(previous);
        lock (_contentStateGate)
        {
            _contentProcess = client;
            _contentGeneration++;
            _contentCommands = new();
            _pendingContentFrame = null;
            _pendingScriptStopped = null;
            _pendingMediaActivity = null;
        }
        _contentProcess.ScriptStopped += OnScriptStopped;
        var generation = _contentGeneration;
        _contentMessageHandler = (message, token) => OnContentMessage(client, generation, message, token);
        _contentProcess.MessageReceived += _contentMessageHandler;
        _scriptStoppedReason = null;
    }

    private ContentProcessClient? RetireAttachedContentProcess(bool clearFrame)
    {
        ContentProcessClient? previous;
        if (_contentProcess is { } attached)
        {
            attached.ScriptStopped -= OnScriptStopped;
            if (_contentMessageHandler is { } previousHandler)
                attached.MessageReceived -= previousHandler;
        }
        lock (_contentStateGate)
        {
            previous = _contentProcess;
            _contentProcess = null;
            _contentMessageHandler = null;
            _contentGeneration++;
            _contentCommands = new();
            _pendingContentFrame = null;
            _pendingScriptStopped = null;
            _pendingMediaActivity = null;
            if (clearFrame)
            {
                _contentBitmap?.Dispose();
                _contentBitmap = null;
                _scriptStoppedReason = null;
            }
            _contentFrameStore?.Dispose();
            _contentFrameStore = null;
            _contentFramePath = null;
        }
        return previous;
    }

    private static void DisposeRetiredContentProcess(ContentProcessClient process) =>
        process.DisposeAsync().AsTask().GetAwaiter().GetResult();

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        FlushPendingContent();
    }

    /// <summary>Changes capabilities on the live content artifact without reloading it.</summary>
    /// <exception cref="InvalidOperationException">Thrown when no content process is attached.</exception>
    public ValueTask UpdateCapabilitiesAsync(FeatureCapabilities capabilities, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        lock (_contentStateGate)
        {
            var process = _contentProcess ?? throw new InvalidOperationException("A content process is required for live capability updates.");
            var generation = _contentGeneration;
            return new ValueTask(_contentCommands.Enqueue(async () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                ValueTask pending;
                lock (_contentStateGate)
                {
                    if (!ReferenceEquals(_contentProcess, process) || _contentGeneration != generation)
                        throw new InvalidOperationException("The content document changed before its capability update was sent.");
                    pending = process.UpdateCapabilitiesAsync(capabilities, cancellationToken);
                }
                await pending.ConfigureAwait(false);
                lock (_contentStateGate)
                    if (ReferenceEquals(_contentProcess, process) && _contentGeneration == generation)
                        _capabilities = capabilities;
            }));
        }
    }

    private bool IsCurrentContentProcess(ContentProcessClient client, long generation)
    {
        lock (_contentStateGate)
            return ReferenceEquals(client, _contentProcess) && generation == _contentGeneration;
    }

    private ValueTask OnContentMessage(ContentProcessClient client, long generation, ContentMessage message, CancellationToken cancellationToken)
    {
        if (!IsCurrentContentProcess(client, generation)) return ValueTask.CompletedTask;
        if (message.Kind == ContentMessageKind.MediaCaptureActivity
            && message.Payload.TryGetProperty("active", out var activeValue)
            && activeValue.ValueKind is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False)
        {
            var active = activeValue.GetBoolean();
            lock (_contentStateGate)
            {
                if (!IsCurrentContentProcess(client, generation)) return ValueTask.CompletedTask;
                _pendingMediaActivity = (client, generation, active);
                if (!IsHandleCreated) return ValueTask.CompletedTask;
            }
            PostPendingContent();
            return ValueTask.CompletedTask;
        }
        if (message.Kind == ContentMessageKind.Cursor && message.Payload.TryGetProperty("cursor", out var cVal))
        {
            var cur = cVal.GetString() ?? "default";
            try { if (!IsDisposed && IsHandleCreated) BeginInvoke(() => Cursor = MapCursor(cur)); }
            catch (InvalidOperationException) { }
            return ValueTask.CompletedTask;
        }
        if (message.Kind == ContentMessageKind.Tooltip && message.Payload.TryGetProperty("tooltip", out var tVal))
        {
            var tooltip = tVal.GetString();
            try { if (!IsDisposed && IsHandleCreated) BeginInvoke(() => _toolTip.SetToolTip(this, tooltip)); }
            catch (InvalidOperationException) { }
            return ValueTask.CompletedTask;
        }
        if (message.Kind == ContentMessageKind.LinkActivated && message.Payload.TryGetProperty("uri", out var uVal)
            && Uri.TryCreate(uVal.GetString(), UriKind.Absolute, out var uri))
        {
            var mods = message.Payload.TryGetProperty("modifiers", out var mVal) && mVal.TryGetInt32(out var mInt) ? (KeyModifiers)mInt : KeyModifiers.None;
            try { if (!IsDisposed && IsHandleCreated) BeginInvoke(() => OnDocumentLinkActivated(this, new Folio.LinkActivatedEventArgs(uri, mods))); }
            catch (InvalidOperationException) { }
            return ValueTask.CompletedTask;
        }
        if (message.Kind == ContentMessageKind.ClipboardCopy)
        {
            var text = message.Payload.TryGetProperty("text", out var tx) ? tx.GetString() ?? "" : "";
            var html = message.Payload.TryGetProperty("html", out var hx) ? hx.GetString() ?? "" : "";
            try
            {
                if (!IsDisposed && IsHandleCreated)
                {
                    BeginInvoke(() =>
                    {
                        var data = new DataObject();
                        if (!string.IsNullOrEmpty(text))
                            data.SetData(DataFormats.UnicodeText, true, text);
                        if (!string.IsNullOrEmpty(html))
                            data.SetData(DataFormats.Html, true, BuildWindowsClipboardHtml(html));
                        try { Clipboard.SetDataObject(data, true); }
                        catch (ExternalException) { }
                    });
                }
            }
            catch (InvalidOperationException) { }
            return ValueTask.CompletedTask;
        }
        if (message.Kind == ContentMessageKind.FormSubmitted && message.Payload.TryGetProperty("action", out var actVal))
        {
            var action = actVal.GetString() ?? "";
            var method = message.Payload.TryGetProperty("method", out var mth) ? mth.GetString() ?? "GET" : "GET";
            var dataList = new List<KeyValuePair<string, string>>();
            if (message.Payload.TryGetProperty("data", out var dArr) && dArr.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var item in dArr.EnumerateArray())
                {
                    var k = item.TryGetProperty("name", out var kn) ? kn.GetString() ?? "" : "";
                    var v = item.TryGetProperty("value", out var vn) ? vn.GetString() ?? "" : "";
                    dataList.Add(new KeyValuePair<string, string>(k, v));
                }
            }
            var formArgs = new FormSubmittedEventArgs(Document?.Node.Descendants.OfType<Folio.Dom.ElementNode>().FirstOrDefault(e => e.LocalName == "form") is { } formEl ? Document.Wrap(formEl) : null!, action, method, dataList);
            try { if (!IsDisposed && IsHandleCreated) BeginInvoke(() => FormSubmitted?.Invoke(this, formArgs)); }
            catch (InvalidOperationException) { }
            return ValueTask.CompletedTask;
        }
        if (message.Kind == ContentMessageKind.SelectDropdown)
        {
            var selectId = message.Payload.TryGetProperty("selectId", out var sId) ? sId.GetString() : null;
            var selectedIndex = message.Payload.TryGetProperty("selectedIndex", out var sIdx) && sIdx.TryGetInt32(out var idx) ? idx : 0;
            var optionsList = new List<SelectOptionItem>();
            if (message.Payload.TryGetProperty("options", out var opts) && opts.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var opt in opts.EnumerateArray())
                {
                    var optIdx = opt.TryGetProperty("index", out var oi) && oi.TryGetInt32(out var oIdx) ? oIdx : optionsList.Count;
                    var val = opt.TryGetProperty("value", out var ov) ? ov.GetString() ?? "" : "";
                    var txt = opt.TryGetProperty("text", out var ot) ? ot.GetString() ?? "" : "";
                    var sel = opt.TryGetProperty("selected", out var os) && os.GetBoolean();
                    var dis = opt.TryGetProperty("disabled", out var od) && od.GetBoolean();
                    optionsList.Add(new SelectOptionItem(optIdx, val, txt, sel, dis));
                }
            }
            var dropdownArgs = new SelectDropdownEventArgs(selectId, selectedIndex, optionsList);
            try { if (!IsDisposed && IsHandleCreated) BeginInvoke(() => SelectDropdownOpened?.Invoke(this, dropdownArgs)); }
            catch (InvalidOperationException) { }
            return ValueTask.CompletedTask;
        }
        if (message.Kind == ContentMessageKind.Frame && message.Payload.TryGetProperty("displayList", out var dlVal) && dlVal.GetString() is { Length: > 0 } dlBase64)
        {
            try
            {
                var dlBytes = Convert.FromBase64String(dlBase64);
                if (DisplayListCodec.TryDecode(dlBytes, out var decodedList, _displayListContext) && decodedList is not null)
                {
                    _list = decodedList;
                }
            }
            catch { }
        }
        if (message.Kind != ContentMessageKind.Frame || !message.Payload.TryGetProperty("canvasFrame", out var frame))
            return ValueTask.CompletedTask;
        var path = frame.TryGetProperty("path", out var pathValue) ? pathValue.GetString() : null;
        if (!string.Equals(path, client.FramePath, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
            || !frame.TryGetProperty("width", out var widthValue) || !widthValue.TryGetInt32(out var width)
            || !frame.TryGetProperty("height", out var heightValue) || !heightValue.TryGetInt32(out var height)
            || !frame.TryGetProperty("stride", out var strideValue) || !strideValue.TryGetInt32(out var stride))
            return ValueTask.CompletedTask;

        lock (_contentStateGate)
        {
            if (!IsCurrentContentProcess(client, generation)) return ValueTask.CompletedTask;
            _pendingContentFrame = (client, generation, width, height, stride);
            if (!IsHandleCreated) return ValueTask.CompletedTask;
        }
        PostPendingContent();
        return ValueTask.CompletedTask;
    }

    private void PostPendingContent()
    {
        try { if (!IsDisposed && IsHandleCreated) BeginInvoke((Action)FlushPendingContent); }
        catch (InvalidOperationException) { }
    }

    private void FlushPendingContent()
    {
        (ContentProcessClient Client, long Generation, int Width, int Height, int Stride)? frame;
        (object? Sender, long Generation, string Reason)? stopped;
        (ContentProcessClient Client, long Generation, bool Active)? activity;
        lock (_contentStateGate)
        {
            frame = _pendingContentFrame;
            stopped = _pendingScriptStopped;
            activity = _pendingMediaActivity;
            _pendingContentFrame = null;
            _pendingScriptStopped = null;
            _pendingMediaActivity = null;
        }
        if (frame is { } pendingFrame && IsCurrentContentProcess(pendingFrame.Client, pendingFrame.Generation))
            ApplyContentFrame(pendingFrame.Client.FramePath, pendingFrame.Width, pendingFrame.Height, pendingFrame.Stride);
        if (stopped is { } pendingStopped)
            ApplyScriptStopped(pendingStopped.Sender, pendingStopped.Generation, pendingStopped.Reason);
        if (activity is { } pendingActivity && IsCurrentContentProcess(pendingActivity.Client, pendingActivity.Generation))
        {
            _mediaCaptureIndicator.Visible = pendingActivity.Active;
            _mediaCaptureIndicator.BringToFront();
            MediaCaptureActivityChanged?.Invoke(pendingActivity.Active);
            Options.MediaCaptureActivityChanged?.Invoke(pendingActivity.Active);
        }
    }

    private void QueueContentLoad(ContentProcessClient process, object payload)
    {
        Task pending;
        long generation;
        lock (_contentStateGate)
        {
            generation = _contentGeneration;
            pending = _contentCommands.Enqueue(() =>
            {
                lock (_contentStateGate)
                {
                    if (!ReferenceEquals(_contentProcess, process) || _contentGeneration != generation)
                        return ValueTask.CompletedTask;
                    return process.SendAsync(ContentMessageKind.Load, payload);
                }
            });
        }
        _ = ObserveAsync();
        async Task ObserveAsync()
        {
            try { await pending.ConfigureAwait(false); }
            catch (Exception error)
            {
                bool current;
                lock (_contentStateGate)
                    current = ReferenceEquals(_contentProcess, process) && _contentGeneration == generation;
                if (current) OnScriptStopped(this, new ContentProcessStoppedEventArgs($"unable to load content document: {error.Message}"));
            }
        }
    }

    private void ApplyContentFrame(string path, int width, int height, int stride)
    {
        try
        {
            if (_contentFramePath != path || _contentFrameStore is null)
            {
                _contentFrameStore?.Dispose();
                _contentFrameStore = new CanvasFrameStore(path, width, height, stride);
                _contentFramePath = path;
            }

            var pixels = _contentFrameStore.ReadLatest(out _);
            _contentBitmap ??= new Bitmap(width, height, PixelFormat.Format32bppPArgb);
            if (_contentBitmap.Width != width || _contentBitmap.Height != height)
            {
                _contentBitmap.Dispose();
                _contentBitmap = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
            }
            var area = new Rectangle(0, 0, width, height);
            var data = _contentBitmap.LockBits(area, ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
            try { Marshal.Copy(pixels, 0, data.Scan0, Math.Min(pixels.Length, Math.Abs(data.Stride) * height)); }
            finally { _contentBitmap.UnlockBits(data); }
            Invalidate();
        }
        catch (Exception exception) when (exception is IOException or ArgumentException or ExternalException)
        {
            RenderFailed?.Invoke(this, new RenderFailedEventArgs(exception));
        }
    }

    private async Task SendContentInputAsync(ContentInputEvent input)
    {
        try
        {
            if (_contentProcess is not null)
                await _contentProcess.SendAsync(ContentMessageKind.Input, input).ConfigureAwait(false);
        }
        catch (InvalidOperationException) { }
        catch (IOException) { }
    }

    protected internal void SendContentInput(ContentInputEvent input) => _ = SendContentInputAsync(input);

    private void OnScriptStopped(object? sender, ContentProcessStoppedEventArgs e)
    {
        var generation = _contentGeneration;
        lock (_contentStateGate)
        {
            if (generation != _contentGeneration || sender is ContentProcessClient current && !ReferenceEquals(current, _contentProcess)) return;
            _pendingScriptStopped = (sender, generation, e.Reason);
            if (!IsHandleCreated) return;
        }
        PostPendingContent();
    }

    private void ApplyScriptStopped(object? sender, long generation, string reason)
    {
        void Apply()
        {
            if (IsDisposed || generation != _contentGeneration ||
                sender is ContentProcessClient client && !ReferenceEquals(client, _contentProcess)) return;
            _scriptStoppedReason = reason;
            ScriptStopped?.Invoke(this, new ScriptStoppedEventArgs(reason));
            RenderFailed?.Invoke(this, new RenderFailedEventArgs(new InvalidOperationException($"Script stopped: {FormatPlainReason(reason)}")));
            Invalidate();
        }
        try
        {
            if (IsHandleCreated && InvokeRequired) BeginInvoke((Action)Apply);
            else Apply();
        }
        catch (InvalidOperationException) { }
    }

    public void LoadHtml(string html, Uri? baseUri = null)
    {
        ArgumentNullException.ThrowIfNull(html);
        lock (_contentStateGate)
        {
            _capabilities = Options.Capabilities;
            _contentGeneration++;
        }
        var options = new FolioOptions
        {
            BaseUri = baseUri ?? Options.BaseUri, ColorScheme = Options.ColorScheme, ReducedMotion = Options.ReducedMotion || SystemReducedMotion(),
            UserStyleSheet = Options.UserStyleSheet, Limits = Options.Limits, CollectDiagnostics = Options.CollectDiagnostics, Fonts = Options.Fonts,
            ResourceLoader = Options.ResourceLoader, Scripting = Options.Scripting, ContentProcessPath = Options.ContentProcessPath,
            ArtifactId = Options.ArtifactId, ArtifactStorage = Options.ArtifactStorage,
            MediaCapturePermission = Options.MediaCapturePermission,
            MediaCaptureActivityChanged = active =>
            {
                _mediaCaptureIndicator.Visible = active;
                _mediaCaptureIndicator.BringToFront();
                MediaCaptureActivityChanged?.Invoke(active);
                Options.MediaCaptureActivityChanged?.Invoke(active);
            },
            IframeNavigationRequested = Options.IframeNavigationRequested,
            WebRtcAllowedOrigins = Options.WebRtcAllowedOrigins,
            MediaAudioOutput = Options.MediaAudioOutput,
            MediaDecoder = Options.MediaDecoder,
            Capabilities = Options.Capabilities,
            DiagramLibraryUrls = Options.DiagramLibraryUrls,
            Raster = Options.Raster ?? new Folio.Skia.SkiaRasterBackend(),
        };
        _document?.Dispose();
        _document = Document.Parse(html, options);
        _document.Input.LinkActivated += OnDocumentLinkActivated;
        _document.Input.CursorChanged += OnDocumentCursorChanged;
        _document.Input.TooltipChanged += OnDocumentTooltipChanged;
        _document.Input.CopyRequested += OnDocumentCopyRequested;
        _document.Input.CutRequested += OnDocumentCutRequested;
        _document.Input.PasteRequested += OnDocumentPasteRequested;
        _document.Input.ButtonClicked += (_, e) => ButtonClicked?.Invoke(this, e);
        _document.Input.FormSubmitted += (_, e) => FormSubmitted?.Invoke(this, e);
        _document.Input.FocusChanged += OnDocumentFocusChanged;
        _document.Input.SelectionChanged += OnDocumentSelectionChanged;
        if (Options.Scripting == ScriptingPolicy.ContentProcess && !string.IsNullOrWhiteSpace(Options.ContentProcessPath))
        {
            _contentBitmap?.Dispose();
            _contentBitmap = null;
            _contentFrameStore?.Dispose();
            _contentFrameStore = null;
            _contentFramePath = null;
            var previous = RetireAttachedContentProcess(clearFrame: true);
            if (previous is not null)
                DisposeRetiredContentProcess(previous);
            try
            {
                AttachContentProcess(ContentProcessClient.Start(Options.ContentProcessPath, resourceLoader: Options.ResourceLoader, artifactStorage: Options.ArtifactStorage, artifactId: Options.ArtifactId, documentLimits: Options.Limits));
                var process = _contentProcess!;
                _contentFramePath = process.FramePath;
                QueueContentLoad(process, new
                {
                    html,
                    url = baseUri?.AbsoluteUri ?? "content",
                    capabilities = Options.Capabilities,
                    framePath = _contentFramePath,
                    viewportWidth = Math.Max(1, ClientSize.Width),
                    viewportHeight = Math.Max(1, ClientSize.Height),
                });
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                OnScriptStopped(this, new ContentProcessStoppedEventArgs($"unable to start content process: {exception.Message}"));
            }
        }
        else
        {
            var previous = RetireAttachedContentProcess(clearFrame: true);
            if (previous is not null)
                DisposeRetiredContentProcess(previous);
        }
        _laidOutWidth = -1;
        _scrollBar.Value = 0;
        _timeline.Reset();
        DiagnosticsChanged?.Invoke(this, new DiagnosticsEventArgs(_document.Diagnostics));
        Invalidate();
    }

    private void OnDocumentFocusChanged(object? sender, FocusChangedEventArgs e)
    {
        if (_accessibleObject is not null && e.FocusedElement is not null)
        {
            var accObj = _accessibleObject.FindObjectForNode(e.FocusedElement.Node);
            if (accObj is not null)
                AccessibilityNotifyClients(AccessibleEvents.Focus, _accessibleObject.GetChildIndex((FolioNodeAccessibleObject)accObj));
        }
    }

    private void OnDocumentSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_accessibleObject is not null)
            AccessibilityNotifyClients(AccessibleEvents.Selection, -1);
    }

    // Device pixels per CSS pixel.
    private float PixelScale => DeviceDpi / 96f * _zoom;

    private float ViewportWidth => Math.Max(1, (ClientSize.Width - (_scrollBar.Visible ? _scrollBar.Width : 0)) / PixelScale);

    private float ViewportHeight => Math.Max(1, ClientSize.Height / PixelScale);

    // Lays the document out again when the viewport width changed; the scroll bar appears when the page is taller.
    private void EnsureLayout()
    {
        if (_document is null || Math.Abs(_laidOutWidth - ViewportWidth) < 0.01f)
            return;
        for (var pass = 0; pass < 2; pass++)
        {
            (_list, _contentHeight) = _document.Paint(ViewportWidth, ViewportHeight, PixelScale, new HarfBuzzShaper(), Time);
            var needsBar = _contentHeight > ViewportHeight;
            if (needsBar == _scrollBar.Visible)
                break;
            _scrollBar.Visible = needsBar; // the width changed: lay out once more
        }
        _laidOutWidth = ViewportWidth;
        _scrollBar.Maximum = (int)Math.Ceiling(_contentHeight * PixelScale);
        _scrollBar.LargeChange = Math.Max(1, ClientSize.Height);
        if (_document.AnimationsRunning(Time) && !_frames.Enabled)
        {
            _frames.Interval = FrameInterval();
            _frames.Start();
        }
    }

    // Seconds on the document timeline: how long the document has been shown.
    private double Time => _timeline.Elapsed.TotalSeconds;

    private bool IsShown => Visible && IsHandleCreated && ClientSize.Width > 0 && ClientSize.Height > 0
        && FindForm() is not { WindowState: FormWindowState.Minimized };

    // One animation frame: only animated elements are styled again when every animation is paint-only; otherwise the
    // next paint lays the document out again at the new time. Frames stop when no animation changes any more.
    private void NextFrame()
    {
        if (_document is null || !IsShown)
        {
            _timeline.Stop();
            return;
        }
        _timeline.Start();
        try
        {
            if (_document.PaintFrame(Time) is { } list)
                _list = list;
            else
                _laidOutWidth = -1;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _frames.Stop();
            RenderFailed?.Invoke(this, new RenderFailedEventArgs(ex));
            return;
        }
        Invalidate();
        if (!_document.AnimationsRunning(Time))
            _frames.Stop();
    }

    // The display's refresh interval in milliseconds (16 when unknown).
    private int FrameInterval()
    {
        using var graphics = CreateGraphics();
        var hdc = graphics.GetHdc();
        try
        {
            var hz = GetDeviceCaps(hdc, VRefresh);
            return hz > 1 ? Math.Max(1, 1000 / hz) : 16;
        }
        finally
        {
            graphics.ReleaseHdc(hdc);
        }
    }

    // Windows' "Show animations in Windows" setting (SPI_GETCLIENTAREAANIMATION); off means the reader prefers reduced motion.
    private static bool SystemReducedMotion()
    {
        var animations = 1;
        return SystemParametersInfo(GetClientAreaAnimation, 0, ref animations, 0) && animations == 0;
    }

    private const int VRefresh = 116;
    private const uint GetClientAreaAnimation = 0x1042;

    [DllImport("gdi32.dll")]
    private static extern int GetDeviceCaps(IntPtr hdc, int index);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint action, uint param, ref int value, uint winIni);


    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(BackColor);
        if (_contentBitmap is not null)
        {
            e.Graphics.DrawImageUnscaled(_contentBitmap, 0, 0);
            PaintScriptStoppedNotice(e.Graphics);
            Rendered?.Invoke(this, EventArgs.Empty);
            return;
        }
        if (_document is null)
        {
            PaintScriptStoppedNotice(e.Graphics);
            return;
        }
        try
        {
            if (IsShown)
                _timeline.Start();
            EnsureLayout();
            _accessibleObject?.SyncTree(_document.AccessibilityTree());
            var width = Math.Max(1, ClientSize.Width - (_scrollBar.Visible ? _scrollBar.Width : 0));
            var height = Math.Max(1, ClientSize.Height);
            using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
            using (var canvas = new SKCanvas(bitmap))
            {
                canvas.Clear((_document?.ColorScheme ?? Options.ColorScheme) == ColorScheme.Dark ? new SKColor(18, 18, 18) : SKColors.White);
                canvas.Scale(PixelScale);
                canvas.Translate(0, -ScrollTop);
                DisplayListPlayer.Replay(_list!, new SkiaCanvas(canvas, subpixelText: SystemInformation.FontSmoothingType == 2),
                    new RectF(0, ScrollTop, width / PixelScale, height / PixelScale));
            }
            using var image = new Bitmap(width, height, bitmap.RowBytes, PixelFormat.Format32bppPArgb, bitmap.GetPixels());
            e.Graphics.DrawImageUnscaled(image, 0, 0);
            PaintScriptStoppedNotice(e.Graphics);
            Rendered?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            RenderFailed?.Invoke(this, new RenderFailedEventArgs(ex));
        }
    }

    private void PaintScriptStoppedNotice(Graphics graphics)
    {
        if (_scriptStoppedReason is not { } reason || ClientSize.Width <= 0 || ClientSize.Height <= 0) return;
        var plain = FormatPlainReason(reason);
        var left = Math.Min(12, ClientSize.Width - 1);
        var top = Math.Min(12, ClientSize.Height - 1);
        using var brush = new SolidBrush(Color.FromArgb(235, Color.White));
        graphics.FillRectangle(brush, left, top, Math.Min(ClientSize.Width - left, 520), Math.Min(ClientSize.Height - top, 44));
        using var textBrush = new SolidBrush(Color.DarkRed);
        graphics.DrawString($"Script stopped: {plain}", Font, textBrush, left + 8, top + 13);
    }

    private static string FormatPlainReason(string reason)
    {
        if (reason.Contains("heartbeat", StringComparison.OrdinalIgnoreCase) || reason.Contains("timed out", StringComparison.OrdinalIgnoreCase) || reason.Contains("too long", StringComparison.OrdinalIgnoreCase))
            return "ran too long";
        if (reason.Contains("memory", StringComparison.OrdinalIgnoreCase))
            return "used too much memory";
        if (reason.Contains("exit", StringComparison.OrdinalIgnoreCase) || reason.Contains("crash", StringComparison.OrdinalIgnoreCase) || reason.Contains("closed", StringComparison.OrdinalIgnoreCase) || reason.Contains("protocol", StringComparison.OrdinalIgnoreCase))
            return "crashed";
        return reason;
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        _mediaCaptureIndicator.Location = new Point(
            Math.Max(0, ClientSize.Width - _mediaCaptureIndicator.Width - _scrollBar.Width - 8), 8);
        _mediaCaptureIndicator.BringToFront();
        Invalidate();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (_contentProcess is not null)
            SendContentInput(new ContentInputEvent("wheel", e.X / PixelScale, e.Y / PixelScale + ScrollTop, DeltaY: -e.Delta));
        if (!_scrollBar.Visible)
            return;
        // Each notch scrolls the system's number of lines, 16 CSS pixels each.
        var max = Math.Max(0, _scrollBar.Maximum - _scrollBar.LargeChange + 1);
        var step = (int)(e.Delta / 120f * SystemInformation.MouseWheelScrollLines * 16 * PixelScale);
        _scrollBar.Value = Math.Clamp(_scrollBar.Value - step, 0, max);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_document is { } doc)
        {
            doc.Input.HandlePointerMove(ToPointerEvent(e, PointerButton.None));
            if (_contentProcess is not null)
            {
                var pointer = ToPointerEvent(e, PointerButton.None);
                SendContentInput(new ContentInputEvent("pointermove", pointer.Position.X, pointer.Position.Y,
                    Buttons: (int)pointer.Buttons, Modifiers: (int)pointer.Modifiers));
            }
            if ((e.Button & MouseButtons.Left) != 0)
                Invalidate();
        }
        else if (_contentProcess is not null)
        {
            var pointer = ToPointerEvent(e, PointerButton.None);
            SendContentInput(new ContentInputEvent("pointermove", pointer.Position.X, pointer.Position.Y,
                Buttons: (int)pointer.Buttons, Modifiers: (int)pointer.Modifiers));
        }
        else
            Cursor = Cursors.Default;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (_document is { } doc)
        {
            doc.Input.HandlePointerDown(ToPointerEvent(e, ToButton(e.Button)));
            if (_contentProcess is not null)
            {
                var pointer = ToPointerEvent(e, ToButton(e.Button));
                SendContentInput(new ContentInputEvent("pointerdown", pointer.Position.X, pointer.Position.Y,
                    (int)pointer.Button, Buttons: (int)pointer.Buttons, Modifiers: (int)pointer.Modifiers));
            }
            Invalidate();
        }
        else if (_contentProcess is not null)
        {
            var pointer = ToPointerEvent(e, ToButton(e.Button));
            SendContentInput(new ContentInputEvent("pointerdown", pointer.Position.X, pointer.Position.Y,
                (int)pointer.Button, Buttons: (int)pointer.Buttons, Modifiers: (int)pointer.Modifiers));
            Invalidate();
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (_document is { } doc)
        {
            doc.Input.HandlePointerUp(ToPointerEvent(e, ToButton(e.Button)));
            if (_contentProcess is not null)
            {
                var pointer = ToPointerEvent(e, ToButton(e.Button));
                SendContentInput(new ContentInputEvent("pointerup", pointer.Position.X, pointer.Position.Y,
                    (int)pointer.Button, Buttons: (int)pointer.Buttons, Modifiers: (int)pointer.Modifiers));
            }
            Invalidate();
        }
        else if (_contentProcess is not null)
        {
            var pointer = ToPointerEvent(e, ToButton(e.Button));
            SendContentInput(new ContentInputEvent("pointerup", pointer.Position.X, pointer.Position.Y,
                (int)pointer.Button, Buttons: (int)pointer.Buttons, Modifiers: (int)pointer.Modifiers));
            Invalidate();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _document?.Input.HandlePointerLeave();
        if (_contentProcess is not null)
            SendContentInput(new ContentInputEvent("pointerleave"));
    }

    private void OnDocumentLinkActivated(object? sender, Folio.LinkActivatedEventArgs e)
    {
        var args = new LinkActivatedEventArgs(e.Uri, e.Modifiers);
        LinkActivated?.Invoke(this, args);
        e.Handled = args.Handled;
        if (!args.Handled && e.Uri.Scheme is "http" or "https" or "mailto")
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
    }

    private void OnDocumentCursorChanged(object? sender, CursorChangedEventArgs e)
    {
        Cursor = MapCursor(e.Cursor);
    }

    private void OnDocumentTooltipChanged(object? sender, TooltipChangedEventArgs e)
    {
        _toolTip.SetToolTip(this, e.Tooltip);
    }

    private static Cursor MapCursor(string cursor) => cursor switch
    {
        "pointer" => Cursors.Hand,
        "text" or "vertical-text" => Cursors.IBeam,
        "crosshair" => Cursors.Cross,
        "help" => Cursors.Help,
        "wait" => Cursors.WaitCursor,
        "progress" => Cursors.AppStarting,
        "not-allowed" or "no-drop" => Cursors.No,
        "move" or "all-scroll" => Cursors.SizeAll,
        "col-resize" or "ew-resize" or "w-resize" or "e-resize" => Cursors.SizeWE,
        "row-resize" or "ns-resize" or "n-resize" or "s-resize" => Cursors.SizeNS,
        "nesw-resize" or "ne-resize" or "sw-resize" => Cursors.SizeNESW,
        "nwse-resize" or "nw-resize" or "se-resize" => Cursors.SizeNWSE,
        _ => Cursors.Default,
    };

    private PointerEvent ToPointerEvent(MouseEventArgs e, PointerButton button)
    {
        var pos = new System.Numerics.Vector2(e.Location.X / PixelScale, e.Location.Y / PixelScale + ScrollTop);
        var buttons = PointerButtons.None;
        if ((e.Button & MouseButtons.Left) != 0) buttons |= PointerButtons.Primary;
        if ((e.Button & MouseButtons.Right) != 0) buttons |= PointerButtons.Secondary;
        if ((e.Button & MouseButtons.Middle) != 0) buttons |= PointerButtons.Middle;

        var modifiers = KeyModifiers.None;
        if ((ModifierKeys & Keys.Alt) != 0) modifiers |= KeyModifiers.Alt;
        if ((ModifierKeys & Keys.Control) != 0) modifiers |= KeyModifiers.Control;
        if ((ModifierKeys & Keys.Shift) != 0) modifiers |= KeyModifiers.Shift;

        return new PointerEvent(pos, button, buttons, e.Clicks, modifiers);
    }

    private static PointerButton ToButton(MouseButtons btn) => btn switch
    {
        MouseButtons.Left => PointerButton.Primary,
        MouseButtons.Right => PointerButton.Secondary,
        MouseButtons.Middle => PointerButton.Middle,
        _ => PointerButton.None,
    };

    private void OnDocumentCopyRequested(object? sender, EventArgs e)
    {
        CopySelection();
    }

    private void OnDocumentCutRequested(object? sender, EventArgs e)
    {
        CopySelection();
    }

    private void OnDocumentPasteRequested(object? sender, PasteEventArgs e)
    {
        try
        {
            if (Clipboard.ContainsText())
            {
                e.Text = Clipboard.GetText();
                e.Handled = true;
            }
        }
        catch (ExternalException)
        {
            // Clipboard may be locked by another application
        }
    }

    internal static string BuildWindowsClipboardHtml(string fragment) =>
        Folio.Interaction.RangeSerializer.ToWindowsClipboardHtml(fragment);

    protected override bool IsInputKey(Keys keyData)
    {
        switch (keyData & Keys.KeyCode)
        {
            case Keys.Left:
            case Keys.Right:
            case Keys.Up:
            case Keys.Down:
            case Keys.Tab:
            case Keys.PageUp:
            case Keys.PageDown:
            case Keys.Home:
            case Keys.End:
            case Keys.Back:
            case Keys.Delete:
            case Keys.Enter:
                return true;
            default:
                return base.IsInputKey(keyData);
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        var modifiers = KeyModifiers.None;
        if (e.Alt) modifiers |= KeyModifiers.Alt;
        if (e.Control) modifiers |= KeyModifiers.Control;
        if (e.Shift) modifiers |= KeyModifiers.Shift;

        var key = e.KeyCode switch
        {
            Keys.A => "a",
            Keys.C => "c",
            Keys.X => "x",
            Keys.V => "v",
            Keys.Z => "z",
            Keys.Y => "y",
            Keys.Back => "Backspace",
            Keys.Delete => "Delete",
            Keys.Down => "ArrowDown",
            Keys.Up => "ArrowUp",
            Keys.Right => "ArrowRight",
            Keys.Left => "ArrowLeft",
            Keys.PageDown => "PageDown",
            Keys.PageUp => "PageUp",
            Keys.Home => "Home",
            Keys.End => "End",
            Keys.Space => " ",
            Keys.Tab => "Tab",
            Keys.Enter => "Enter",
            _ => e.KeyCode.ToString(),
        };

        var code = e.KeyCode switch
        {
            Keys.A => "KeyA",
            Keys.C => "KeyC",
            Keys.X => "KeyX",
            Keys.V => "KeyV",
            Keys.Z => "KeyZ",
            Keys.Y => "KeyY",
            Keys.Back => "Backspace",
            Keys.Delete => "Delete",
            _ => e.KeyCode.ToString(),
        };

        if (_document is { } doc)
        {
            doc.Input.HandleKeyDown(new KeyEvent(key, code, modifiers));
            if (_contentProcess is not null)
                SendContentInput(new ContentInputEvent("keydown", Key: key, Code: code, Modifiers: (int)modifiers));
            Invalidate();
        }
        else if (_contentProcess is not null)
        {
            SendContentInput(new ContentInputEvent("keydown", Key: key, Code: code, Modifiers: (int)modifiers));
            Invalidate();
        }
    }

    protected override void OnKeyPress(KeyPressEventArgs e)
    {
        base.OnKeyPress(e);
        if (!char.IsControl(e.KeyChar))
        {
            if (_document is { } doc)
            {
                doc.Input.HandleTextInput(new TextInputEvent(e.KeyChar.ToString()));
                if (_contentProcess is not null)
                    SendContentInput(new ContentInputEvent("textinput", Text: e.KeyChar.ToString()));
                e.Handled = true;
                Invalidate();
            }
            else if (_contentProcess is not null)
            {
                SendContentInput(new ContentInputEvent("textinput", Text: e.KeyChar.ToString()));
                e.Handled = true;
                Invalidate();
            }
        }
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (_contentProcess is null)
            return;

        var modifiers = KeyModifiers.None;
        if (e.Alt) modifiers |= KeyModifiers.Alt;
        if (e.Control) modifiers |= KeyModifiers.Control;
        if (e.Shift) modifiers |= KeyModifiers.Shift;
        SendContentInput(new ContentInputEvent("keyup", Key: e.KeyCode.ToString(), Code: e.KeyCode.ToString(), Modifiers: (int)modifiers));
    }

    private Uri? LinkAt(Point point) => _document?.LinkAt(point.X / PixelScale, point.Y / PixelScale + ScrollTop);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            ContentProcessClient? process;
            try
            {
                process = RetireAttachedContentProcess(clearFrame: true);
                if (process is not null)
                    DisposeRetiredContentProcess(process);
            }
            finally
            {
                try
                {
                    _frames.Dispose();
                    _toolTip.Dispose();
                    _document?.Dispose();
                }
                finally { base.Dispose(disposing); }
            }
            return;
        }
        base.Dispose(disposing);
    }
}

public sealed record SelectOptionItem(int Index, string Value, string Text, bool Selected, bool Disabled);

public sealed class SelectDropdownEventArgs(string? selectId, int selectedIndex, IReadOnlyList<SelectOptionItem> options) : EventArgs
{
    public string? SelectId { get; } = selectId;
    public int SelectedIndex { get; } = selectedIndex;
    public IReadOnlyList<SelectOptionItem> Options { get; } = options;
}
