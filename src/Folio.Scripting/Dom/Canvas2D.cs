using System.Globalization;
using Folio.Dom;
using Folio.Imaging;
using Folio.Painting;
using Folio.Resources;
using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;

namespace Folio.Scripting.Dom;

internal sealed class MediaElementState : IImageHandle, IMediaPresentation
{
    public MediaElementState(IScriptEngine engine, IHostObjectTemplate eventTemplate, ElementNode element, bool enabled, bool allowSoundAutoplay = false, IResourceLoader? resourceLoader = null, Uri? baseUri = null, IMediaAudioOutput? audioOutput = null, IMediaDecoder? decoder = null, int maxMediaBytes = 128 * 1024 * 1024)
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
        element.MediaPresentation = this;
        var bytes = LoadSourceBytes(element.GetAttribute("src"), resourceLoader, baseUri);
        var decoded = Decode(bytes, element.LocalName == "video");
        _frameImage = decoded?.VideoFrame ?? (element.LocalName == "video" && bytes is { Length: > 0 } ? ImageLoader.Decode(bytes) : null);
        _audioFrame = decoded?.Audio ?? (element.LocalName == "audio" && bytes is { Length: > 0 } ? DecodeWav(bytes) : null);
        var posterBytes = LoadSourceBytes(element.GetAttribute("poster"), resourceLoader, baseUri);
        Poster = posterBytes is null ? null : DecodePoster(posterBytes);
        Duration = double.TryParse(element.GetAttribute("data-duration"), out var duration) ? Math.Max(0, duration) : decoded?.Duration > 0 ? decoded.Duration : ParseMediaDuration(bytes);
        SourceBytes = bytes?.LongLength ?? (long.TryParse(element.GetAttribute("data-size"), out var size) ? Math.Max(0, size) : 0);
        if (Enabled && bytes is { Length: > 0 } && !LooksLikeMedia(bytes) && _frameImage is null && _audioFrame is null) Error = "Media resource is corrupt or truncated.";
        LoadTextTracks();
    }

    public ElementNode Element { get; }
    public IScriptEngine Engine { get; }
    public IHostObjectTemplate EventTemplate { get; }
    public bool Enabled { get; private set; }
    public bool AllowSoundAutoplay { get; }
    private bool _captureVideo;
    public int Width => _captureVideo ? 640 : int.TryParse(Element.GetAttribute("data-width"), out var width) ? Math.Clamp(width, 0, 4096) : _frameImage?.Width ?? 300;
    public int Height => _captureVideo ? 360 : int.TryParse(Element.GetAttribute("data-height"), out var height) ? Math.Clamp(height, 0, 4096) : _frameImage?.Height ?? 150;
    private IImageHandle? _frameImage;
    private readonly IMediaAudioOutput? _audioOutput;
    private readonly IMediaDecoder? _decoder;
    private readonly int _maxMediaBytes;
    private MediaAudioFrame? _audioFrame;
    private int _audioCursor;
    private byte[]? _framePixels;
    public ReadOnlyMemory<byte> Pixels => _frameImage?.Pixels ?? (_framePixels ??= new byte[Math.Min((long)Width * Height * 4, 16 * 1024 * 1024)]);
    public double CurrentTime { get; private set; }
    public double Duration { get; private set; }
    public bool Paused { get; set; } = true;
    public bool Ended { get; set; }
    public double Volume { get; set; } = 1;
    public IMediaPoster? Poster { get; }

    private static IMediaPoster? DecodePoster(byte[] bytes)
    {
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
        SrcObject = value;
        _captureVideo = false;
        _framePixels = null;
        if (!value.IsObject || !Engine.TryGetArrayItems(Engine.GetProperty(value, "_tracks"), out var tracks))
            return;
        _captureVideo = tracks.Any(track => Engine.GetProperty(track, "kind").AsString() == "video");
        if (_captureVideo)
            _framePixels = CreateCaptureFrame();
    }

    private byte[] CreateCaptureFrame()
    {
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
    public int ReadyState => Enabled && Error is null ? 4 : 0;
    public int NetworkState => Enabled && Error is null ? 1 : 0;
    public string? Error { get; set; }
    private bool _capabilityDisabled;
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
        Dispatch(ScriptValue.Null, "seeking");
        var wasEnded = Ended;
        SetCurrentTime(value);
        if (Ended && !wasEnded) Dispatch(OnEnded, "ended");
        Dispatch(ScriptValue.Null, "seeked");
    }
    internal void StopPlayback()
    {
        Paused = true;
        AudioStop();
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
            var bytes = LoadSourceBytes(element.GetAttribute("src"), _resourceLoader, _baseUri);
            if (bytes is not null)
            {
                foreach (var cue in WebVttParser.Parse(System.Text.Encoding.UTF8.GetString(bytes)))
                    track.Cues.Add(new TextTrackCueState(cue.StartTime, cue.EndTime, cue.Text, cue.Id));
            }
            TextTracks.Add(track);
        }
    }
    public void RejectForBudget(string message = "Media resource exceeds the host budget.") => Error = message;
    public void SetCapability(bool enabled)
    {
        if (Enabled == enabled && !_capabilityDisabled) return;
        Enabled = enabled;
        if (!enabled)
        {
            _capabilityDisabled = true;
            Paused = true;
            AudioPause();
            Error = "Media is disabled by the host.";
            Dispatch(OnError, "error");
        }
        else if (_capabilityDisabled)
        {
            _capabilityDisabled = false;
            Error = null;
            Load();
        }
    }
    public ScriptValue OnLoadedMetadata { get; set; } = ScriptValue.Null;
    public ScriptValue OnLoadedData { get; set; } = ScriptValue.Null;
    public ScriptValue OnCanPlay { get; set; } = ScriptValue.Null;
    public ScriptValue OnPlaying { get; set; } = ScriptValue.Null;
    public ScriptValue OnTimeUpdate { get; set; } = ScriptValue.Null;
    public ScriptValue OnRateChange { get; set; } = ScriptValue.Null;
    public ScriptValue OnPlay { get; set; } = ScriptValue.Null;
    public ScriptValue OnPause { get; set; } = ScriptValue.Null;
    public ScriptValue OnEnded { get; set; } = ScriptValue.Null;
    public ScriptValue OnVolumeChange { get; set; } = ScriptValue.Null;
    public ScriptValue OnError { get; set; } = ScriptValue.Null;
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
        if (!Enabled || Error is not null) { Error ??= "Media playback is unavailable."; return false; }
        if (autoplay && !Muted && !DefaultMuted && !AllowSoundAutoplay) { Error = "Audible autoplay is blocked by the host policy."; return false; }
        if (Ended) Seek(0);
        Paused = false;
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
        Paused = true;
        AudioPause();
        Dispatch(OnPause, "pause");
    }

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
        var bytes = LoadSourceBytes(Element.GetAttribute("src"), _resourceLoader, _baseUri);
        if (bytes is not null)
        {
            SourceBytes = bytes.LongLength;
            var decoded = Decode(bytes, Element.LocalName == "video");
            _frameImage = decoded?.VideoFrame ?? (Element.LocalName == "video" ? ImageLoader.Decode(bytes) : null);
            _audioFrame = decoded?.Audio ?? (Element.LocalName == "audio" ? DecodeWav(bytes) : null);
            Duration = decoded?.Duration > 0 ? decoded.Duration : ParseMediaDuration(bytes);
            Error = LooksLikeMedia(bytes) || _frameImage is not null || _audioFrame is not null ? null : "Media resource is corrupt or truncated.";
        }
        else
        {
            _frameImage = null;
            _audioFrame = null;
        }
        _framePixels = null;
        _audioCursor = 0;
        AudioFlush();
        CurrentTime = 0;
        Paused = true;
        Ended = false;
        if (Error is { }) Dispatch(OnError, "error");
        else
        {
            Dispatch(ScriptValue.Null, "loadstart");
            if (Duration != previousDuration) Dispatch(ScriptValue.Null, "durationchange");
            Dispatch(OnLoadedMetadata, "loadedmetadata");
            Dispatch(OnLoadedData, "loadeddata");
            Dispatch(OnCanPlay, "canplay");
            Dispatch(ScriptValue.Null, "canplaythrough");
        }
    }

    public string CanPlayType(string type)
    {
        if (!Enabled) return "";
        var mime = type.Trim().ToLowerInvariant().Split(';', 2)[0];
        return mime switch
        {
            "audio/wav" or "audio/x-wav" or "audio/wave" or "audio/mpeg" or "audio/mp3" or "audio/ogg" or
            "video/mp4" or "video/webm" or "video/ogg" => "maybe",
            _ => "",
        };
    }

    private static byte[]? LoadSourceBytes(string? source, IResourceLoader? resourceLoader, Uri? baseUri)
    {
        if (string.IsNullOrWhiteSpace(source)) return null;
        if (source.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var comma = source.IndexOf(',');
            if (comma < 0) return null;
            try
            {
                var payload = source[(comma + 1)..];
                return source[..comma].Contains(";base64", StringComparison.OrdinalIgnoreCase)
                    ? Convert.FromBase64String(payload)
                    : System.Text.Encoding.UTF8.GetBytes(Uri.UnescapeDataString(payload));
            }
            catch (FormatException) { return null; }
        }
        if (resourceLoader is null) return null;
        if (baseUri is not null && Uri.TryCreate(baseUri, source, out var resolved)) source = resolved.AbsoluteUri;
        if (!Uri.TryCreate(source, UriKind.Absolute, out var uri)) return null;
        return resourceLoader.LoadAsync(new ResourceRequest(uri.AbsoluteUri, ResourceKind.Media), CancellationToken.None).GetAwaiter().GetResult()?.Data;
    }

    private static double ParseMediaDuration(byte[]? bytes)
    {
        var wav = ParseWavDuration(bytes);
        return wav > 0 ? wav : ParseMpegDuration(bytes);
    }

    private MediaDecodeResult? Decode(byte[]? bytes, bool video)
    {
        if (_decoder is null || bytes is not { Length: > 0 } || bytes.Length > _maxMediaBytes) return null;
        try { return _decoder.Decode(bytes, null, video, _maxMediaBytes); }
        catch { return null; }
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
        var chunkSamples = Math.Max(frame.Channels, frame.Channels * Math.Max(1, frame.SampleRate / 50));
        while (_audioCursor < target)
        {
            var length = Math.Min(chunkSamples, target - _audioCursor);
            var samples = frame.Samples.Slice(_audioCursor, length).ToArray();
            if (Volume < 1) for (var i = 0; i < samples.Length; i++) samples[i] *= (float)Volume;
            _audioOutput.Submit(new MediaAudioFrame(frame.SampleRate, frame.Channels, samples));
            _audioCursor += length;
        }
    }

    private void AudioStart()
    {
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

    private static MediaAudioFrame? DecodeWav(byte[] bytes)
    {
        if (bytes.Length < 44 || !bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8) || !bytes.AsSpan(8, 4).SequenceEqual("WAVE"u8)) return null;
        var format = 0; var channels = 0; var sampleRate = 0; var bits = 0; var dataOffset = -1; var dataLength = 0;
        for (var offset = 12; offset + 8 <= bytes.Length;)
        {
            var chunkSize = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset + 4, 4));
            if (chunkSize < 0 || offset + 8 + chunkSize > bytes.Length) return null;
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
        if (sampleCount <= 0 || sampleCount > 16 * 1024 * 1024) return null;
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

