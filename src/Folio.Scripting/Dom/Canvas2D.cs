using System.Globalization;
using Folio.Dom;
using Folio.Imaging;
using Folio.Painting;
using Folio.Resources;
using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;

namespace Folio.Scripting.Dom;

internal sealed class MediaElementState : IImageHandle, IMediaPresentation, IMediaFrameSource, IFeatureFallbackSource
{
    public MediaElementState(IScriptEngine engine, IHostObjectTemplate eventTemplate, ElementNode element, bool enabled, bool allowSoundAutoplay = false, IResourceLoader? resourceLoader = null, Uri? baseUri = null, IMediaAudioOutput? audioOutput = null, IMediaDecoder? decoder = null, int maxMediaBytes = 128 * 1024 * 1024, MediaResourceBudget? mediaBudget = null, bool soundEnabled = false, Func<bool>? checkSoundPermission = null, Func<bool>? checkMediaPermission = null, Func<ScriptValue, MediaCaptureVideoFrame?>? captureFrame = null, Func<ScriptValue, MediaAudioFrame?>? captureAudio = null, bool deferLoad = false)
    {
        Engine = engine;
        EventTemplate = eventTemplate;
        Element = element;
        Enabled = enabled;
        AllowSoundAutoplay = allowSoundAutoplay;
        Muted = element.GetAttribute("muted") is not null;
        DefaultMuted = Muted;
        Autoplay = element.GetAttribute("autoplay") is not null;
        Controls = element.GetAttribute("controls") is not null;
        _resourceLoader = resourceLoader;
        _baseUri = baseUri;
        _audioOutput = audioOutput;
        _decoder = decoder;
        _maxMediaBytes = Math.Max(0, maxMediaBytes);
        _budget = mediaBudget ?? new MediaResourceBudget(_maxMediaBytes, 1);
        _soundEnabled = soundEnabled;
        _checkSoundPermission = checkSoundPermission;
        _checkMediaPermission = checkMediaPermission;
        _captureFrame = captureFrame;
        _captureAudio = captureAudio;
        _capabilityDisabled = !enabled;
        element.MediaPresentation = this;
        if (!deferLoad) Initialize();
    }

    internal void Initialize()
    {
        LoadResource();
        if (!_retired) LoadTextTracks();
    }

    public ElementNode Element { get; }
    public IScriptEngine Engine { get; }
    public IHostObjectTemplate EventTemplate { get; }
    public bool Enabled { get; private set; }
    public bool AllowSoundAutoplay { get; }
    private bool _captureVideo;
    private int _captureWidth = 640;
    private int _captureHeight = 360;
    public int Width => _captureVideo ? _captureWidth : int.TryParse(Element.GetAttribute("data-width"), out var width) ? Math.Clamp(width, 0, 4096) : _frameImage?.Width ?? 300;
    public int Height => _captureVideo ? _captureHeight : int.TryParse(Element.GetAttribute("data-height"), out var height) ? Math.Clamp(height, 0, 4096) : _frameImage?.Height ?? 150;
    private IImageHandle? _frameImage;
    private MediaVideoFrame[]? _videoFrames;
    private readonly IMediaAudioOutput? _audioOutput;
    private readonly IMediaDecoder? _decoder;
    private readonly int _maxMediaBytes;
    private readonly MediaResourceBudget _budget;
    private volatile bool _soundEnabled;
    private readonly Func<bool>? _checkSoundPermission;
    private readonly Func<bool>? _checkMediaPermission;
    private readonly Func<ScriptValue, MediaCaptureVideoFrame?>? _captureFrame;
    private readonly Func<ScriptValue, MediaAudioFrame?>? _captureAudio;
    private ScriptValue _captureTrack = ScriptValue.Undefined;
    private bool _soundPlaybackAuthorized;
    private MediaResourceBudget.Lease? _reservation;
    private MediaResourceBudget.Lease? _posterReservation;
    private MediaResourceBudget.Lease? _captureReservation;
    private long _loadGeneration;
    private long _posterGeneration;
    private bool _posterSharesMediaSource;
    private bool _retired;
    private bool _hasSource;
    private string _currentSource = "";
    private MediaAudioFrame? _audioFrame;
    private int _audioCursor;
    private byte[]? _framePixels;
    public ReadOnlyMemory<byte> Pixels
    {
        get
        {
            RefreshCaptureFrame();
            return _frameImage?.Pixels ?? (_framePixels ??= new byte[Math.Min((long)Width * Height * 4, 16 * 1024 * 1024)]);
        }
    }
    public double CurrentTime { get; private set; }
    public double Duration { get; private set; }
    public bool Paused { get; set; } = true;
    public bool Ended { get; set; }
    public double Volume { get; set; } = 1;
    public IMediaPoster? Poster { get; private set; }
    // A decoded video frame is the presentation while playback is active. While paused at time zero the poster (or
    // the media element's native placeholder) remains visible until play starts.
    public IMediaPoster? CurrentFrame
    {
        get
        {
            RefreshCaptureFrame();
            return !Paused && _frameImage is { } image ? new FrameImage(image)
                : _captureVideo && _framePixels is { } pixels ? new MediaPosterImage(Width, Height, pixels) : null;
        }
    }

    private sealed class FrameImage(IImageHandle image) : IMediaPoster
    {
        public int Width => image.Width;
        public int Height => image.Height;
        public ReadOnlyMemory<byte> Pixels => image.Pixels;
    }

