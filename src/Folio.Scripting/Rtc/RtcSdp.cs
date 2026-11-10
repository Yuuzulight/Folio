using System.Text;

namespace Folio.Scripting.Rtc;

/// <summary>
/// RFC 4566 / RFC 8866 Session Description Protocol (SDP) parser and generator for WebRTC.
/// </summary>
internal sealed class RtcSdp
{
    public string Type { get; set; } = "offer";
    public string? IceUfrag { get; set; }
    public string? IcePassword { get; set; }
    public string? FingerprintAlgorithm { get; set; } = "sha-256";
    public string? FingerprintValue { get; set; }
    public string Setup { get; set; } = "actpass"; // "actpass", "active", "passive"
    public int SctpPort { get; set; } = 5000;
    public List<IceCandidate> Candidates { get; } = [];
    public List<RtcMediaDescription> Media { get; } = [];
    public List<string> BundleMids { get; } = [];

    internal RtcMediaDescription? TransportMedia()
    {
        var active = Media.Where(media => media.Port != 0).ToArray();
        if (active.Length == 0) return null;
        if (BundleMids.Count == 0)
        {
            if (active.Length > 1) throw new NotSupportedException("Multiple unbundled ICE transports are not implemented.");
            return active[0];
        }
        if (active.Any(media => media.Mid is null || !BundleMids.Contains(media.Mid)))
            throw new NotSupportedException("Separate ICE transports outside the BUNDLE group are not implemented.");
        return BundleMids.Select(mid => active.FirstOrDefault(media => media.Mid == mid)).FirstOrDefault(media => media is not null)
            ?? throw new InvalidOperationException("The BUNDLE group has no active transport.");
    }

    internal string TransportSetup => TransportMedia()?.Setup ?? Setup;
    internal string? TransportIceUfrag => TransportMedia()?.IceUfrag ?? IceUfrag;
    internal string? TransportIcePassword => TransportMedia()?.IcePassword ?? IcePassword;
    internal string? TransportFingerprint => TransportMedia()?.FingerprintValue ?? FingerprintValue;
    internal IReadOnlyList<IceCandidate> TransportCandidates => TransportMedia() is { Candidates.Count: > 0 } media ? media.Candidates : Candidates;
    internal ulong RemoteMaximumMessageSize => Media.FirstOrDefault(media => media.Kind == "application" && media.Port != 0)?.MaximumMessageSize ?? 65536;
    internal int RemoteSctpPort => Media.FirstOrDefault(media => media.Kind == "application" && media.Port != 0)?.SctpPort ?? SctpPort;

