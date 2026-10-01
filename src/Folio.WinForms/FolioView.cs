using System.ComponentModel;
using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Folio.Painting;
using Folio.Skia;
using SkiaSharp;

namespace Folio.WinForms;

/// <summary>A link the reader activated; set <see cref="Handled"/> to stop the default action (opening the system browser).</summary>
public sealed class LinkActivatedEventArgs(Uri uri) : EventArgs
{
    public Uri Uri { get; } = uri;
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

    public FolioView()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        _scrollBar.ValueChanged += (_, _) => Invalidate();
        Controls.Add(_scrollBar);
        _frames.Tick += (_, _) => NextFrame();
    }

    /// <summary>The options new documents are parsed with.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public FolioOptions Options { get; set; } = new();

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

    public event EventHandler? Rendered;
    public event EventHandler<DiagnosticsEventArgs>? DiagnosticsChanged;
    public event EventHandler<RenderFailedEventArgs>? RenderFailed;

    /// <summary>A link was clicked. Unless handled, http, https and mailto links open in the system browser.</summary>
    public event EventHandler<LinkActivatedEventArgs>? LinkActivated;

    public void LoadHtml(string html, Uri? baseUri = null)
    {
        ArgumentNullException.ThrowIfNull(html);
        var options = new FolioOptions
        {
            BaseUri = baseUri ?? Options.BaseUri, ColorScheme = Options.ColorScheme, ReducedMotion = Options.ReducedMotion || SystemReducedMotion(),
            UserStyleSheet = Options.UserStyleSheet, Limits = Options.Limits, CollectDiagnostics = Options.CollectDiagnostics, Fonts = Options.Fonts,
            ResourceLoader = Options.ResourceLoader,
        };
        _document?.Dispose();
        _document = Document.Parse(html, options);
        _laidOutWidth = -1;
        _scrollBar.Value = 0;
        _timeline.Reset();
        DiagnosticsChanged?.Invoke(this, new DiagnosticsEventArgs(_document.Diagnostics));
        Invalidate();
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

    private float ScrollTop => _scrollBar.Visible ? _scrollBar.Value / PixelScale : 0;

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(BackColor);
        if (_document is null)
            return;
        try
        {
            if (IsShown)
                _timeline.Start();
            EnsureLayout();
            var width = Math.Max(1, ClientSize.Width - (_scrollBar.Visible ? _scrollBar.Width : 0));
            var height = Math.Max(1, ClientSize.Height);
            using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
            using (var canvas = new SKCanvas(bitmap))
            {
                canvas.Clear(_document.Options.ColorScheme == ColorScheme.Dark ? new SKColor(18, 18, 18) : SKColors.White);
                canvas.Scale(PixelScale);
                canvas.Translate(0, -ScrollTop);
                DisplayListPlayer.Replay(_list!, new SkiaCanvas(canvas, subpixelText: SystemInformation.FontSmoothingType == 2));
            }
            using var image = new Bitmap(width, height, bitmap.RowBytes, PixelFormat.Format32bppPArgb, bitmap.GetPixels());
            e.Graphics.DrawImageUnscaled(image, 0, 0);
            Rendered?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            RenderFailed?.Invoke(this, new RenderFailedEventArgs(ex));
        }
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        Invalidate();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
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
        Cursor = LinkAt(e.Location) is null ? Cursors.Default : Cursors.Hand;
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button != MouseButtons.Left || LinkAt(e.Location) is not { } uri)
            return;
        var args = new LinkActivatedEventArgs(uri);
        LinkActivated?.Invoke(this, args);
        if (!args.Handled && uri.Scheme is "http" or "https" or "mailto")
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }

    private Uri? LinkAt(Point point) => _document?.LinkAt(point.X / PixelScale, point.Y / PixelScale + ScrollTop);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _frames.Dispose();
            _document?.Dispose();
        }
        base.Dispose(disposing);
    }
}
