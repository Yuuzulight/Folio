using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Folio.Resources;

namespace Folio.Scripting.Dom;

internal sealed record StoredCookie(
    string Name,
    string Value,
    string Domain,
    string Path,
    long? ExpiresUnixSeconds,
    bool HttpOnly,
    bool Secure,
    string? SameSite);

internal static class CookieJar
{
    public const string KeyPrefix = "folio/cookies/";

    private static string PrefixOf(string? artifactId) =>
        string.IsNullOrWhiteSpace(artifactId) ? KeyPrefix : $"{artifactId}:{KeyPrefix}";

    private static string FullKey(string? artifactId, string host) =>
        $"{PrefixOf(artifactId)}{host.ToLowerInvariant()}";

    public static string GetDocumentCookieString(IArtifactStorage? storage, string artifactId, Uri? url)
    {
        if (storage is null || url is null || string.IsNullOrEmpty(url.Host))
            return "";
        var matching = GetMatchingCookies(storage, artifactId, url, includeHttpOnly: false);
        return string.Join("; ", matching.Select(c => $"{c.Name}={c.Value}"));
    }

    public static void SetDocumentCookie(IArtifactStorage? storage, string artifactId, Uri? url, string setCookieHeader)
    {
        if (storage is null || url is null || string.IsNullOrEmpty(url.Host))
            return;
        SetFromHeader(storage, artifactId, url, setCookieHeader, fromHttp: false);
    }

    public static string? BuildCookieHeader(IArtifactStorage? storage, string artifactId, Uri? url)
    {
        if (storage is null || url is null || string.IsNullOrEmpty(url.Host))
            return null;
        var matching = GetMatchingCookies(storage, artifactId, url, includeHttpOnly: true);
        if (matching.Count == 0)
            return null;
        return string.Join("; ", matching.Select(c => $"{c.Name}={c.Value}"));
    }

