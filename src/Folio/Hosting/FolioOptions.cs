namespace Folio;

/// <summary>Requested camera and microphone tracks for a host permission decision.</summary>
public readonly record struct MediaCaptureRequest(bool Audio, bool Video);

/// <summary>The colour scheme the host reports to <c>prefers-color-scheme</c>.</summary>
public enum ColorScheme
{
    Light,
    Dark,
}

/// <summary>
/// Limits on the work one document may cause (docs/study/16-resources-and-security.md). Exceeding one stops that part
/// of the work, keeps what is done and adds a <see cref="DiagnosticCode.LimitExceeded"/> diagnostic; it never throws.
/// </summary>
public sealed record ResourceLimits
{
    public static ResourceLimits Default { get; } = new();

    /// <summary>HTML input size: bytes for a stream, characters for a string.</summary>
    public int MaxInputSize { get; init; } = 16 * 1024 * 1024;

    /// <summary>DOM nodes created by the parser.</summary>
    public int MaxNodes { get; init; } = 200_000;

    /// <summary>ElementNode nesting depth; deeper content attaches to the deepest allowed element.</summary>
    public int MaxNestingDepth { get; init; } = 512;

    /// <summary>Rules per stylesheet.</summary>
    public int MaxStyleRules { get; init; } = 100_000;

    /// <summary>Aggregate bytes available to a document's WebGL resources.</summary>
    public int MaxGpuBytes { get; init; } = 64 * 1024 * 1024;
    public int MaxMediaBytes { get; init; } = 128 * 1024 * 1024;
    /// <summary>Maximum number of media decoders held by one document.</summary>
    public int MaxMediaDecoders { get; init; } = 8;

    /// <summary>Maximum serialized payload accepted by a worker postMessage call.</summary>
    public int MaxWorkerMessageBytes { get; init; } = 1 * 1024 * 1024;
    public int MaxWorkers { get; init; } = 8;
    public long MaxWorkerMemoryBytes { get; init; } = 64L * 1024 * 1024;
    /// <summary>Maximum bytes retained by the artifact's Cache API stores.</summary>
    public long MaxServiceWorkerCacheBytes { get; init; } = 64L * 1024 * 1024;
    /// <summary>Maximum nested iframe contexts a document may create.</summary>
    public int MaxIframes { get; init; } = 16;
    /// <summary>Maximum simultaneously open WebRTC peer connections.</summary>
    public int MaxWebRtcConnections { get; init; } = 8;

    /// <summary>Maximum WebAssembly linear-memory bytes available to a document.</summary>
    public int MaxWebAssemblyBytes { get; init; } = 64 * 1024 * 1024;
}

/// <summary>Capabilities a host explicitly grants to a document.</summary>
public sealed record FeatureCapabilities
{
    public static FeatureCapabilities Default { get; } = new();

    public bool WebGl { get; init; }
    public bool Media { get; init; }
    /// <summary>Allows audible media to begin from an autoplay attribute without a user gesture.</summary>
    public bool AllowSoundAutoplay { get; init; }
    public bool Workers { get; init; }
    public bool WebAssembly { get; init; }
    public bool Iframes { get; init; }
    public bool WebRtc { get; init; }
    public bool ServiceWorkers { get; init; }
}

/// <summary>How a host configures a document. Members for resource loading and scripting arrive with those features.</summary>
public sealed class FolioOptions
{
    /// <summary>Stable host identity used to scope persisted artifact data.</summary>
    public string ArtifactId { get; init; } = "default";

    /// <summary>Optional host-owned persistence for artifact data such as service-worker caches.</summary>
    public IArtifactStorage? ArtifactStorage { get; init; }

    /// <summary>Optional host permission callback for camera and microphone capture. Null preserves the capability-only path.</summary>
    public Func<MediaCaptureRequest, bool>? MediaCapturePermission { get; init; }

    /// <summary>Origins permitted for WebRTC ICE servers. An empty set permits loopback only.</summary>
    public IReadOnlySet<string> WebRtcAllowedOrigins { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Stage-6 capabilities granted by the host. All are disabled by default.</summary>
    public FeatureCapabilities Capabilities { get; init; } = FeatureCapabilities.Default;

    /// <summary>Resolves relative URLs; null for documents without a location.</summary>
    public Uri? BaseUri { get; init; }

    public ColorScheme ColorScheme { get; init; } = ColorScheme.Light;

    public bool ReducedMotion { get; init; }

    /// <summary>A user stylesheet, applied between the user-agent and author styles.</summary>
    public string? UserStyleSheet { get; init; }

    /// <summary>Where text finds its fonts, and what generic family names stand for. By default there are no fonts.</summary>
    public Typography.FontSettings Fonts { get; init; } = Typography.FontSettings.Default;

    public ResourceLimits Limits { get; init; } = ResourceLimits.Default;

    /// <summary>
    /// Loads stylesheets, images and fonts beyond <c>data:</c> URLs, which load without it. Null (the default) loads
    /// nothing else, so no request ever leaves the process. <see cref="Resources.ResourceLoaders"/> has local folders and
    /// allowlisted HTTPS origins.
    /// </summary>
    public Resources.IResourceLoader? ResourceLoader { get; init; }

    /// <summary>
    /// Pinned diagram-library URLs that Folio may replace with its own diagram stand-in. The URL must match the
    /// resolved <c>script src</c> exactly; leaving this empty keeps ordinary script loading behaviour.
    /// </summary>
    public IReadOnlySet<string> DiagramLibraryUrls { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether parse errors are recorded in <see cref="Document.Diagnostics"/>; limit hits always are.</summary>
    public bool CollectDiagnostics { get; init; } = true;

    /// <summary>
    /// Whether scripts run in the document (docs/architecture.md, study 17). Disabled by default.
    /// In M4, <see cref="ScriptingPolicy.ContentProcess"/> enables scripting.
    /// </summary>
    public ScriptingPolicy Scripting { get; init; } = ScriptingPolicy.Disabled;

    /// <summary>
    /// Makes the bitmaps canvas elements draw on. Null (the default) leaves <c>getContext('2d')</c> returning null.
    /// <c>Folio.Skia</c> has one.
    /// </summary>
    public Painting.IRasterBackend? Raster { get; init; }

    /// <summary>Optional content-host executable used by a host that enables <see cref="ScriptingPolicy.ContentProcess"/>.</summary>
    public string? ContentProcessPath { get; init; }
}

/// <summary>The scripting policy for documents (docs/architecture.md, study 17).</summary>
public enum ScriptingPolicy
{
    /// <summary>Scripting is disabled (the default). Scripts do not execute and noscript parses as markup.</summary>
    Disabled,

    /// <summary>Scripting is enabled. The parser scripting flag is set and scripts execute.</summary>
    ContentProcess,
}
