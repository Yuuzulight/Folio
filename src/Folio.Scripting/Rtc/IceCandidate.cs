using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Folio.Scripting.Rtc;

/// <summary>Represents an ICE candidate per RFC 5245 / RFC 8445.</summary>
internal sealed record IceCandidate
{
    public required string Foundation { get; init; }
    public required int Component { get; init; } // 1 = RTP, 2 = RTCP
    public required string Protocol { get; init; } // "udp"
    public required long Priority { get; init; }
    public required string Address { get; init; }
    public required int Port { get; init; }
    public required string Type { get; init; } // "host", "srflx", "prflx", "relay"
    public string? RelatedAddress { get; init; }
    public int? RelatedPort { get; init; }
    public string? SdpMid { get; init; }
    public int? SdpMLineIndex { get; init; }
    public string? UsernameFragment { get; init; }

    public static long ComputePriority(string type, int component, int localPreference = 65535)
    {
        var typePref = type switch
        {
            "host" => 126,
            "prflx" => 110,
            "srflx" => 100,
            "relay" => 0,
            _ => 50
        };
        return ((long)typePref << 24) | ((long)(localPreference & 0xFFFF) << 8) | (long)(256 - component);
    }

    public string ToSdp()
    {
        var result = $"candidate:{Foundation} {Component} {Protocol.ToLowerInvariant()} {Priority} {Address} {Port} typ {Type}";
        if (!string.IsNullOrEmpty(RelatedAddress) && RelatedPort.HasValue)
            result += $" raddr {RelatedAddress} rport {RelatedPort.Value}";
        if (!string.IsNullOrEmpty(UsernameFragment))
            result += $" ufrag {UsernameFragment}";
        return result;
    }

    public static bool TryParse(string raw, [NotNullWhen(true)] out IceCandidate? candidate)
    {
        candidate = null;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        var text = raw.Trim();
        if (text.StartsWith("a=candidate:", StringComparison.OrdinalIgnoreCase))
            text = text["a=candidate:".Length..].Trim();
        else if (text.StartsWith("candidate:", StringComparison.OrdinalIgnoreCase))
            text = text["candidate:".Length..].Trim();

        var tokens = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length < 8) return false;

        var foundation = tokens[0];
        if (!int.TryParse(tokens[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var component)) return false;
        var protocol = tokens[2];
        if (!long.TryParse(tokens[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var priority)) return false;
        var address = tokens[4];
        if (!int.TryParse(tokens[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out var port)) return false;
        if (!string.Equals(tokens[6], "typ", StringComparison.OrdinalIgnoreCase)) return false;
        var type = tokens[7];

        string? raddr = null;
        int? rport = null;
        string? ufrag = null;

        for (var i = 8; i < tokens.Length - 1; i += 2)
        {
            var key = tokens[i].ToLowerInvariant();
            var val = tokens[i + 1];
            if (key == "raddr") raddr = val;
            else if (key == "rport" && int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedPort)) rport = parsedPort;
            else if (key == "ufrag") ufrag = val;
        }

        candidate = new IceCandidate
        {
            Foundation = foundation,
            Component = component,
            Protocol = protocol,
            Priority = priority,
            Address = address,
            Port = port,
            Type = type,
            RelatedAddress = raddr,
            RelatedPort = rport,
            UsernameFragment = ufrag
        };
        return true;
    }
}
