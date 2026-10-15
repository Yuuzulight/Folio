using System.Security.Cryptography;

namespace Folio.Resources;

/// <summary>
/// Subresource Integrity (https://www.w3.org/TR/SRI/): checks a response body against an element's <c>integrity</c>
/// metadata, a space-separated list of <c>sha256-</c>, <c>sha384-</c> or <c>sha512-</c> digests in base64.
/// </summary>
internal static class SubresourceIntegrity
{
    /// <summary>
    /// True when the metadata names no algorithm Folio knows (nothing to check), or when any digest of the strongest
    /// algorithm listed matches the data. Options after a <c>?</c> in a token are ignored.
    /// </summary>
    public static bool Matches(ReadOnlySpan<byte> data, string? metadata)
    {
        if (string.IsNullOrWhiteSpace(metadata))
            return true;

        var tokens = new List<(int Strength, string Algorithm, byte[] Digest)>();
        foreach (var token in metadata.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var dash = token.IndexOf('-');
            if (dash <= 0)
                continue;
            var strength = token[..dash].ToLowerInvariant() switch { "sha512" => 3, "sha384" => 2, "sha256" => 1, _ => 0 };
            if (strength == 0)
                continue;
            var encoded = token[(dash + 1)..];
            if (encoded.IndexOf('?') is var question and >= 0)
                encoded = encoded[..question];
            tokens.Add((strength, token[..dash].ToLowerInvariant(), Base64(encoded)));
        }
        if (tokens.Count == 0)
            return true;

        var strongest = tokens.Max(t => t.Strength);
        foreach (var (strength, algorithm, digest) in tokens)
        {
            if (strength != strongest || digest.Length == 0)
                continue;
            var actual = algorithm switch { "sha512" => SHA512.HashData(data), "sha384" => SHA384.HashData(data), _ => SHA256.HashData(data) };
            if (CryptographicOperations.FixedTimeEquals(actual, digest))
                return true;
        }
        return false;
    }

    private static byte[] Base64(string text)
    {
        var buffer = new byte[text.Length];
        return Convert.TryFromBase64String(text, buffer, out var length) ? buffer[..length] : [];
    }
}