    public static void SetFromHeader(IArtifactStorage? storage, string artifactId, Uri? url, string headerValue, bool fromHttp)
    {
        if (storage is null || url is null || string.IsNullOrEmpty(url.Host) || string.IsNullOrWhiteSpace(headerValue))
            return;

        var parsed = ParseSetCookieHeader(headerValue, url);
        if (parsed is null)
            return;
        if (!fromHttp && parsed.HttpOnly)
            return;

        var storageKey = FullKey(artifactId, parsed.Domain);
        var list = ReadHostCookies(storage, storageKey);
        var existing = list.FirstOrDefault(c =>
            string.Equals(c.Name, parsed.Name, StringComparison.Ordinal) &&
            string.Equals(c.Domain, parsed.Domain, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(c.Path, parsed.Path, StringComparison.Ordinal));
        if (!fromHttp && existing is { HttpOnly: true })
            return;

        list.RemoveAll(c =>
            string.Equals(c.Name, parsed.Name, StringComparison.Ordinal) &&
            string.Equals(c.Domain, parsed.Domain, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(c.Path, parsed.Path, StringComparison.Ordinal));

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (parsed.ExpiresUnixSeconds is not { } exp || exp > now)
            list.Add(parsed);

        WriteHostCookies(storage, storageKey, list);
    }

    public static List<StoredCookie> GetAllCookies(IArtifactStorage? storage, string artifactId)
    {
        if (storage is null) return [];
        var prefix = PrefixOf(artifactId);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var result = new List<StoredCookie>();
        foreach (var key in storage.Keys(prefix))
        {
            var hostCookies = ReadHostCookies(storage, key);
            var active = hostCookies.Where(c => c.ExpiresUnixSeconds is null || c.ExpiresUnixSeconds > now).ToList();
            result.AddRange(active);
        }
        return result;
    }

    public static List<StoredCookie> GetMatchingCookies(IArtifactStorage? storage, string artifactId, Uri url, bool includeHttpOnly)
    {
        if (storage is null || string.IsNullOrEmpty(url.Host))
            return [];
        var prefix = PrefixOf(artifactId);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var requestHost = url.Host.ToLowerInvariant();
        var requestPath = string.IsNullOrEmpty(url.AbsolutePath) ? "/" : url.AbsolutePath;
        var isHttps = url.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) || url.IsLoopback;
        var matches = new List<StoredCookie>();

        foreach (var candidateHost in EnumerateCandidateDomains(requestHost))
        {
            var key = prefix + candidateHost;
            var cookies = ReadHostCookies(storage, key);
            foreach (var c in cookies)
            {
                if (c.ExpiresUnixSeconds is { } exp && exp <= now) continue;
                if (!includeHttpOnly && c.HttpOnly) continue;
                if (c.Secure && !isHttps) continue;
                if (!DomainMatches(requestHost, c.Domain)) continue;
                if (!PathMatches(requestPath, c.Path)) continue;
                matches.Add(c);
            }
        }
        matches.Sort((a, b) => b.Path.Length.CompareTo(a.Path.Length));
        return matches;
    }

    public static void UpsertCookies(IArtifactStorage? storage, string artifactId, IEnumerable<StoredCookie> cookies, Uri? fallbackUrl = null)
    {
        if (storage is null) return;
        var byHost = new Dictionary<string, List<StoredCookie>>(StringComparer.OrdinalIgnoreCase);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        foreach (var raw in cookies)
        {
            if (string.IsNullOrWhiteSpace(raw.Name)) continue;
            var domain = !string.IsNullOrWhiteSpace(raw.Domain)
                ? raw.Domain.Trim().TrimStart('.').ToLowerInvariant()
                : fallbackUrl?.Host.ToLowerInvariant() ?? "";
            if (string.IsNullOrEmpty(domain)) continue;
            var path = string.IsNullOrEmpty(raw.Path) ? "/" : raw.Path;
            var normalized = raw with { Domain = domain, Path = path };
            if (!byHost.TryGetValue(domain, out var bucket))
                byHost[domain] = bucket = ReadHostCookies(storage, FullKey(artifactId, domain));
            bucket.RemoveAll(c =>
                string.Equals(c.Name, normalized.Name, StringComparison.Ordinal) &&
                string.Equals(c.Domain, normalized.Domain, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(c.Path, normalized.Path, StringComparison.Ordinal));
            if (normalized.ExpiresUnixSeconds is null || normalized.ExpiresUnixSeconds > now)
                bucket.Add(normalized);
        }
        foreach (var pair in byHost)
            WriteHostCookies(storage, FullKey(artifactId, pair.Key), pair.Value);
    }

    public static JsonArray ToJsonArray(IEnumerable<StoredCookie> cookies)
    {
        var array = new JsonArray();
        foreach (var c in cookies)
        {
            var obj = new JsonObject
            {
                ["name"] = c.Name,
                ["value"] = c.Value,
                ["domain"] = c.Domain,
                ["path"] = c.Path,
                ["httpOnly"] = c.HttpOnly,
                ["secure"] = c.Secure,
            };
            if (c.ExpiresUnixSeconds is { } exp) obj["expires"] = exp;
            if (!string.IsNullOrEmpty(c.SameSite)) obj["sameSite"] = c.SameSite;
            array.Add((JsonNode?)obj);
        }
        return array;
    }

    public static List<StoredCookie> ParseJsonArray(JsonElement array, Uri? fallbackUrl = null)
    {
        var list = new List<StoredCookie>();
        if (array.ValueKind != JsonValueKind.Array) return list;
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var name = item.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
            if (string.IsNullOrEmpty(name)) continue;
            var value = item.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            var domain = item.TryGetProperty("domain", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() ?? "" : "";
            if (string.IsNullOrWhiteSpace(domain) && item.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String && Uri.TryCreate(u.GetString(), UriKind.Absolute, out var parsedUri))
                domain = parsedUri.Host;
            if (string.IsNullOrWhiteSpace(domain) && fallbackUrl is not null)
                domain = fallbackUrl.Host;
            domain = domain.Trim().TrimStart('.').ToLowerInvariant();
            if (string.IsNullOrEmpty(domain)) continue;
            var path = item.TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(p.GetString()) ? p.GetString()! : "/";
            long? expires = null;
            if (item.TryGetProperty("expires", out var e) && e.ValueKind == JsonValueKind.Number && e.TryGetDouble(out var expNum) && expNum > 0)
                expires = (long)expNum;
            var httpOnly = item.TryGetProperty("httpOnly", out var h) && h.ValueKind == JsonValueKind.True;
            var secure = item.TryGetProperty("secure", out var s) && s.ValueKind == JsonValueKind.True;
            var sameSite = item.TryGetProperty("sameSite", out var ss) && ss.ValueKind == JsonValueKind.String ? ss.GetString() : null;
            list.Add(new StoredCookie(name, value, domain, path, expires, httpOnly, secure, sameSite));
        }
        return list;
    }

    private static IEnumerable<string> EnumerateCandidateDomains(string host)
    {
        yield return host;
        var idx = host.IndexOf('.');
        while (idx >= 0 && idx + 1 < host.Length)
        {
            host = host[(idx + 1)..];
            if (host.Length > 0) yield return host;
            idx = host.IndexOf('.');
        }
    }

    private static bool DomainMatches(string requestHost, string cookieDomain)
    {
        cookieDomain = cookieDomain.TrimStart('.').ToLowerInvariant();
        if (string.Equals(requestHost, cookieDomain, StringComparison.OrdinalIgnoreCase)) return true;
        return requestHost.EndsWith("." + cookieDomain, StringComparison.OrdinalIgnoreCase);
    }

    private static bool PathMatches(string requestPath, string cookiePath)
    {
        if (cookiePath == "/") return true;
        if (!requestPath.StartsWith(cookiePath, StringComparison.Ordinal)) return false;
        return cookiePath.EndsWith('/') || requestPath.Length == cookiePath.Length || requestPath[cookiePath.Length] == '/';
    }

    private static StoredCookie? ParseSetCookieHeader(string header, Uri url)
    {
        var parts = header.Split(';');
        if (parts.Length == 0) return null;
        var eq = parts[0].IndexOf('=');
        if (eq <= 0) return null;
        var name = parts[0][..eq].Trim();
        var value = parts[0][(eq + 1)..].Trim();
        if (name.Length == 0) return null;

        var domain = url.Host.ToLowerInvariant();
        var path = DefaultPath(url);
        long? expires = null;
        bool httpOnly = false;
        bool secure = false;
        string? sameSite = null;

        for (var i = 1; i < parts.Length; i++)
        {
            var attr = parts[i].Trim();
            if (attr.Length == 0) continue;
            var attrEq = attr.IndexOf('=');
            var attrName = (attrEq >= 0 ? attr[..attrEq] : attr).Trim().ToLowerInvariant();
            var attrVal = attrEq >= 0 ? attr[(attrEq + 1)..].Trim() : "";
            switch (attrName)
            {
                case "domain":
                    var candidate = attrVal.TrimStart('.').ToLowerInvariant();
                    if (!string.IsNullOrEmpty(candidate) && DomainMatches(url.Host.ToLowerInvariant(), candidate))
                        domain = candidate;
                    break;
                case "path":
                    if (attrVal.StartsWith('/')) path = attrVal;
                    break;
                case "max-age":
                    if (long.TryParse(attrVal, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds))
                        expires = seconds <= 0 ? 1 : DateTimeOffset.UtcNow.ToUnixTimeSeconds() + seconds;
                    break;
                case "expires":
                    if (expires is null && DateTimeOffset.TryParse(attrVal, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dto))
                        expires = Math.Max(1, dto.ToUnixTimeSeconds());
                    break;
                case "httponly":
                    httpOnly = true;
                    break;
                case "secure":
                    secure = true;
                    break;
                case "samesite":
                    sameSite = attrVal;
                    break;
            }
        }

        return new StoredCookie(name, value, domain, path, expires, httpOnly, secure, sameSite);
    }

    private static string DefaultPath(Uri url)
    {
        var path = url.AbsolutePath;
        if (string.IsNullOrEmpty(path) || !path.StartsWith('/')) return "/";
        var lastSlash = path.LastIndexOf('/');
        return lastSlash <= 0 ? "/" : path[..lastSlash];
    }

    private static List<StoredCookie> ReadHostCookies(IArtifactStorage storage, string key)
    {
        var raw = storage.Read(key);
        if (string.IsNullOrWhiteSpace(raw)) return [];
        try
        {
            using var doc = JsonDocument.Parse(raw);
            return ParseJsonArray(doc.RootElement);
        }
        catch
        {
            return [];
        }
    }

    private static void WriteHostCookies(IArtifactStorage storage, string key, List<StoredCookie> cookies)
    {
        if (cookies.Count == 0)
        {
            storage.Delete(key);
            return;
        }
        storage.Write(key, ToJsonArray(cookies).ToJsonString());
    }
}