    private static IMediaPoster? DecodePoster(byte[]? bytes)
    {
        if (bytes is null) return null;
        if (ImageLoader.Decode(bytes) is { } decoded)
            return new MediaPosterImage(decoded.Width, decoded.Height, decoded.Pixels.ToArray());
        var text = Encoding.UTF8.GetString(bytes);
        if (!text.Contains("<svg", StringComparison.OrdinalIgnoreCase))
            return null;
        static int Attribute(string text, string name, int fallback) =>
            int.TryParse(Regex.Match(text, $"\\b{name}\\s*=\\s*['\\\"](\\d+)").Groups[1].Value, out var value)
                ? Math.Clamp(value, 1, 4096) : fallback;
        var width = Attribute(text, "width", 640);
        var height = Attribute(text, "height", 360);
        var fill = Regex.Match(text, "<rect\\b[^>]*\\bfill\\s*=\\s*['\\\"]#([0-9a-fA-F]{6})(?:[0-9a-fA-F]{2})?['\\\"]", RegexOptions.IgnoreCase).Groups[1].Value;
        if (fill.Length != 6) return null;
        var color = Convert.ToUInt32(fill, 16);
        var pixels = new byte[width * height * 4];
        for (var offset = 0; offset < pixels.Length; offset += 4)
        {
            pixels[offset] = (byte)(color >> 16);
            pixels[offset + 1] = (byte)(color >> 8);
            pixels[offset + 2] = (byte)color;
            pixels[offset + 3] = 255;
        }
        return new MediaPosterImage(width, height, pixels);
    }
    public bool Muted { get; set; }
    public bool Autoplay { get; set; }
    public bool Loop { get; set; }
    public bool Controls { get; set; }
    public string Preload { get; set; } = "metadata";
    public bool DefaultMuted { get; set; }
    public string CrossOrigin { get; set; } = "";
    public double PlaybackRate { get; set; } = 1;
    public double DefaultPlaybackRate { get; set; } = 1;
    public bool PreservesPitch { get; set; } = true;
    public ScriptValue SrcObject { get; private set; } = ScriptValue.Null;
    public void SetSrcObject(ScriptValue value)
    {
        var generation = ++_loadGeneration;
        ReleaseResource();
        SrcObject = value;
        _captureVideo = false;
        _captureWidth = 640;
        _captureHeight = 360;
        _framePixels = null;
        _captureTrack = ScriptValue.Undefined;
        if (_retired || !Enabled || _checkMediaPermission is not null && !_checkMediaPermission())
        {
            Error = "Media is disabled by the host.";
            _fallback = new(Element.LocalName == "video" ? FallbackFeature.Video : FallbackFeature.Audio, FallbackReason.HostDenied);
            return;
        }
        Error = null;
        _fallback = null;
        Duration = CurrentTime = 0;
        Ended = false;
        Paused = true;
        AudioFlush();
        if (!LoadStillCurrent(generation)) return;
        AudioStop();
        if (!LoadStillCurrent(generation)) return;
        if (!value.IsObject || !Engine.TryGetArrayItems(Engine.GetProperty(value, "_tracks"), out var tracks))
            return;
        if (!LoadStillCurrent(generation)) return;
        var video = false;
        var audio = false;
        _audioFrame = null;
        foreach (var track in tracks)
        {
            var kind = Engine.GetProperty(track, "kind").AsString();
            var live = Engine.GetProperty(track, "readyState").AsString() == "live";
            if (!LoadStillCurrent(generation)) return;
            video |= live && kind == "video";
            audio |= live && kind == "audio";
            if (live && kind == "audio") _audioFrame ??= _captureAudio?.Invoke(track);
        }
        _captureVideo = video;
        _hasSource = video || audio;
        if (_captureVideo)
        {
            _framePixels = CreateCaptureFrame(tracks);
            _captureReservation = _budget.AcquireBytes();
            var captureBytes = (long)_framePixels.LongLength;
            if (_captureReservation is null || !_captureReservation.Resize(captureBytes))
            {
                _captureReservation?.Dispose();
                _captureReservation = null;
                _captureVideo = false;
                _hasSource = audio;
                Error = "Captured video exceeds the host media budget.";
                _fallback = new(FallbackFeature.Video, FallbackReason.ResourceLimit);
                return;
            }
            foreach (var track in tracks)
                if (Engine.GetProperty(track, "kind").AsString() == "video" && Engine.GetProperty(track, "readyState").AsString() == "live")
                { _captureTrack = track; break; }
        }
        if (video || audio)
            _fallback = Element.LocalName == "audio" && !_soundEnabled ? new(FallbackFeature.Sound, FallbackReason.HostDenied) : null;
    }

