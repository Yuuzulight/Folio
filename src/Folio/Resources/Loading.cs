using System.Text;

namespace Folio.Resources;

/// <summary>What a load is for (docs/study/16-resources-and-security.md).</summary>
public enum ResourceKind
{
    Stylesheet,
    Image,
    Font,
    Script,
    SvgImage,
}

/// <summary>A load a document asks for: an absolute URL and what it is for.</summary>
public sealed record ResourceRequest(string Url, ResourceKind Kind);

/// <summary>A loaded resource (its bytes and MIME type, if known), or why it was not loaded.</summary>
public sealed record ResourceResponse(byte[]? Data, string? ContentType, string? Error)
{
    public bool Succeeded => Data is not null;

    public static ResourceResponse Loaded(byte[] data, string? contentType = null) => new(data, contentType, null);

    public static ResourceResponse Refused(string reason) => new(null, null, reason);
}

/// <summary>
/// A document's loads (docs/study/16-resources-and-security.md, option B, deny by default): <c>data:</c> URLs always,
/// <c>file:</c> URLs under folders allowed here, and everything else only through the host's <see cref="IResourceLoader"/>.
/// With default options nothing but <c>data:</c> URLs loads, so no request ever leaves the process.
/// </summary>
/// <param name="host">The host's loader; null loads nothing beyond data: URLs and the allowed folders.</param>
// ponytail: loads are synchronous, made while styling before the first layout, each within HostTimeout; a load that
// finishes later is not waited for.
internal sealed class ResourceLoader(IReadOnlyList<string>? allowedFolders = null, int maxBytes = ResourceLoader.MaxResourceBytes,
                                     IResourceLoader? host = null)
{
    /// <summary>The largest resource loaded (the font file limit of study 16, the largest resource type).</summary>
    public const int MaxResourceBytes = 20 * 1024 * 1024;

    /// <summary>How long a load through the host's loader may take.</summary>
    public static readonly TimeSpan HostTimeout = TimeSpan.FromSeconds(10);

    private readonly LocalFolder[] _folders = (allowedFolders ?? []).Select(f => new LocalFolder(f)).ToArray();

    /// <summary>Loads nothing but <c>data:</c> URLs: the default.</summary>
    public static ResourceLoader DataUrlsOnly { get; } = new();

    public int MaxBytes { get; } = maxBytes;

    public ResourceResponse Load(ResourceRequest request)
    {
        var url = request.Url;
        if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            if (DataUrl.TryParse(url, out var data, out var mimeType) is false)
                return ResourceResponse.Refused("Malformed data: URL.");
            return data.Length > MaxBytes
                ? ResourceResponse.Refused($"The data: URL is larger than {MaxBytes} bytes.")
                : new ResourceResponse(data, mimeType, null);
        }

        if (url.StartsWith("file:", StringComparison.OrdinalIgnoreCase) && _folders.Length > 0
            && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.IsFile)
        {
            string? lastError = null;
            foreach (var folder in _folders)
            {
                var result = folder.Read(uri.LocalPath, MaxBytes);
                if (result.Succeeded)
                    return result;
                lastError = result.Error;
            }
            return ResourceResponse.Refused(lastError ?? "Not found.");
        }

        if (host is not null && FromHost(host, request) is { } hosted)
            return hosted.Data is { Length: var length } && length > MaxBytes ? ResourceResponse.Refused($"The resource is larger than {MaxBytes} bytes.") : hosted;
        return ResourceResponse.Refused($"Loading {Scheme(url)} URLs is not allowed.");
    }

    // The host's answer within the timeout; its loader runs off this thread, so one that awaits on a UI thread's
    // context cannot deadlock it. A loader that throws counts as a refusal.
    private static ResourceResponse? FromHost(IResourceLoader host, ResourceRequest request)
    {
        using var cancel = new CancellationTokenSource(HostTimeout);
        try
        {
            var load = Task.Run(() => host.LoadAsync(request, cancel.Token), cancel.Token);
            return load.Wait(HostTimeout) ? load.Result : ResourceResponse.Refused("The load timed out.");
        }
        catch (AggregateException e)
        {
            return ResourceResponse.Refused(e.InnerException is OperationCanceledException ? "The load timed out." : "The loader failed.");
        }
    }

    // A URL scheme: a letter, then letters, digits, "+", "-" or ".", then ":" (so "/a" and "C:" paths are not taken as absolute URLs).
    private static bool HasScheme(string reference)
    {
        var colon = reference.IndexOf(':');
        if (colon < 2 || !char.IsAsciiLetter(reference[0]))
            return false;
        for (var i = 1; i < colon; i++)
        {
            if (!(char.IsAsciiLetterOrDigit(reference[i]) || reference[i] is '+' or '-' or '.'))
                return false;
        }
        return true;
    }

    private static string Scheme(string url)
    {
        var colon = url.IndexOf(':');
        return colon > 0 ? url[..(colon + 1)] : "relative";
    }

    /// <summary>
    /// Resolves a reference against a base URL; null when it cannot be resolved (a relative reference without a base).
    /// </summary>
    // ponytail: System.Uri resolution, which differs from the URL Standard only in edge cases artifacts do not hit.
    public static string? Resolve(string? baseUrl, string reference)
    {
        reference = reference.Trim();
        if (reference.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return reference;
        if (HasScheme(reference) && Uri.TryCreate(reference, UriKind.Absolute, out var absolute))
            return absolute.AbsoluteUri;
        if (baseUrl is null || !Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri))
            return null;
        return Uri.TryCreate(baseUri, reference, out var resolved) ? resolved.AbsoluteUri : null;
    }
}

