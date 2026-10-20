namespace Folio.Dom;

internal enum FallbackFeature
{
    WebGl,
    Audio,
    Video,
    Iframes,
    Sound,
    Camera,
    Microphone
}

internal enum FallbackReason
{
    HostDenied,
    GraphicsUnavailable,
    ContextLost,
    UnsupportedFormat,
    DecodeFailed,
    SourceRefused,
    SourceUnavailable,
    ResourceLimit,
    Closed
}

/// <summary>A known feature failure, distinct from legitimately empty content.</summary>
internal readonly record struct FeatureFallback(FallbackFeature Feature, FallbackReason Reason)
{
    internal string Label
    {
        get
        {
            var feature = Feature switch
            {
                FallbackFeature.WebGl => "WebGL",
                FallbackFeature.Audio => "Audio",
                FallbackFeature.Video => "Video",
                FallbackFeature.Iframes => "Frame",
                FallbackFeature.Sound => "Sound",
                FallbackFeature.Camera => "Camera",
                FallbackFeature.Microphone => "Microphone",
                _ => "Content"
            };
            var reason = Reason switch
            {
                FallbackReason.HostDenied => "not allowed",
                FallbackReason.GraphicsUnavailable => "graphics support missing",
                FallbackReason.ContextLost => "graphics connection lost",
                FallbackReason.UnsupportedFormat => "unsupported format",
                FallbackReason.DecodeFailed => "could not read this format",
                FallbackReason.SourceRefused => "source blocked",
                FallbackReason.SourceUnavailable => "source unavailable",
                FallbackReason.ResourceLimit => "resource limit reached",
                FallbackReason.Closed => "closed",
                _ => "unavailable"
            };
            return $"{feature} unavailable: {reason}";
        }
    }
}

internal interface IFeatureFallbackSource
{
    FeatureFallback? Fallback { get; }
}

internal static class FeatureFallbacks
{
    internal static FeatureFallback? For(ElementNode element)
    {
        if (element.Name.Namespace != Namespaces.Html) return null;
        if (element.CanvasSurface is IFeatureFallbackSource canvas) return canvas.Fallback;
        if (element.MediaPresentation is IFeatureFallbackSource media) return media.Fallback;
        if (element.FramePresentation is IFeatureFallbackSource frame) return frame.Fallback;

        var document = element.OwnerDocument;
        return element.LocalName switch
        {
            "video" when !document.MediaAllowed => new(FallbackFeature.Video, FallbackReason.HostDenied),
            "audio" when !document.MediaAllowed => new(FallbackFeature.Audio, FallbackReason.HostDenied),
            "audio" when !document.SoundAllowed && HasMediaSource(element) => new(FallbackFeature.Sound, FallbackReason.HostDenied),
            "video" when element.MediaPresentation is null && HasMediaSource(element) => new(FallbackFeature.Video, FallbackReason.SourceUnavailable),
            "audio" when element.MediaPresentation is null && HasMediaSource(element) => new(FallbackFeature.Audio, FallbackReason.SourceUnavailable),
            "iframe" or "embed" or "object" when !document.IframesAllowed => new(FallbackFeature.Iframes, FallbackReason.HostDenied),
            "iframe" when element.FramePresentation is null && string.IsNullOrEmpty(element.GetAttribute("srcdoc")) &&
                (string.IsNullOrWhiteSpace(element.GetAttribute("src")) || element.GetAttribute("src")!.Equals("about:blank", StringComparison.OrdinalIgnoreCase)) => null,
            "iframe" or "embed" or "object" when element.FramePresentation is null => new(FallbackFeature.Iframes, FallbackReason.SourceUnavailable),
            _ => null
        };
    }

    private static bool HasMediaSource(ElementNode element) =>
        !string.IsNullOrWhiteSpace(element.GetAttribute("src")) ||
        element.Children.OfType<ElementNode>().Any(child => child.Name.Namespace == Namespaces.Html &&
            child.LocalName == "source" && !string.IsNullOrWhiteSpace(child.GetAttribute("src")));
}