    private byte[] CreateCaptureFrame(IReadOnlyList<ScriptValue> tracks)
    {
        if (_captureFrame is not null)
        {
            foreach (var track in tracks)
            {
                var kind = Engine.GetProperty(track, "kind").AsString();
                if (kind != "video") continue;
                var frame = _captureFrame(track);
                if (frame is { IsValid: true } && frame.Value.Rgba.Length <= 16 * 1024 * 1024)
                {
                    _captureWidth = frame.Value.Width;
                    _captureHeight = frame.Value.Height;
                    return frame.Value.Rgba.ToArray();
                }
            }
        }
        var width = Width;
        var height = Height;
        var pixels = new byte[Math.Min((long)width * height * 4, 16 * 1024 * 1024)];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var offset = (y * width + x) * 4;
            pixels[offset] = (byte)(32 + (x * 160 / Math.Max(1, width - 1)));
            pixels[offset + 1] = (byte)(96 + (y * 96 / Math.Max(1, height - 1)));
            pixels[offset + 2] = 192;
            pixels[offset + 3] = 255;
        }
        return pixels;
    }

    private void RefreshCaptureFrame()
    {
        if (!_captureVideo || !_captureTrack.IsObject || _captureFrame is null) return;
        var frame = _captureFrame(_captureTrack);
        if (frame is not { IsValid: true } || frame.Value.Rgba.Length > 16 * 1024 * 1024) return;
        _captureWidth = frame.Value.Width;
        _captureHeight = frame.Value.Height;
        _framePixels = frame.Value.Rgba.ToArray();
    }
    public int ReadyState => Enabled && Error is null && _hasSource ? 4 : 0;
    public int NetworkState => Enabled && Error is null && _hasSource ? 1 : 0;
    public string? Error { get; set; }
    private bool _capabilityDisabled;
    private FeatureFallback? _fallback;
    public FeatureFallback? Fallback => _fallback is { Feature: FallbackFeature.Sound }
        ? (_soundEnabled ? null : _fallback)
        : _fallback ?? (Enabled && _hasSource && Element.LocalName == "audio" && !_soundEnabled
            ? new(FallbackFeature.Sound, FallbackReason.HostDenied) : null);
    public long SourceBytes { get; private set; }
    public List<MediaTextTrackState> TextTracks { get; } = [];
    public IReadOnlyList<IMediaCue> ActiveCues => TextTracks.SelectMany(track => track.ActiveCues).Cast<IMediaCue>().ToArray();
    public void TogglePlayPause() { if (Paused) Play(); else Pause(); }
    public void SeekBy(double seconds) => Seek(CurrentTime + seconds);
    public void ToggleMute() => SetMuted(!Muted);
    public void SetMuted(bool muted)
    {
        if (Muted == muted) return;
        Muted = muted;
        // A blocked audible autoplay is a policy failure, not a permanent media
        // error. Once the element is muted, the caller may retry play().
        if (muted && Error?.Contains("autoplay", StringComparison.OrdinalIgnoreCase) == true)
        {
            Error = null;
            _fallback = null;
        }
        AudioFlush();
        Dispatch(OnVolumeChange, "volumechange");
    }
    public void SetVolume(double volume)
    {
        if (!double.IsFinite(volume)) throw WebIdlException.TypeError("The media volume must be finite.");
        volume = Math.Clamp(volume, 0, 1);
        if (Volume == volume) return;
        Volume = volume;
        AudioFlush();
        Dispatch(OnVolumeChange, "volumechange");
    }
    public void Seek(double value)
    {
        if (!double.IsFinite(value)) throw WebIdlException.TypeError("The media position must be finite.");
        value = Duration > 0 ? Math.Clamp(value, 0, Duration) : Math.Max(0, value);
        AudioFlush();
        if (_audioFrame is { } frame) _audioCursor = AudioPosition(frame, value);
        Dispatch(OnSeeking, "seeking");
        var wasEnded = Ended;
        SetCurrentTime(value);
        if (Ended && !wasEnded) Dispatch(OnEnded, "ended");
        Dispatch(OnSeeked, "seeked");
    }
    internal void StopPlayback()
    {
        _loadGeneration++;
        _retired = true;
        Enabled = false;
        Error = "Media is disabled by the host.";
        _fallback = new(Element.LocalName == "video" ? FallbackFeature.Video : FallbackFeature.Audio, FallbackReason.HostDenied);
        Paused = true;
        AudioStop();
        ReleaseResource();
    }
    private static int AudioPosition(MediaAudioFrame frame, double time)
    {
        var requested = Math.Floor(Math.Max(0, time) * frame.SampleRate) * frame.Channels;
        return requested >= frame.Samples.Length ? frame.Samples.Length : (int)requested;
    }
    private void SetCurrentTime(double value)
    {
        var previousTime = CurrentTime;
        CurrentTime = Math.Max(0, value);
        SelectVideoFrame();
        if (CurrentTime < previousTime && _audioFrame is { } rewind)
            _audioCursor = AudioPosition(rewind, CurrentTime);
        foreach (var track in TextTracks)
        {
            var before = track.ActiveCues.ToArray();
            track.CurrentTime = CurrentTime;
            var after = track.ActiveCues.ToArray();
            foreach (var cue in before.Except(after))
                if (cue.OnExit.IsFunction) Engine.Call(cue.OnExit, ScriptValue.Undefined, [Engine.CreateHostObject(EventTemplate, new MediaEventState("exit"))]);
            foreach (var cue in after.Except(before))
                if (cue.OnEnter.IsFunction) Engine.Call(cue.OnEnter, ScriptValue.Undefined, [Engine.CreateHostObject(EventTemplate, new MediaEventState("enter"))]);
            if (!before.SequenceEqual(after))
            {
                var eventValue = Engine.CreateHostObject(EventTemplate, new MediaEventState("cuechange"));
                if (track.OnCueChange.IsFunction) Engine.Call(track.OnCueChange, ScriptValue.Undefined, [eventValue]);
                foreach (var listener in track.CueChangeListeners.ToArray())
                    if (listener.IsFunction) Engine.Call(listener, ScriptValue.Undefined, [eventValue]);
            }
        }
        Ended = Duration > 0 && CurrentTime >= Duration;
        Dispatch(OnTimeUpdate, "timeupdate");
    }

    private void LoadTextTracks()
    {
        foreach (var element in Element.Children.OfType<ElementNode>().Where(child => child.Name.Namespace == Namespaces.Html && child.LocalName == "track"))
        {
            var kind = element.GetAttribute("kind") ?? "subtitles";
            var track = new MediaTextTrackState(kind, element.GetAttribute("label") ?? "", element.GetAttribute("srclang") ?? "")
            {
                Mode = element.GetAttribute("default") is not null ? "showing" : "disabled"
            };
            var bytes = LoadSource(element.GetAttribute("src"), _resourceLoader, _baseUri).Data;
            if (bytes is not null)
            {
                foreach (var cue in WebVttParser.Parse(System.Text.Encoding.UTF8.GetString(bytes)))
                    track.Cues.Add(new TextTrackCueState(cue.StartTime, cue.EndTime, cue.Text, cue.Id));
            }
            TextTracks.Add(track);
        }
    }
    public void RejectForBudget(string message = "Media resource exceeds the host budget.")
    {
        Error = message;
        _fallback = new(Element.LocalName == "video" ? FallbackFeature.Video : FallbackFeature.Audio, FallbackReason.ResourceLimit);
    }
    public void SetCapability(bool enabled)
    {
        if (Enabled == enabled && !_capabilityDisabled) return;
        Enabled = enabled;
        if (!enabled)
        {
            _loadGeneration++;
            _capabilityDisabled = true;
            Paused = true;
            AudioPause();
            AudioFlush();
            ReleaseResource();
            Error = "Media is disabled by the host.";
            _fallback = new(Element.LocalName == "video" ? FallbackFeature.Video : FallbackFeature.Audio, FallbackReason.HostDenied);
            Dispatch(OnError, "error");
        }
        else if (_capabilityDisabled)
        {
            _capabilityDisabled = false;
            Error = null;
            Load();
        }
    }
    /// <summary>Updates the host's independent sound permission without changing decoded media or playback time.</summary>
    public void SetSoundCapability(bool enabled)
    {
        var wasEnabled = _soundEnabled;
        _soundEnabled = enabled;
        if (wasEnabled && !enabled)
        {
            _soundPlaybackAuthorized = false;
            AudioPause();
            AudioFlush();
            AudioStop();
        }
    }
    public ScriptValue OnLoadedMetadata { get; set; } = ScriptValue.Null;
    public ScriptValue OnLoadedData { get; set; } = ScriptValue.Null;
    public ScriptValue OnCanPlay { get; set; } = ScriptValue.Null;
    public ScriptValue OnCanPlayThrough { get; set; } = ScriptValue.Null;
    public ScriptValue OnLoadStart { get; set; } = ScriptValue.Null;
    public ScriptValue OnDurationChange { get; set; } = ScriptValue.Null;
    public ScriptValue OnPlaying { get; set; } = ScriptValue.Null;
    public ScriptValue OnTimeUpdate { get; set; } = ScriptValue.Null;
    public ScriptValue OnRateChange { get; set; } = ScriptValue.Null;
    public ScriptValue OnPlay { get; set; } = ScriptValue.Null;
    public ScriptValue OnPause { get; set; } = ScriptValue.Null;
    public ScriptValue OnEnded { get; set; } = ScriptValue.Null;
    public ScriptValue OnVolumeChange { get; set; } = ScriptValue.Null;
    public ScriptValue OnError { get; set; } = ScriptValue.Null;
    public ScriptValue OnSeeking { get; set; } = ScriptValue.Null;
    public ScriptValue OnSeeked { get; set; } = ScriptValue.Null;
    private readonly Dictionary<string, List<ScriptValue>> _listeners = new(StringComparer.Ordinal);
    private readonly IResourceLoader? _resourceLoader;
    private readonly Uri? _baseUri;
    public void AddEventListener(string type, ScriptValue listener)
    {
        if (!listener.IsFunction) return;
        if (!_listeners.TryGetValue(type, out var list)) _listeners[type] = list = [];
        if (!list.Contains(listener)) list.Add(listener);
    }
    public void RemoveEventListener(string type, ScriptValue listener)
    {
        if (_listeners.TryGetValue(type, out var list)) list.Remove(listener);
    }
    public void Dispatch(ScriptValue handler, string type)
    {
        var eventValue = Engine.CreateHostObject(EventTemplate, new MediaEventState(type));
        if (handler.IsFunction) Engine.Call(handler, ScriptValue.Undefined, [eventValue]);
        if (_listeners.TryGetValue(type, out var list)) foreach (var listener in list.ToArray()) if (listener.IsFunction) Engine.Call(listener, ScriptValue.Undefined, [eventValue]);
    }

    public bool Play(bool autoplay = false)
    {
        var permitted = _checkMediaPermission?.Invoke() ?? Enabled;
        if (!permitted || !Enabled || Error is not null) { Error ??= "Media playback is unavailable."; return false; }
        if (autoplay && !Muted && !DefaultMuted && !AllowSoundAutoplay) { Error = "Audible autoplay is blocked by the host policy."; return false; }
        if (!Paused)
        {
            // A sound-capability revocation may leave media playback running silently. A
            // subsequent play() after re-grant must re-arm the output without duplicating
            // the play/playing lifecycle events for an already-running element.
            if (_audioFrame is not null && !_soundPlaybackAuthorized)
            {
                _soundPlaybackAuthorized = !Muted && Volume > 0 && SoundPermissionGranted();
                if (_soundPlaybackAuthorized)
                {
                    AudioStart();
                    SubmitAudioThrough(CurrentTime);
                }
            }
            return true;
        }
        if (Ended) Seek(0);
        Paused = false;
        _soundPlaybackAuthorized = _audioFrame is not null && !Muted && Volume > 0 ? SoundPermissionGranted() : _soundEnabled;
        Ended = false;
        if (_audioFrame is not null)
        {
            AudioStart();
            SubmitAudioThrough(CurrentTime);
        }
        Dispatch(OnPlay, "play");
        Dispatch(OnPlaying, "playing");
        return true;
    }

    public void Pause()
    {
        if (Paused) return;
        Paused = true;
        AudioPause();
        Dispatch(OnPause, "pause");
    }

    internal string CurrentSource => _currentSource;

    internal void AdvancePlayback(double seconds)
    {
        if (Paused || Ended || Duration <= 0 || seconds <= 0 || PlaybackRate <= 0) return;
        var next = CurrentTime + seconds * PlaybackRate;
        if (next >= Duration)
        {
            SetCurrentTime(Duration);
            SubmitAudioThrough(CurrentTime);
            if (Loop && !Paused)
            {
                // Keep the completed iteration queued while scheduling the next one.
                SetCurrentTime(0);
                AudioStart();
                SubmitAudioThrough(CurrentTime);
                Dispatch(OnPlay, "play");
                Dispatch(OnPlaying, "playing");
            }
            else
            {
                Paused = true;
                // The final queued PCM must drain. Stop/reset is reserved for teardown.
                Dispatch(OnPause, "pause");
                Dispatch(OnEnded, "ended");
            }
            return;
        }
        SetCurrentTime(next);
        SubmitAudioThrough(CurrentTime);
    }

    public void Load()
    {
        var previousDuration = Duration;
        var beforeGeneration = _loadGeneration;
        LoadResource();
        if (_loadGeneration != beforeGeneration + 1) return;
        _framePixels = null;
        _audioCursor = 0;
        AudioFlush();
        CurrentTime = 0;
        Paused = true;
        Ended = false;
        if (Error is { }) Dispatch(OnError, "error");
        else
        {
            Dispatch(OnLoadStart, "loadstart");
            if (Duration != previousDuration) Dispatch(OnDurationChange, "durationchange");
            Dispatch(OnLoadedMetadata, "loadedmetadata");
            Dispatch(OnLoadedData, "loadeddata");
            Dispatch(OnCanPlay, "canplay");
            Dispatch(OnCanPlayThrough, "canplaythrough");
        }
    }

    public string CanPlayType(string type)
    {
        var mime = type.Trim().ToLowerInvariant().Split(';', 2)[0];
        var supported = mime switch
        {
            "audio/wav" or "audio/x-wav" or "audio/wave" or "audio/mpeg" or "audio/mp3" or "audio/ogg" or
            "video/mp4" or "video/webm" or "video/ogg" => "maybe",
            _ => "",
        };
        if (supported.Length == 0 || _decoder is null) return supported;
        return _decoder.CanDecode(mime, Element.LocalName.Equals("video", StringComparison.OrdinalIgnoreCase)) ? supported : "";
    }

    private static (byte[]? Data, string? ContentType) LoadSource(
        string? source,
        IResourceLoader? resourceLoader,
        Uri? baseUri,
        ResourceKind kind = ResourceKind.Media)
    {
        if (string.IsNullOrWhiteSpace(source)) return (null, null);
        if (source.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var comma = source.IndexOf(',');
            if (comma < 0) return (null, null);
            try
            {
                var payload = source[(comma + 1)..];
                var metadata = source[5..comma];
                var contentType = metadata.Split(';', 2)[0] is { Length: > 0 } type ? type : null;
                var data = metadata.Contains(";base64", StringComparison.OrdinalIgnoreCase)
                    ? Convert.FromBase64String(payload)
                    : System.Text.Encoding.UTF8.GetBytes(Uri.UnescapeDataString(payload));
                return (data, contentType);
            }
            catch (FormatException) { return (null, null); }
        }
        if (resourceLoader is null) return (null, null);
        if (baseUri is not null && Uri.TryCreate(baseUri, source, out var resolved)) source = resolved.AbsoluteUri;
        if (!Uri.TryCreate(source, UriKind.Absolute, out var uri)) return (null, null);
        try
        {
            var response = resourceLoader.LoadAsync(new ResourceRequest(uri.AbsoluteUri, kind), CancellationToken.None).GetAwaiter().GetResult();
            return response is null ? (null, null) : (response.Data, response.ContentType);
        }
        catch { return (null, null); }
    }

    private string? SelectMediaSource()
    {
        var direct = Element.GetAttribute("src");
        if (!string.IsNullOrWhiteSpace(direct))
            return direct;
        foreach (var child in Element.Children.OfType<ElementNode>())
        {
            if (child.LocalName != "source")
                continue;
            var source = child.GetAttribute("src");
            if (string.IsNullOrWhiteSpace(source))
                continue;
            var type = child.GetAttribute("type");
            var mediaType = type?.Trim().ToLowerInvariant().Split(';', 2)[0];
            if (!string.IsNullOrWhiteSpace(type)
                && (Element.LocalName == "audio" && mediaType is not null && !mediaType.StartsWith("audio/", StringComparison.Ordinal)
                    || Element.LocalName == "video" && mediaType is not null && !mediaType.StartsWith("video/", StringComparison.Ordinal)))
                continue;
            if (string.IsNullOrWhiteSpace(type) || CanPlayType(type) is not "")
                return source;
        }
        return null;
    }

    private static double ParseMediaDuration(byte[]? bytes)
    {
        var wav = ParseWavDuration(bytes);
        return wav > 0 ? wav : ParseMpegDuration(bytes);
    }

    private void ReleaseResource()
    {
        _reservation?.Dispose();
        _reservation = null;
        _captureReservation?.Dispose();
        _captureReservation = null;
        ReleasePoster();
        _frameImage = null;
        _videoFrames = null;
        _audioFrame = null;
        _framePixels = null;
        SourceBytes = 0;
        _hasSource = false;
        _currentSource = "";
        _posterSharesMediaSource = false;
    }

    private void LoadResource()
    {
        var generation = ++_loadGeneration;
        ReleaseResource();
        if (_retired) return;
        Error = null;
        _fallback = null;
        var selectedSource = SelectMediaSource();
        _posterSharesMediaSource = !string.IsNullOrWhiteSpace(selectedSource)
            && string.Equals(Element.GetAttribute("poster"), selectedSource, StringComparison.Ordinal);
        if (!_posterSharesMediaSource)
            RefreshPoster();
        if (generation != _loadGeneration || _retired) return;
        Duration = double.TryParse(Element.GetAttribute("data-duration"), out var declaredDuration) ? Math.Max(0, declaredDuration) : 0;
        if (!Enabled || _checkMediaPermission is not null && !_checkMediaPermission())
        {
            Error = "Media is disabled by the host.";
            _fallback = new(Element.LocalName == "video" ? FallbackFeature.Video : FallbackFeature.Audio, FallbackReason.HostDenied);
            return;
        }
        var source = selectedSource;
        var video = Element.LocalName == "video";
        if (string.IsNullOrWhiteSpace(source))
            return;
        _currentSource = source;
        _reservation = _budget.Acquire();
        if (_reservation is null) { RejectForBudget("Media decoder limit exceeded."); return; }
        _hasSource = true;
        var sourceResult = LoadSource(source, _resourceLoader, _baseUri);
        var bytes = sourceResult.Data;
        if (!LoadStillCurrent(generation))
        {
            if (generation == _loadGeneration)
            {
                Error = "Media is disabled by the host.";
                _fallback = new(video ? FallbackFeature.Video : FallbackFeature.Audio, FallbackReason.HostDenied);
                ReleaseResource();
            }
            return;
        }
        SourceBytes = bytes?.LongLength ?? (long.TryParse(Element.GetAttribute("data-size"), out var declaredSize) ? Math.Max(0, declaredSize) : 0);
        if (!string.IsNullOrWhiteSpace(source) && bytes is null)
        {
            Error = "Media resource could not be loaded.";
            _fallback = new(Element.LocalName == "video" ? FallbackFeature.Video : FallbackFeature.Audio,
                _resourceLoader is null ? FallbackReason.SourceUnavailable : FallbackReason.SourceRefused);
            ReleaseResource();
            return;
        }
        if (!_reservation.Resize(SourceBytes)) { RejectResourceBudget(); return; }
        if (bytes is not { Length: > 0 })
        {
            _fallback = new(video ? FallbackFeature.Video : FallbackFeature.Audio, FallbackReason.SourceUnavailable);
            ReleaseResource();
            return;
        }
        var outputLimit = (int)Math.Max(0, Math.Min(_maxMediaBytes, _reservation.AvailableBytes - SourceBytes));
        if (outputLimit == 0) { RejectResourceBudget(); return; }
        MediaDecodeResult? decoded = null;
        if (_decoder is not null && outputLimit > 0)
        {
            try { decoded = _decoder.Decode(bytes, sourceResult.ContentType, video, outputLimit); }
            catch
            {
                if (generation != _loadGeneration) return;
                Error = "Media decoder failed.";
                _fallback = new(video ? FallbackFeature.Video : FallbackFeature.Audio, FallbackReason.DecodeFailed);
            }
        }
        if (!LoadStillCurrent(generation))
        {
            if (generation == _loadGeneration)
            {
                Error = "Media is disabled by the host.";
                _fallback = new(video ? FallbackFeature.Video : FallbackFeature.Audio, FallbackReason.HostDenied);
                ReleaseResource();
            }
            return;
        }
        if (decoded is not null)
        {
            var frame = decoded.VideoFrame;
            var frames = decoded.VideoFrames?.ToArray();
            if (frames is { Length: > 0 })
                frame = frames[0].Frame;
            var audio = decoded.Audio;
            // Check host output before retaining it or exposing it to canvas/audio.
            if (frame is not null && (frame.Width <= 0 || frame.Height <= 0 || frame.Width > outputLimit / 4 / frame.Height))
            { RejectResourceBudget(); return; }
            var frameBytes = 0L;
            if (frames is not null && !ValidateVideoFrames(frames, outputLimit, out frameBytes))
            { RejectResourceBudget(); return; }
            var pixelBytes = frames is { Length: > 0 } ? frameBytes : frame is null ? 0 : (long)frame.Width * frame.Height * 4;
            var audioBytes = audio is null ? 0 : (long)audio.Value.Samples.Length * sizeof(float);
            if (pixelBytes < 0 || frames is null && frame is not null && (frame.Width <= 0 || frame.Height <= 0 || frame.Pixels.Length != pixelBytes) ||
                audio is { } pcm && (pcm.SampleRate <= 0 || pcm.Channels <= 0 || pcm.Samples.Length % pcm.Channels != 0) ||
                pixelBytes > outputLimit || audioBytes > outputLimit - pixelBytes)
            { RejectResourceBudget(); return; }
            _videoFrames = frames;
            _frameImage = frames is { Length: > 0 } ? frames[0].Frame : frame;
            _audioFrame = audio;
        }
        else if (video)
            _frameImage = PngDecoder.CanDecode(bytes) ? PngDecoder.Decode(bytes, outputLimit / 4)
                : JpegDecoder.CanDecode(bytes) ? JpegDecoder.Decode(bytes, outputLimit / 4) : null;
        else
        {
            _audioFrame = DecodeWav(bytes, outputLimit, out var exceedsBudget);
            if (exceedsBudget) { RejectResourceBudget(); return; }
        }
        // A timestamped video decode retains the complete bounded sequence, not
        // only the frame currently selected for painting. Charge every retained
        // frame to the shared document budget so playback cannot under-account
        // decoded pixels while advancing its clock.
        var videoBytes = _videoFrames is { Length: > 0 }
            ? _videoFrames.Sum(frame => (long)frame.Frame.Pixels.Length)
            : _frameImage?.Pixels.Length ?? 0;
        var allocated = SourceBytes + videoBytes + (long)(_audioFrame?.Samples.Length ?? 0) * sizeof(float);
        if (!_reservation.Resize(allocated)) { RejectResourceBudget(); return; }
        if (Duration <= 0) Duration = decoded?.Duration > 0 ? decoded.Duration : ParseMediaDuration(bytes);
        if (!LooksLikeMedia(bytes) && _frameImage is null && _audioFrame is null)
        {
            Error ??= "Media resource is corrupt or truncated.";
            _fallback ??= new(video ? FallbackFeature.Video : FallbackFeature.Audio, FallbackReason.UnsupportedFormat);
        }
        else if (_frameImage is null && _audioFrame is null || !video && _audioFrame is null)
        {
            Error ??= "Media decoder failed.";
            _fallback ??= new(video ? FallbackFeature.Video : FallbackFeature.Audio, FallbackReason.DecodeFailed);
        }
        else
            _fallback = !video && !_soundEnabled
                ? new(FallbackFeature.Sound, FallbackReason.HostDenied)
                : null;
        if (_posterSharesMediaSource)
        {
            if (_frameImage is not null && _videoFrames is null)
                Poster = new FrameImage(_frameImage);
            else
            {
                _posterSharesMediaSource = false;
                RefreshPoster();
            }
        }
        if (Error is not null) ReleaseResource();
    }

    private bool ValidateVideoFrames(MediaVideoFrame[] frames, int outputLimit, out long totalBytes)
    {
        totalBytes = 0;
        var previous = -1d;
        foreach (var item in frames)
        {
            if (!double.IsFinite(item.Time) || item.Time < 0 || item.Time < previous || item.Frame is null ||
                item.Frame.Width <= 0 || item.Frame.Height <= 0 || item.Frame.Width > outputLimit / 4 / item.Frame.Height)
                return false;
            var bytes = checked((long)item.Frame.Width * item.Frame.Height * 4);
            if (item.Frame.Pixels.Length != bytes || bytes > outputLimit - totalBytes)
                return false;
            totalBytes += bytes;
            previous = item.Time;
        }
        return true;
    }

    private void SelectVideoFrame()
    {
        if (_videoFrames is not { Length: > 0 } frames) return;
        var selected = frames[0].Frame;
        foreach (var frame in frames)
        {
            if (frame.Time > CurrentTime) break;
            selected = frame.Frame;
        }
        _frameImage = selected;
    }

    internal void RefreshPoster()
    {
        if (_retired) return;
        var generation = ++_posterGeneration;
        var source = Element.GetAttribute("poster");
        if (_posterSharesMediaSource && _frameImage is not null && _videoFrames is null
            && string.Equals(source, SelectMediaSource(), StringComparison.Ordinal))
        {
            Poster = new FrameImage(_frameImage);
            return;
        }
        _posterSharesMediaSource = false;
        var bytes = LoadSource(source, _resourceLoader, _baseUri, ResourceKind.Image).Data;
        if (generation != _posterGeneration || _retired || Element.GetAttribute("poster") != source) return;
        ReleasePoster();
        var poster = DecodePoster(bytes);
        if (poster is null) { Poster = null; return; }
        var reservation = _budget.AcquireBytes();
        var posterBytes = (bytes?.LongLength ?? 0) + poster.Pixels.Length;
        if (reservation is null || !reservation.Resize(posterBytes))
        {
            reservation?.Dispose();
            Poster = null;
            RejectForBudget("Media poster exceeds the host budget.");
            return;
        }
        _posterReservation = reservation;
        Poster = poster;
    }

    private void ReleasePoster()
    {
        _posterReservation?.Dispose();
        _posterReservation = null;
        Poster = null;
    }

    private void RejectResourceBudget()
    {
        ReleaseResource();
        RejectForBudget();
    }

    private bool LoadStillCurrent(long generation)
    {
        if (generation != _loadGeneration || !Enabled) return false;
        try { return _checkMediaPermission?.Invoke() ?? true; }
        catch { return false; }
    }

    private void SubmitAudioThrough(double time)
    {
        if (_audioOutput is null || _audioFrame is not { } frame || frame.SampleRate <= 0 || frame.Channels <= 0) return;
        var target = AudioPosition(frame, time);
        if (_audioCursor == 0 && target == 0)
            target = Math.Min(frame.Samples.Length, Math.Max(1, frame.Channels * Math.Max(1, frame.SampleRate / 50)));
        if (Muted || Volume <= 0)
        {
            _audioCursor = target;
            return;
        }
        if (!SoundPermissionGranted() || !_soundPlaybackAuthorized) { _audioCursor = target; return; }
        var chunkSamples = Math.Max(frame.Channels, frame.Channels * Math.Max(1, frame.SampleRate / 50));
        while (_audioCursor < target)
        {
            if (!SoundPermissionGranted() || !_soundPlaybackAuthorized) { _audioCursor = target; return; }
            var length = Math.Min(chunkSamples, target - _audioCursor);
            var samples = frame.Samples.Slice(_audioCursor, length).ToArray();
            if (Volume < 1) for (var i = 0; i < samples.Length; i++) samples[i] *= (float)Volume;
            _audioOutput.Submit(new MediaAudioFrame(frame.SampleRate, frame.Channels, samples));
            _audioCursor += length;
        }
    }

    private void AudioStart()
    {
        if (Muted || Volume <= 0) return;
        if (!SoundPermissionGranted() || !_soundPlaybackAuthorized) return;
        if (_audioOutput is IMediaAudioOutputLifecycle lifecycle) lifecycle.Start();
    }

    private void AudioPause()
    {
        if (_audioOutput is IMediaAudioOutputLifecycle lifecycle) lifecycle.Pause();
    }

    private void AudioFlush()
    {
        if (_audioOutput is IMediaAudioOutputLifecycle lifecycle) lifecycle.Flush();
    }

    private void AudioStop()
    {
        if (_audioOutput is IMediaAudioOutputLifecycle lifecycle) lifecycle.Stop();
    }

    private bool SoundPermissionGranted()
    {
        var granted = _soundEnabled;
        try
        {
            if (_checkSoundPermission is not null) granted = _checkSoundPermission() && _soundEnabled;
        }
        catch { granted = false; }
        if (granted) return true;
        if (_soundPlaybackAuthorized)
        {
            _soundPlaybackAuthorized = false;
            AudioPause();
            AudioFlush();
            AudioStop();
        }
        return false;
    }

    private static bool LooksLikeMedia(byte[] bytes) =>
        bytes.Length >= 12 && ((bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8) && bytes.AsSpan(8, 4).SequenceEqual("WAVE"u8)) ||
        bytes.AsSpan(0, 4).SequenceEqual("OggS"u8) || bytes.AsSpan(0, 3).SequenceEqual("ID3"u8) ||
        (bytes[0] == 0xFF && (bytes[1] & 0xE0) == 0xE0) || bytes.AsSpan(4, 4).SequenceEqual("ftyp"u8) ||
        (bytes[0] == 0x1A && bytes[1] == 0x45 && bytes[2] == 0xDF && bytes[3] == 0xA3));

    private static double ParseMpegDuration(byte[]? bytes)
    {
        if (bytes is null || bytes.Length < 4) return 0;
        var offset = 0;
        if (bytes.Length >= 10 && bytes[0] == 'I' && bytes[1] == 'D' && bytes[2] == '3')
            offset = 10 + ((bytes[6] & 0x7F) << 21) + ((bytes[7] & 0x7F) << 14) + ((bytes[8] & 0x7F) << 7) + (bytes[9] & 0x7F);
        var samples = 0L;
        var sampleRate = 0;
        for (var cursor = offset; cursor + 4 <= bytes.Length;)
        {
            if (bytes[cursor] != 0xFF || (bytes[cursor + 1] & 0xE0) != 0xE0) { cursor++; continue; }
            var version = (bytes[cursor + 1] >> 3) & 3;
            var layer = (bytes[cursor + 1] >> 1) & 3;
            var bitrateIndex = (bytes[cursor + 2] >> 4) & 15;
            var rateIndex = (bytes[cursor + 2] >> 2) & 3;
            if (version != 3 || layer != 1 || bitrateIndex is 0 or 15 || rateIndex == 3) { cursor++; continue; }
            var bitrates = new[] { 0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 0 };
            var rates = new[] { 44100, 48000, 32000, 0 };
            var bitrate = bitrates[bitrateIndex] * 1000;
            sampleRate = rates[rateIndex];
            var frameLength = 144 * bitrate / sampleRate;
            if (frameLength < 1 || cursor + frameLength > bytes.Length) break;
            samples += 1152;
            cursor += frameLength;
        }
        return samples > 0 && sampleRate > 0 ? (double)samples / sampleRate : 0;
    }

    private static double ParseWavDuration(byte[]? bytes)
    {
        if (bytes is null || bytes.Length < 44 || bytes[0] != 'R' || bytes[1] != 'I' || bytes[2] != 'F' || bytes[3] != 'F' || bytes[8] != 'W' || bytes[9] != 'A' || bytes[10] != 'V' || bytes[11] != 'E') return 0;
        var sampleRate = 0; var blockAlign = 0; var dataLength = 0;
        for (var offset = 12; offset + 8 <= bytes.Length;)
        {
            var chunkSize = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset + 4, 4));
            if (chunkSize < 0 || offset + 8 + chunkSize > bytes.Length) break;
            if (bytes[offset] == 'f' && bytes[offset + 1] == 'm' && bytes[offset + 2] == 't' && bytes[offset + 3] == ' ' && chunkSize >= 16)
            {
                sampleRate = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset + 12, 4));
                blockAlign = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(offset + 20, 2));
            }
            else if (bytes[offset] == 'd' && bytes[offset + 1] == 'a' && bytes[offset + 2] == 't' && bytes[offset + 3] == 'a') dataLength = chunkSize;
            offset += 8 + chunkSize + (chunkSize & 1);
        }
        return sampleRate > 0 && blockAlign > 0 && dataLength > 0 ? (double)dataLength / (sampleRate * blockAlign) : 0;
    }

    private static MediaAudioFrame? DecodeWav(byte[] bytes, int maxOutputBytes, out bool exceedsBudget)
    {
        exceedsBudget = false;
        if (bytes.Length < 44 || !bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8) || !bytes.AsSpan(8, 4).SequenceEqual("WAVE"u8)) return null;
        var format = 0; var channels = 0; var sampleRate = 0; var bits = 0; var dataOffset = -1; var dataLength = 0;
        for (var offset = 12; offset + 8 <= bytes.Length;)
        {
            var chunkSize = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset + 4, 4));
            if (chunkSize < 0 || (long)offset + 8 + chunkSize > bytes.Length) return null;
            if (bytes.AsSpan(offset, 4).SequenceEqual("fmt "u8) && chunkSize >= 16)
            {
                format = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(offset + 8, 2));
                channels = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(offset + 10, 2));
                sampleRate = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset + 12, 4));
                bits = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(offset + 22, 2));
            }
            else if (bytes.AsSpan(offset, 4).SequenceEqual("data"u8)) { dataOffset = offset + 8; dataLength = chunkSize; }
            offset += 8 + chunkSize + (chunkSize & 1);
        }
        if (format is not (1 or 3) || channels is < 1 or > 8 || sampleRate is < 8000 or > 192000 || bits is not (8 or 16 or 24 or 32) || dataOffset < 0 || dataLength == 0)
            return null;
        var bytesPerSample = bits / 8;
        var sampleCount = dataLength / bytesPerSample;
        if (sampleCount <= 0) return null;
        if (sampleCount > Math.Min(16 * 1024 * 1024, maxOutputBytes / sizeof(float)))
        { exceedsBudget = true; return null; }
        var samples = new float[sampleCount];
        for (var i = 0; i < sampleCount; i++)
        {
            var at = dataOffset + i * bytesPerSample;
            samples[i] = format == 3 && bits == 32
                ? BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(at, 4)))
                : bits switch
                {
                    8 => (bytes[at] - 128) / 128f,
                    16 => BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(at, 2)) / 32768f,
                    24 => ReadPcm24(bytes, at) / 8388608f,
                    _ => BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(at, 4)) / 2147483648f,
                };
        }
        return new MediaAudioFrame(sampleRate, channels, samples);
    }

    private static int ReadPcm24(byte[] bytes, int offset)
    {
        var value = bytes[offset] | bytes[offset + 1] << 8 | bytes[offset + 2] << 16;
        return (value & 0x800000) != 0 ? value | unchecked((int)0xFF000000) : value;
    }
}