/// <summary>
/// Serves files under one folder (the study's LocalFolderLoader), refusing anything that could leave it: parent
/// references, UNC and device paths, alternate data streams, reserved device names, and links whose target is
/// outside the folder.
/// </summary>
internal sealed class LocalFolder(string root)
{
    private readonly string _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));

    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    public ResourceResponse Read(string path, int maxBytes)
    {
        if (path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal))
            return ResourceResponse.Refused("Network and device paths are not allowed.");

        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return ResourceResponse.Refused("Invalid path.");
        }
        if (!IsUnderRoot(full))
            return ResourceResponse.Refused("The path is outside the allowed folder.");

        var current = _root;
        foreach (var segment in full[_root.Length..].Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment.Contains(':') || ReservedNames.Contains(Path.GetFileNameWithoutExtension(segment)))
                return ResourceResponse.Refused("Alternate data streams and reserved names are not allowed.");
            current = Path.Combine(current, segment);
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (info.Exists && info.LinkTarget is not null
                && info.ResolveLinkTarget(returnFinalTarget: true) is { } target && !IsUnderRoot(Path.GetFullPath(target.FullName)))
                return ResourceResponse.Refused("A link points outside the allowed folder.");
        }

        var file = new FileInfo(full);
        if (!file.Exists)
            return ResourceResponse.Refused("Not found.");
        if (file.Length > maxBytes)
            return ResourceResponse.Refused($"The file is larger than {maxBytes} bytes.");
        try
        {
            return new ResourceResponse(File.ReadAllBytes(full), null, null);
        }
        catch (IOException e)
        {
            return ResourceResponse.Refused(e.Message);
        }
        catch (UnauthorizedAccessException)
        {
            return ResourceResponse.Refused("Access denied.");
        }
    }

    private bool IsUnderRoot(string full) =>
        full.StartsWith(_root, PathComparison) && full.Length > _root.Length && full[_root.Length] is '\\' or '/';
}

/// <summary>https://fetch.spec.whatwg.org/#data-url-processor</summary>
internal static class DataUrl
{
    public static bool TryParse(string url, out byte[] data, out string mimeType)
    {
        data = [];
        mimeType = "";
        var hash = url.IndexOf('#');
        var input = (hash >= 0 ? url[..hash] : url)[5..];
        var comma = input.IndexOf(',');
        if (comma < 0)
            return false;

        var type = input[..comma].Trim(' ', '\t', '\n', '\f', '\r');
        var body = PercentDecode(input[(comma + 1)..]);

        // ";base64" at the end of the type, with optional spaces before "base64".
        var semicolon = type.LastIndexOf(';');
        if (semicolon >= 0 && type[(semicolon + 1)..].TrimStart(' ').Equals("base64", StringComparison.OrdinalIgnoreCase))
        {
            type = type[..semicolon];
            if (ForgivingBase64(body) is not { } decoded)
                return false;
            body = decoded;
        }

        if (type.StartsWith(';'))
            type = "text/plain" + type;
        data = body;
        mimeType = type.Contains('/') ? type.ToLowerInvariant() : "text/plain;charset=us-ascii";
        return true;
    }

    // https://url.spec.whatwg.org/#percent-decode
    private static byte[] PercentDecode(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var output = new List<byte>(bytes.Length);
        for (var i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] == '%' && i + 2 < bytes.Length && IsHex(bytes[i + 1]) && IsHex(bytes[i + 2]))
            {
                output.Add((byte)(Hex(bytes[i + 1]) * 16 + Hex(bytes[i + 2])));
                i += 2;
            }
            else
            {
                output.Add(bytes[i]);
            }
        }
        return [.. output];
    }

    private static bool IsHex(byte b) => b is >= (byte)'0' and <= (byte)'9' or >= (byte)'a' and <= (byte)'f' or >= (byte)'A' and <= (byte)'F';

    private static int Hex(byte b) => b <= '9' ? b - '0' : (b | 0x20) - 'a' + 10;

    // https://infra.spec.whatwg.org/#forgiving-base64-decode
    private static byte[]? ForgivingBase64(byte[] input)
    {
        var text = Encoding.Latin1.GetString(input).Where(c => c is not ('\t' or '\n' or '\f' or '\r' or ' ')).ToArray();
        var length = text.Length;
        if (length % 4 == 0)
        {
            if (length > 0 && text[length - 1] == '=')
                length--;
            if (length > 0 && text[length - 1] == '=')
                length--;
        }
        if (length % 4 == 1)
            return null;
        for (var i = 0; i < length; i++)
        {
            if (!(char.IsAsciiLetterOrDigit(text[i]) || text[i] is '+' or '/'))
                return null;
        }
        var padded = new string(text, 0, length).PadRight((length + 3) / 4 * 4, '=');
        return Convert.FromBase64String(padded);
    }
}