internal sealed class WebGlContextState
{
    private readonly IScriptEngine _engine;
    private readonly CanvasElementState _canvas;
    private readonly int _maxGpuBytes;
    private uint _nextHandle = 1;
    public Dictionary<uint, byte[]> Buffers { get; } = [];
    public Dictionary<uint, double[]> BufferValues { get; } = [];
    public Dictionary<uint, (int Type, string Source, bool Compiled)> Shaders { get; } = [];
    public HashSet<uint> Programs { get; } = [];
    public Dictionary<uint, List<uint>> ProgramShaders { get; } = [];
    public HashSet<uint> LinkedPrograms { get; } = [];
    public HashSet<int> EnabledVertexAttributes { get; } = [];
    public Dictionary<int, (int Size, uint Type, bool Normalized, int Stride, int Offset)> VertexAttribPointers { get; } = [];
    public Dictionary<int, uint> VertexAttribBuffers { get; } = [];
    public Dictionary<int, uint> VertexAttribDivisors { get; } = [];
    public Dictionary<int, double[]> UniformValues { get; } = [];
    public HashSet<uint> EnabledCapabilities { get; } = [];
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
    public Dictionary<uint, (int Width, int Height, byte[] Pixels)> Textures { get; } = [];
    public Dictionary<uint, uint> TextureInternalFormats { get; } = [];
    public Dictionary<uint, Dictionary<uint, int>> TextureParameters { get; } = [];
    public HashSet<uint> TextureMipmaps { get; } = [];
    public HashSet<uint> Framebuffers { get; } = [];
    public Dictionary<uint, uint> FramebufferColorAttachments { get; } = [];
    public Dictionary<uint, (int Width, int Height, byte[] Pixels)> Renderbuffers { get; } = [];
    public Dictionary<uint, uint> RenderbufferFormats { get; } = [];
    public Dictionary<uint, uint> FramebufferRenderbufferAttachments { get; } = [];
    public Dictionary<uint, uint> FramebufferDepthRenderbufferAttachments { get; } = [];
    public List<uint> DrawBuffers { get; } = [];
    public uint ReadBuffer { get; set; } = 0x0405;
    public HashSet<uint> VertexArrays { get; } = [];
    public HashSet<uint> Syncs { get; } = [];
    public HashSet<uint> Queries { get; } = [];
    public Dictionary<uint, bool> QueryResults { get; } = [];
    public uint? ActiveQuery { get; set; }
    public uint? BoundTexture { get; set; }
    public uint ActiveTextureUnit { get; set; } = 0x84C0;
    public uint? BoundFramebuffer { get; set; }
    public uint? BoundRenderbuffer { get; set; }
    public uint? BoundArrayBuffer { get; set; }
    public uint? BoundElementArrayBuffer { get; set; }
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
    public float ClearA { get; private set; } = 1;
    public ScriptValue OnContextLost { get; set; } = ScriptValue.Null;
    public ScriptValue OnContextRestored { get; set; } = ScriptValue.Null;
    public WebGlContextState(IScriptEngine engine, CanvasElementState canvas, int maxGpuBytes) { _engine = engine; _canvas = canvas; _maxGpuBytes = Math.Max(0, maxGpuBytes); }
    public uint CreateHandle() => _nextHandle++;
    public void AddDrawCall() => DrawCalls++;
    public void SetError(int error) { if (Error == 0) Error = error; }
    public int TakeError() { var error = Error; Error = 0; return error; }
    public void Lose() { if (Lost) return; Lost = true; SetError(0x9242); if (OnContextLost.IsFunction) _engine.Call(OnContextLost, ScriptValue.Undefined, [_engine.Evaluate("({ type: 'webglcontextlost' })")]); }
    public void Restore() { if (!Lost) return; Lost = false; if (OnContextRestored.IsFunction) _engine.Call(OnContextRestored, ScriptValue.Undefined, [_engine.Evaluate("({ type: 'webglcontextrestored' })")]); }
    public bool TryAllocate(int bytes)
    {
        if (bytes < 0 || bytes > _maxGpuBytes - AllocatedBytes) { SetError(0x0505); return false; }
        AllocatedBytes += bytes;
        return true;
    }
    public bool ReplaceBuffer(uint handle, int bytes)
    {
        var oldBytes = Buffers.TryGetValue(handle, out var existing) ? existing.Length : 0;
        var nextTotal = AllocatedBytes - oldBytes + bytes;
        if (bytes < 0 || bytes > _maxGpuBytes || nextTotal > _maxGpuBytes) { SetError(0x0505); return false; }
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

    public void DrawArrays(uint mode, int first, int count)
    {
        DrawCalls++;
        DrawTriangles(mode, count, vertex => first + vertex);
    }

    public void DrawElements(uint mode, uint type, int count, int offset)
    {
        DrawCalls++;
        DrawElementsCore(mode, type, count, offset);
    }

    public void DrawArraysInstanced(uint mode, int first, int count, int instanceCount)
    {
        DrawCalls++;
        if (instanceCount < 0 || instanceCount > 1024)
            return;
        for (var instance = 0; instance < instanceCount; instance++)
            DrawTriangles(mode, count, vertex => first + vertex);
    }

    public void DrawElementsInstanced(uint mode, uint type, int count, int offset, int instanceCount)
    {
        DrawCalls++;
        if (instanceCount < 0 || instanceCount > 1024)
            return;
        for (var instance = 0; instance < instanceCount; instance++)
            DrawElementsCore(mode, type, count, offset);
    }

    private void DrawElementsCore(uint mode, uint type, int count, int offset)
    {
        var indexStride = type switch { 0x1401u => 1, 0x1403u => 2, 0x1405u => 4, _ => 0 };
        if (indexStride == 0 || count < 3 || count > 30_000 || offset < 0 || offset % indexStride != 0 || BoundElementArrayBuffer is not { } elementBuffer ||
            !BufferValues.TryGetValue(elementBuffer, out var indices) || offset / indexStride + count > indices.Length)
            return;
        DrawTriangles(mode, count, vertex => (int)indices[offset / indexStride + vertex]);
    }

    private void DrawTriangles(uint mode, int count, Func<int, int> vertexIndex)
    {
        if (mode != 0x0004 || count < 3 || count > 30_000 || !EnabledVertexAttributes.Contains(0) ||
            !VertexAttribPointers.TryGetValue(0, out var pointer) || pointer.Type != 0x1406 || pointer.Size < 2 ||
            !VertexAttribBuffers.TryGetValue(0, out var bufferHandle) || !Buffers.TryGetValue(bufferHandle, out var buffer))
            return;
        var surface = Canvas.Surface();
        if (surface is null || surface.Width <= 0 || surface.Height <= 0)
            return;
        var stride = pointer.Stride > 0 ? pointer.Stride : pointer.Size * 4;
        if (stride < pointer.Size * 4)
            return;
        var colorValues = UniformValues.TryGetValue(0, out var uniform) && uniform.Length >= 4 ? uniform : [1, 1, 1, 1];
        var color = new Rgba((float)Math.Clamp(colorValues[0], 0, 1), (float)Math.Clamp(colorValues[1], 0, 1),
            (float)Math.Clamp(colorValues[2], 0, 1), (float)Math.Clamp(colorValues[3], 0, 1));
        var blend = EnabledCapabilities.Contains(0x0BE2) && BlendSourceFactor == 0x0302 && BlendDestinationFactor == 0x0303
            ? BlendMode.Normal : BlendMode.Copy;
        var viewportX = ViewportX;
        var viewportY = ViewportY;
        var viewportWidth = ViewportWidth > 0 ? ViewportWidth : surface.Width;
        var viewportHeight = ViewportHeight > 0 ? ViewportHeight : surface.Height;
        var clipped = EnabledCapabilities.Contains(0x0C11);
        if (clipped)
        {
            surface.Canvas.Save();
            surface.Canvas.ClipRoundedRect(new RoundedRect(new RectF(ScissorX, ScissorY, ScissorWidth, ScissorHeight), default));
        }
        float Read(int vertex, int component)
        {
            var offset = (long)vertexIndex(vertex) * stride + pointer.Offset + component * 4;
            return offset >= 0 && offset + 4 <= buffer.Length ? BitConverter.ToSingle(buffer, (int)offset) : float.NaN;
        }
        (float X, float Y) Point(int vertex)
        {
            var x = Read(vertex, 0);
            var y = Read(vertex, 1);
            return (viewportX + (x + 1) * 0.5f * viewportWidth, viewportY + (1 - y) * 0.5f * viewportHeight);
        }
        for (var vertex = 0; vertex + 2 < count; vertex += 3)
        {
            var a = Point(vertex); var b = Point(vertex + 1); var c = Point(vertex + 2);
            if (float.IsNaN(a.X) || float.IsNaN(a.Y) || float.IsNaN(b.X) || float.IsNaN(b.Y) || float.IsNaN(c.X) || float.IsNaN(c.Y))
                continue;
            Canvas.Surface()!.Canvas.FillPath(new PathData().MoveTo(a.X, a.Y).LineTo(b.X, b.Y).LineTo(c.X, c.Y).Close(),
                FillRule.NonZero, new Paint(color, Blend: blend));
        }
        if (clipped)
            surface.Canvas.Restore();
        Canvas.Touch();
    }
    public void DeleteTexture(uint handle)
    {
        if (Textures.Remove(handle, out var texture)) AllocatedBytes = Math.Max(0, AllocatedBytes - texture.Pixels.Length);
        TextureParameters.Remove(handle);
        TextureInternalFormats.Remove(handle);
        TextureMipmaps.Remove(handle);
        if (BoundTexture == handle) BoundTexture = null;
    }
    public void DeleteFramebuffer(uint handle)
    {
        Framebuffers.Remove(handle);
        FramebufferColorAttachments.Remove(handle);
        FramebufferRenderbufferAttachments.Remove(handle);
        FramebufferDepthRenderbufferAttachments.Remove(handle);
        if (BoundFramebuffer == handle) BoundFramebuffer = null;
    }
    public void DeleteRenderbuffer(uint handle)
    {
        if (Renderbuffers.Remove(handle, out var renderbuffer))
            AllocatedBytes = Math.Max(0, AllocatedBytes - renderbuffer.Pixels.Length);
        RenderbufferFormats.Remove(handle);
        foreach (var attachment in FramebufferRenderbufferAttachments.Where(pair => pair.Value == handle).Select(pair => pair.Key).ToArray())
            FramebufferRenderbufferAttachments.Remove(attachment);
        foreach (var attachment in FramebufferDepthRenderbufferAttachments.Where(pair => pair.Value == handle).Select(pair => pair.Key).ToArray())
            FramebufferDepthRenderbufferAttachments.Remove(attachment);
        if (BoundRenderbuffer == handle) BoundRenderbuffer = null;
    }
    public bool ReplaceRenderbuffer(uint handle, int width, int height, int bytes)
    {
        var oldBytes = Renderbuffers.TryGetValue(handle, out var existing) ? existing.Pixels.Length : 0;
        var nextTotal = AllocatedBytes - oldBytes + bytes;
        if (bytes < 0 || bytes > _maxGpuBytes || nextTotal > _maxGpuBytes) { SetError(0x0505); return false; }
        AllocatedBytes = nextTotal;
        Renderbuffers[handle] = (width, height, new byte[bytes]);
        return true;
    }
    public bool ReplaceTexture(uint handle, int width, int height, int bytes)
    {
        var oldBytes = Textures.TryGetValue(handle, out var existing) ? existing.Pixels.Length : 0;
        var nextTotal = AllocatedBytes - oldBytes + bytes;
        if (bytes < 0 || bytes > _maxGpuBytes || nextTotal > _maxGpuBytes) { SetError(0x0505); return false; }
        AllocatedBytes = nextTotal;
        Textures[handle] = (width, height, new byte[bytes]);
        return true;
    }
    public void CopyTexturePixels(uint handle, int x, int y, int width, int height, IReadOnlyList<ScriptValue> source)
    {
        if (!Textures.TryGetValue(handle, out var texture)) return;
        var copyWidth = Math.Min(width, Math.Max(0, texture.Width - x));
        var copyHeight = Math.Min(height, Math.Max(0, texture.Height - y));
        for (var row = 0; row < copyHeight; row++)
            for (var column = 0; column < copyWidth; column++)
            {
                var sourceIndex = (row * width + column) * 4;
                var targetIndex = ((y + row) * texture.Width + x + column) * 4;
                for (var channel = 0; channel < 4 && sourceIndex + channel < source.Count && targetIndex + channel < texture.Pixels.Length; channel++)
                    texture.Pixels[targetIndex + channel] = (byte)Math.Clamp(WebIdlConversions.ToInt32(source[sourceIndex + channel]), 0, 255);
            }
        Textures[handle] = texture;
    }
    public void DeleteShader(uint handle) => Shaders.Remove(handle);
    public void DeleteProgram(uint handle)
    {
        Programs.Remove(handle);
        ProgramShaders.Remove(handle);
        LinkedPrograms.Remove(handle);
        if (CurrentProgram == handle) CurrentProgram = null;
    }
    public void SetClearColor(float r, float g, float b, float a) => (ClearR, ClearG, ClearB, ClearA) = (Math.Clamp(r, 0, 1), Math.Clamp(g, 0, 1), Math.Clamp(b, 0, 1), Math.Clamp(a, 0, 1));
    public void Clear(uint mask)
    {
        var clearColor = (mask & 0x4000) != 0;
        var clearDepth = (mask & 0x0100) != 0;
        if (!clearColor && !clearDepth)
            return;
        var clear = new[] { (byte)Math.Round(ClearR * 255), (byte)Math.Round(ClearG * 255), (byte)Math.Round(ClearB * 255), (byte)Math.Round(ClearA * 255) };
        var clipped = EnabledCapabilities.Contains(0x0C11);
        if (BoundFramebuffer is { } framebuffer)
        {
            if (clearColor && FramebufferColorAttachments.TryGetValue(framebuffer, out var texture) && Textures.TryGetValue(texture, out var texturePixels))
            {
                Fill(texturePixels.Pixels, clear, texturePixels.Width, texturePixels.Height, clipped);
                Textures[texture] = texturePixels;
            }
            else if (clearColor && FramebufferRenderbufferAttachments.TryGetValue(framebuffer, out var renderbuffer) && Renderbuffers.TryGetValue(renderbuffer, out var renderbufferPixels))
            {
                Fill(renderbufferPixels.Pixels, clear, renderbufferPixels.Width, renderbufferPixels.Height, clipped);
                Renderbuffers[renderbuffer] = renderbufferPixels;
            }
            if (clearDepth && DepthMask && FramebufferDepthRenderbufferAttachments.TryGetValue(framebuffer, out var depthBuffer) && Renderbuffers.TryGetValue(depthBuffer, out var depthPixels))
            {
                FillDepth(depthPixels.Pixels, ClearDepth, depthPixels.Width, depthPixels.Height, clipped);
                Renderbuffers[depthBuffer] = depthPixels;
            }
            return;
        }
        if (!clearColor || _canvas.Surface() is not { } surface)
            return;
        var color = new Rgba(ClearR, ClearG, ClearB, ClearA);
        if (!ColorMaskRed || !ColorMaskGreen || !ColorMaskBlue || !ColorMaskAlpha)
        {
            var pixels = surface.ReadPixels(0, 0, surface.Width, surface.Height);
            Fill(pixels, clear, surface.Width, surface.Height, clipped);
            surface.WritePixels(0, 0, surface.Width, surface.Height, pixels);
            _canvas.Touch();
            return;
        }
        if (clipped)
            surface.Canvas.Save();
        if (clipped)
            surface.Canvas.ClipRoundedRect(new RoundedRect(new RectF(ScissorX, ScissorY, ScissorWidth, ScissorHeight), default));
        surface.Canvas.FillPath(new PathData().AddRoundedRect(new RoundedRect(new RectF(0, 0, surface.Width, surface.Height), default)), FillRule.NonZero,
            new Paint(color, Blend: BlendMode.Copy));
        if (clipped)
            surface.Canvas.Restore();
        _canvas.Touch();

        void Fill(byte[] pixels, byte[] color, int width, int height, bool clipped)
        {
            if (!clipped)
            {
                for (var i = 0; i + 3 < pixels.Length; i += 4)
                    WriteColor(pixels, i, color);
                return;
            }
            var left = Math.Clamp(ScissorX, 0, width);
            var top = Math.Clamp(ScissorY, 0, height);
            var right = Math.Clamp((long)ScissorX + ScissorWidth, 0, width);
            var bottom = Math.Clamp((long)ScissorY + ScissorHeight, 0, height);
            for (var y = top; y < bottom; y++)
                for (var x = left; x < right; x++)
                    WriteColor(pixels, (y * width + x) * 4, color);
        }

        void WriteColor(byte[] pixels, int offset, byte[] color)
        {
            if (ColorMaskRed) pixels[offset] = color[0];
            if (ColorMaskGreen) pixels[offset + 1] = color[1];
            if (ColorMaskBlue) pixels[offset + 2] = color[2];
            if (ColorMaskAlpha) pixels[offset + 3] = color[3];
        }

        void FillDepth(byte[] pixels, float depth, int width, int height, bool clipped)
        {
            var bits = BitConverter.SingleToInt32Bits(depth);
            if (!clipped)
            {
                for (var i = 0; i + 3 < pixels.Length; i += 4)
                    BinaryPrimitives.WriteInt32LittleEndian(pixels.AsSpan(i, 4), bits);
                return;
            }
            var left = Math.Clamp(ScissorX, 0, width);
            var top = Math.Clamp(ScissorY, 0, height);
            var right = Math.Clamp((long)ScissorX + ScissorWidth, 0, width);
            var bottom = Math.Clamp((long)ScissorY + ScissorHeight, 0, height);
            for (var y = top; y < bottom; y++)
                for (var x = left; x < right; x++)
                    BinaryPrimitives.WriteInt32LittleEndian(pixels.AsSpan((y * width + x) * 4, 4), bits);
        }
    }
}