internal sealed class MediaPosterImage(int width, int height, byte[] pixels) : IImageHandle, IMediaPoster
{
    public int Width { get; } = width;
    public int Height { get; } = height;
    public ReadOnlyMemory<byte> Pixels { get; } = pixels;
}

internal sealed class MediaTextTrackState(string kind, string label, string language)
{
    public string Kind { get; } = kind;
    public string Label { get; } = label;
    public string Language { get; } = language;
    private string _mode = "disabled";
    public string Mode
    {
        get => _mode;
        set
        {
            if (value is not ("disabled" or "hidden" or "showing"))
                throw WebIdlException.TypeError("TextTrack.mode must be disabled, hidden, or showing.");
            _mode = value;
        }
    }
    public List<TextTrackCueState> Cues { get; } = [];
    public List<ScriptValue> CueChangeListeners { get; } = [];
    public ScriptValue OnCueChange { get; set; } = ScriptValue.Null;
    public double CurrentTime { get; set; }
    public IReadOnlyList<TextTrackCueState> ActiveCues => Mode == "disabled"
        ? []
        : Cues.Where(cue => CurrentTime >= cue.StartTime && CurrentTime < cue.EndTime).ToArray();
}

internal sealed class TextTrackCueState : IMediaCue
{
    public TextTrackCueState(double startTime, double endTime, string text, string id = "")
    {
        StartTime = Math.Max(0, startTime);
        EndTime = Math.Max(StartTime, endTime);
        Text = text;
        Id = id;
    }
    public double StartTime { get; }
    public double EndTime { get; }
    public string Text { get; }
    public string Id { get; }
    public ScriptValue OnEnter { get; set; } = ScriptValue.Null;
    public ScriptValue OnExit { get; set; } = ScriptValue.Null;
}