    public static RtcSdp Parse(string sdp)
    {
        var result = new RtcSdp();
        if (string.IsNullOrWhiteSpace(sdp)) return result;

        var lines = sdp.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        RtcMediaDescription? currentMedia = null;

        foreach (var line in lines)
        {
            if (line.Length < 2 || line[1] != '=') continue;
            var type = line[0];
            var value = line[2..].Trim();

            if (type == 'm')
            {
                var parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 3)
                {
                    currentMedia = new RtcMediaDescription
                    {
                        Kind = parts[0],
                        Port = int.TryParse(parts[1], out var p) ? p : 9,
                        Protocol = parts[2]
                    };
                    result.Media.Add(currentMedia);
                }
            }
            else if (type == 'a')
            {
                var colon = value.IndexOf(':');
                var attrName = colon >= 0 ? value[..colon].Trim() : value;
                var attrValue = colon >= 0 ? value[(colon + 1)..].Trim() : "";

                if (attrName.Equals("group", StringComparison.OrdinalIgnoreCase) && currentMedia is null && attrValue.StartsWith("BUNDLE ", StringComparison.Ordinal))
                {
                    if (result.BundleMids.Count != 0) throw new NotSupportedException("Multiple BUNDLE groups are not implemented.");
                    result.BundleMids.AddRange(attrValue.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1));
                }
                else if (attrName.Equals("ice-ufrag", StringComparison.OrdinalIgnoreCase))
                {
                    if (currentMedia != null) currentMedia.IceUfrag = attrValue;
                    else result.IceUfrag = attrValue;
                }
                else if (attrName.Equals("ice-pwd", StringComparison.OrdinalIgnoreCase))
                {
                    if (currentMedia != null) currentMedia.IcePassword = attrValue;
                    else result.IcePassword = attrValue;
                }
                else if (attrName.Equals("fingerprint", StringComparison.OrdinalIgnoreCase))
                {
                    var fpParts = attrValue.Split(' ', 2);
                    if (fpParts.Length == 2)
                    {
                        if (currentMedia != null)
                        {
                            currentMedia.FingerprintAlgorithm = fpParts[0];
                            currentMedia.FingerprintValue = fpParts[1];
                        }
                        else
                        {
                            result.FingerprintAlgorithm = fpParts[0];
                            result.FingerprintValue = fpParts[1];
                        }
                    }
                }
                else if (attrName.Equals("setup", StringComparison.OrdinalIgnoreCase))
                {
                    if (currentMedia != null) currentMedia.Setup = attrValue;
                    else result.Setup = attrValue;
                }
                else if (attrName.Equals("sctp-port", StringComparison.OrdinalIgnoreCase))
                {
                    if (int.TryParse(attrValue, out var port))
                    {
                        if (currentMedia != null) currentMedia.SctpPort = port;
                        result.SctpPort = port;
                    }
                }
                else if (attrName.Equals("max-message-size", StringComparison.OrdinalIgnoreCase))
                {
                    if (currentMedia is null || currentMedia.Kind != "application" || currentMedia.MaximumMessageSize is not null ||
                        !ulong.TryParse(attrValue, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var maximum))
                        throw new InvalidDataException("Invalid SCTP max-message-size attribute.");
                    currentMedia.MaximumMessageSize = maximum;
                }
                else if (attrName.Equals("candidate", StringComparison.OrdinalIgnoreCase))
                {
                    if (IceCandidate.TryParse("candidate:" + attrValue, out var candidate))
                    {
                        if (currentMedia != null) currentMedia.Candidates.Add(candidate);
                        result.Candidates.Add(candidate);
                    }
                }
                else if (attrName.Equals("mid", StringComparison.OrdinalIgnoreCase) && currentMedia != null)
                {
                    currentMedia.Mid = attrValue;
                }
            }
        }

        return result;
    }

    public string ToSdp()
    {
        var sb = new StringBuilder();
        sb.Append("v=0\r\n");
        sb.Append("o=- ").Append(DateTimeOffset.UtcNow.ToUnixTimeSeconds()).Append(" 2 IN IP4 127.0.0.1\r\n");
        sb.Append("s=-\r\n");
        sb.Append("t=0 0\r\n");

        if (Media.Count > 0)
        {
            sb.Append("a=group:BUNDLE");
            for (var i = 0; i < Media.Count; i++)
            {
                var mid = Media[i].Mid ?? i.ToString();
                sb.Append(' ').Append(mid);
            }
            sb.Append("\r\n");
        }

        sb.Append("a=msid-semantic: WMS\r\n");

        if (!string.IsNullOrEmpty(IceUfrag)) sb.Append("a=ice-ufrag:").Append(IceUfrag).Append("\r\n");
        if (!string.IsNullOrEmpty(IcePassword)) sb.Append("a=ice-pwd:").Append(IcePassword).Append("\r\n");
        if (!string.IsNullOrEmpty(FingerprintValue))
            sb.Append("a=fingerprint:").Append(FingerprintAlgorithm ?? "sha-256").Append(' ').Append(FingerprintValue).Append("\r\n");

        sb.Append("a=setup:").Append(Setup).Append("\r\n");

        for (var i = 0; i < Media.Count; i++)
        {
            var m = Media[i];
            var mid = m.Mid ?? i.ToString();
            sb.Append("m=").Append(m.Kind).Append(' ').Append(m.Port).Append(' ').Append(m.Protocol);
            if (m.Kind == "application") sb.Append(" webrtc-datachannel\r\n");
            else sb.Append(" 96\r\n");

            sb.Append("c=IN IP4 0.0.0.0\r\n");
            sb.Append("a=mid:").Append(mid).Append("\r\n");

            if (m.Kind == "application")
            {
                sb.Append("a=sctp-port:").Append(m.SctpPort > 0 ? m.SctpPort : SctpPort).Append("\r\n");
                if (m.MaximumMessageSize is { } maximum) sb.Append("a=max-message-size:").Append(maximum).Append("\r\n");
            }
            else
            {
                sb.Append("a=sendrecv\r\n");
                sb.Append("a=rtcp-mux\r\n");
            }

            var mediaUfrag = m.IceUfrag ?? IceUfrag;
            var mediaPwd = m.IcePassword ?? IcePassword;
            if (!string.IsNullOrEmpty(mediaUfrag)) sb.Append("a=ice-ufrag:").Append(mediaUfrag).Append("\r\n");
            if (!string.IsNullOrEmpty(mediaPwd)) sb.Append("a=ice-pwd:").Append(mediaPwd).Append("\r\n");
            if (!string.IsNullOrEmpty(m.Setup)) sb.Append("a=setup:").Append(m.Setup).Append("\r\n");
            if (!string.IsNullOrEmpty(m.FingerprintValue))
                sb.Append("a=fingerprint:").Append(m.FingerprintAlgorithm ?? "sha-256").Append(' ').Append(m.FingerprintValue).Append("\r\n");

            var candidates = m.Candidates.Count > 0 ? m.Candidates : Candidates;
            foreach (var candidate in candidates)
            {
                sb.Append("a=").Append(candidate.ToSdp()).Append("\r\n");
            }
        }

        return sb.ToString();
    }
}

internal sealed class RtcMediaDescription
{
    public string Kind { get; set; } = "application";
    public int Port { get; set; } = 9;
    public string Protocol { get; set; } = "UDP/DTLS/SCTP";
    public string? Mid { get; set; }
    public string? IceUfrag { get; set; }
    public string? IcePassword { get; set; }
    public string? FingerprintAlgorithm { get; set; }
    public string? FingerprintValue { get; set; }
    public string? Setup { get; set; }
    public int SctpPort { get; set; } = 5000;
    public ulong? MaximumMessageSize { get; set; }
    public List<IceCandidate> Candidates { get; } = [];
}
