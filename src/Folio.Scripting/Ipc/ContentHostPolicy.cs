using System.Text.Json;

namespace Folio.Scripting.Ipc;

/// <summary>Bounds shared by the trusted load-policy writer and untrusted notification reader.</summary>
internal static class ContentHostPolicy
{
    internal const int MaxOrigins = 64;
    internal const int MaxOriginLength = 2048;
    internal const int MaxOriginsLength = 32768;
    internal const int MaxNavigationLength = 8192;

    internal static HashSet<string> NormalizeOrigins(IEnumerable<string> origins)
    {
        ArgumentNullException.ThrowIfNull(origins);
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var count = 0;
        var length = 0;
        foreach (var entry in origins)
        {
            if (++count > MaxOrigins || entry is null || entry.Length > MaxOriginLength
                || (length += entry.Length) > MaxOriginsLength)
                throw new ArgumentException("The WebRTC origin policy exceeds its bounds.", nameof(origins));
            var value = entry.TrimEnd('/');
            if (value.Length == 0 || value.Any(char.IsWhiteSpace) || value.Any(char.IsControl)
                || value.Contains('@') || !Uri.TryCreate(value, UriKind.Absolute, out var uri)
                || uri.Scheme is not ("stun" or "stuns" or "turn" or "turns") || uri.UserInfo.Length != 0)
                throw new ArgumentException("The WebRTC origin policy contains an invalid ICE origin.", nameof(origins));
            // Preserve opaque STUN/TURN endpoints and the existing case-insensitive,
            // trailing-slash-insensitive exact endpoint/authority matching semantics.
            result.Add(value);
        }
        return result;
    }

    internal static HashSet<string> NormalizeDiagramUrls(IEnumerable<string> urls)
    {
        ArgumentNullException.ThrowIfNull(urls);
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var count = 0;
        var length = 0;
        foreach (var url in urls)
        {
            if (++count > MaxOrigins || url is null || url.Length > MaxOriginLength ||
                (length += url.Length) > MaxOriginsLength || !TryNavigationUri(url, out _))
                throw new ArgumentException("The diagram library policy contains an invalid URL or exceeds its bounds.", nameof(urls));
            result.Add(url);
        }
        return result;
    }

    internal static HashSet<string> ReadOrigins(JsonElement policy) => ReadUrls(policy, "webRtcAllowedOrigins", NormalizeOrigins);

    internal static HashSet<string> ReadDiagramUrls(JsonElement policy) => ReadUrls(policy, "diagramLibraryUrls", NormalizeDiagramUrls);

    private static HashSet<string> ReadUrls(JsonElement policy, string propertyName, Func<IEnumerable<string>, HashSet<string>> normalize)
    {
        if (policy.ValueKind != JsonValueKind.Object || !policy.TryGetProperty(propertyName, out var origins)
            || origins.ValueKind != JsonValueKind.Array || origins.GetArrayLength() > MaxOrigins)
            return new(StringComparer.OrdinalIgnoreCase);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in policy.EnumerateObject())
            if (names.Count >= 8 || property.Name.Length > 64 || !names.Add(property.Name))
                return new(StringComparer.OrdinalIgnoreCase);
        try
        {
            return normalize(origins.EnumerateArray().Select(value =>
                value.ValueKind == JsonValueKind.String ? value.GetString()! : throw new ArgumentException()));
        }
        catch (ArgumentException) { return new(StringComparer.OrdinalIgnoreCase); }
    }

    internal static bool TryNavigationUri(string? value, out Uri? uri)
    {
        uri = null;
        return value is { Length: > 0 and <= MaxNavigationLength }
            && !value.Any(char.IsControl) && value == value.Trim()
            && Uri.TryCreate(value, UriKind.Absolute, out uri) && uri.IsWellFormedOriginalString() && uri.UserInfo.Length == 0
            && uri.Scheme is not ("javascript" or "vbscript" or "data")
            && !Uri.UnescapeDataString(value).Any(char.IsControl);
    }
}