internal sealed class MediaTimeRangesState(IReadOnlyList<(double Start, double End)> ranges)
{
    public IReadOnlyList<(double Start, double End)> Ranges { get; } = ranges;
}

internal sealed partial class WebGlContextState
{
    private readonly IScriptEngine _engine;
    private readonly CanvasElementState _canvas;
    private int _maxGpuBytes;
    private uint _nextHandle = 1;
    public Dictionary<uint, byte[]> Buffers { get; } = [];
    public Dictionary<uint, double[]> BufferValues { get; } = [];
    public Dictionary<uint, (int Type, string Source, bool Compiled, string InfoLog)> Shaders { get; } = [];
    public HashSet<uint> Programs { get; } = [];
    public Dictionary<uint, List<uint>> ProgramShaders { get; } = [];
    public HashSet<uint> LinkedPrograms { get; } = [];
    public Dictionary<uint, string> ProgramInfoLogs { get; } = [];
    public float ClearDepth { get; set; } = 1;
    public uint DepthFunction { get; set; } = 0x0203;
    public bool DepthMask { get; set; } = true;
    public uint BlendSourceFactor { get; set; } = 1;
    public uint BlendDestinationFactor { get; set; } = 0;
    public uint BlendSourceAlphaFactor { get; set; } = 1;
    public uint BlendDestinationAlphaFactor { get; set; } = 0;
    public uint BlendEquation { get; set; } = 0x8006;
    public uint BlendAlphaEquation { get; set; } = 0x8006;
    public bool ColorMaskRed { get; set; } = true;
    public bool ColorMaskGreen { get; set; } = true;
    public bool ColorMaskBlue { get; set; } = true;
    public bool ColorMaskAlpha { get; set; } = true;
    public uint StencilFunction { get; set; } = 0x0207;
    public int StencilReference { get; set; }
    public uint StencilMask { get; set; } = 0xFFFFFFFF;
    public uint StencilFail { get; set; }
    public uint StencilDepthFail { get; set; }
    public uint StencilDepthPass { get; set; }
    public int ClearStencil { get; set; }
    public bool Alpha { get; set; } = true;
    public bool Depth { get; set; } = true;
    public bool Stencil { get; set; } = false;
    public bool Antialias { get; set; } = false;
    public bool PremultipliedAlpha { get; set; } = true;
    public bool PreserveDrawingBuffer { get; set; } = false;
    public bool UnpackFlipY { get; set; } = false;
    public bool UnpackPremultiplyAlpha { get; set; } = false;
    public int UnpackAlignment { get; set; } = 4;
    public int PackAlignment { get; set; } = 4;
    public uint UnpackColorspaceConversion { get; set; } = 0x9244;
    public uint? BoundReadFramebuffer { get; set; }
    public uint? BoundDrawFramebuffer { get; set; }
    public HashSet<uint> Samplers { get; } = [];
    public Dictionary<uint, Dictionary<uint, double>> SamplerParameters { get; } = [];
    public Dictionary<uint, uint?> BoundSamplers { get; } = [];
    public Dictionary<uint, Dictionary<uint, int>> TextureParameters { get; } = [];
    public List<uint> DrawBuffers { get; } = [];
    public uint ReadBuffer { get; set; } = 0x0405;
    public HashSet<uint> VertexArrays { get; } = [];
    public HashSet<uint> Syncs { get; } = [];
    public HashSet<uint> Queries { get; } = [];
    public Dictionary<uint, bool> QueryResults { get; } = [];
    public uint? ActiveQuery { get; set; }
    public uint ActiveTextureUnit { get; set; } = 0x84C0;
    public uint? BoundFramebuffer { get; set; }
    public uint? BoundRenderbuffer { get; set; }
    public uint? BoundArrayBuffer { get; set; }
    public uint? CurrentProgram { get; set; }
    public uint? BoundVertexArray { get; set; }
    public int DrawCalls { get; private set; }
    public int ViewportX { get; set; }
    public int ViewportY { get; set; }
    public int ViewportWidth { get; set; } = 300;
    public int ViewportHeight { get; set; } = 150;
    public int ScissorX { get; set; }
    public int ScissorY { get; set; }
    public int ScissorWidth { get; set; }
    public int ScissorHeight { get; set; }
    public int Error { get; private set; }
    public bool Lost { get; private set; }
    public int AllocatedBytes { get; private set; }
    public CanvasElementState Canvas => _canvas;
    public float ClearR { get; private set; }
    public float ClearG { get; private set; }
    public float ClearB { get; private set; }
    public float ClearA { get; private set; }
    public ScriptValue OnContextLost { get; set; } = ScriptValue.Null;
    public ScriptValue OnContextRestored { get; set; } = ScriptValue.Null;
    private TimeSpan _maxShaderExecutionTime;
    public WebGlContextState(IScriptEngine engine, CanvasElementState canvas, int maxGpuBytes, TimeSpan maxShaderExecutionTime = default)
    {
        _engine = engine;
        _canvas = canvas;
        _maxGpuBytes = Math.Max(0, maxGpuBytes);
        _maxShaderExecutionTime = maxShaderExecutionTime <= TimeSpan.Zero ? TimeSpan.FromSeconds(2) : maxShaderExecutionTime;
    }
    public void UpdateShaderExecutionTime(TimeSpan maxShaderExecutionTime) =>
        _maxShaderExecutionTime = maxShaderExecutionTime <= TimeSpan.Zero ? TimeSpan.FromSeconds(2) : maxShaderExecutionTime;
    public void UpdateBudget(int maxGpuBytes)
    {
        _maxGpuBytes = Math.Max(0, maxGpuBytes);
        if (_maxGpuBytes == 0 || AllocatedBytes > _maxGpuBytes)
        {
            _canvas.MarkGpuFailure(new Folio.Scripting.Gpu.GpuFailure(Folio.Scripting.Gpu.GpuFailureKind.BudgetExceeded, "WebGL GPU memory budget exceeded."));
            Lose();
        }
        else if (Lost && _canvas.Element.OwnerDocument.WebGlAllowed)
        {
            Restore();
        }
    }
    public uint CreateHandle() => _nextHandle++;
    public void AddDrawCall() => DrawCalls++;
    public void SetError(int error) { if (Error == 0) Error = error; }
    public int TakeError() { var error = Error; Error = 0; return error; }
    public void Lose()
    {
        if (Lost) return;
        Lost = true;
        _canvas.MarkContextLost();
        SetError(0x9242);
        try
        {
            _canvas.Element.OwnerDocument.EventDispatcher?.Invoke(
                _canvas.Element,
                "webglcontextlost",
                new EventDispatchArgs { Bubbles = true, Cancelable = true });
        }
        catch { }
        if (OnContextLost.IsFunction)
        {
            try { _engine.Call(OnContextLost, ScriptValue.Undefined, [_engine.Evaluate("({ type: 'webglcontextlost' })")]); }
            catch { }
        }
    }
    public void Restore()
    {
        if (!Lost) return;
        Lost = false;
        _canvas.ProbeGraphics();
        _canvas.ClearFallback();
        try
        {
            _canvas.Element.OwnerDocument.EventDispatcher?.Invoke(
                _canvas.Element,
                "webglcontextrestored",
                new EventDispatchArgs { Bubbles = true, Cancelable = false });
        }
        catch { }
        if (OnContextRestored.IsFunction)
        {
            try { _engine.Call(OnContextRestored, ScriptValue.Undefined, [_engine.Evaluate("({ type: 'webglcontextrestored' })")]); }
            catch { }
        }
    }
    public bool TryAllocate(int bytes)
    {
        if (bytes < 0 || bytes > _maxGpuBytes - AllocatedBytes)
        {
            SetError(0x0505);
            _canvas.Element.OwnerDocument.DiagnosticSink?.Invoke(new Diagnostic(DiagnosticCode.LimitExceeded, Severity.Warning, "WebGL GPU memory budget exceeded.", null, "webgl"));
            return false;
        }
        AllocatedBytes += bytes;
        return true;
    }
    public bool ReplaceBuffer(uint handle, int bytes)
    {
        var oldBytes = Buffers.TryGetValue(handle, out var existing) ? existing.Length : 0;
        var nextTotal = AllocatedBytes - oldBytes + bytes;
        if (bytes < 0 || bytes > _maxGpuBytes || nextTotal > _maxGpuBytes)
        {
            SetError(0x0505);
            _canvas.Element.OwnerDocument.DiagnosticSink?.Invoke(new Diagnostic(DiagnosticCode.LimitExceeded, Severity.Warning, "WebGL GPU memory budget exceeded.", null, "webgl"));
            return false;
        }
        AllocatedBytes = nextTotal;
        Buffers[handle] = new byte[bytes];
        return true;
    }
    public void DeleteBuffer(uint handle)
    {
        if (Buffers.Remove(handle, out var buffer)) AllocatedBytes = Math.Max(0, AllocatedBytes - buffer.Length);
        BufferValues.Remove(handle);
        if (BoundArrayBuffer == handle) BoundArrayBuffer = null;
        if (BoundElementArrayBuffer == handle) BoundElementArrayBuffer = null;
        foreach (var index in VertexAttribBuffers.Where(pair => pair.Value == handle).Select(pair => pair.Key).ToArray())
            VertexAttribBuffers.Remove(index);
    }

    public void DeleteShader(uint handle)
    {
        Shaders.Remove(handle);
        foreach (var pair in ProgramShaders)
            pair.Value.Remove(handle);
    }
    public void DeleteProgram(uint handle)
    {
        Programs.Remove(handle);
        ProgramShaders.Remove(handle);
        LinkedPrograms.Remove(handle);
        ProgramInfoLogs.Remove(handle);
        if (CurrentProgram == handle) CurrentProgram = null;
    }
    public void SetClearColor(float r, float g, float b, float a) => (ClearR, ClearG, ClearB, ClearA) = (Math.Clamp(r, 0, 1), Math.Clamp(g, 0, 1), Math.Clamp(b, 0, 1), Math.Clamp(a, 0, 1));
}
