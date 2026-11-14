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
    internal const int MaxMediaSections = 16;

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

    internal const int MaxSdpChars = 64 * 1024;
    internal const int MaxCandidates = 64;

    public static RtcSdp Parse(string sdp)
    {
        var result = new RtcSdp();
        if (string.IsNullOrWhiteSpace(sdp)) return result;
        if (sdp.Length > MaxSdpChars) throw new InvalidDataException("The session description is too large.");

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
                    if (result.Media.Count >= MaxMediaSections) throw new InvalidDataException("Too many media sections.");
                    currentMedia = new RtcMediaDescription
                    {
                        Kind = parts[0],
                        Port = int.TryParse(parts[1], out var p) ? p : 9,
                        Protocol = parts[2]
                    };
                    if (parts[0] is "audio" or "video")
                        foreach (var token in parts.Skip(3).Take(RtcMediaDescription.MaxCodecs))
                            if (int.TryParse(token, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var pt) && pt is >= 0 and <= 127)
                                currentMedia.PayloadTypes.Add(pt);
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
                    if (result.Candidates.Count >= MaxCandidates) throw new InvalidDataException("Too many ICE candidates.");
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
                else if (currentMedia is not null && attrName is "sendrecv" or "sendonly" or "recvonly" or "inactive")
                {
                    currentMedia.Direction = attrName;
                }
                else if (currentMedia is not null && attrName.Equals("rtcp-mux", StringComparison.OrdinalIgnoreCase))
                {
                    currentMedia.RtcpMux = true;
                }
                else if (currentMedia is not null && attrName.Equals("rtpmap", StringComparison.OrdinalIgnoreCase))
                {
                    // a=rtpmap:<pt> <name>/<clock>[/<channels>]
                    var halves = attrValue.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                    var spec = halves.Length == 2 ? halves[1].Split('/') : [];
                    if (halves.Length == 2 && spec.Length >= 2 &&
                        int.TryParse(halves[0], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var mapped) && mapped is >= 0 and <= 127 &&
                        int.TryParse(spec[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var clock) && clock is > 0 and <= 192000 &&
                        currentMedia.RtpMaps.Count < RtcMediaDescription.MaxCodecs)
                        currentMedia.RtpMaps[mapped] = new RtcCodec(mapped, spec[0].ToUpperInvariant(), clock,
                            spec.Length > 2 && int.TryParse(spec[2], out var channels) && channels is > 0 and <= 8 ? channels : 1);
                }
                else if (currentMedia is not null && attrName.Equals("fmtp", StringComparison.OrdinalIgnoreCase))
                {
                    // a=fmtp:<pt> <format specific parameters>; kept bounded, only read for codecs Folio implements
                    var halves = attrValue.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                    if (halves.Length == 2 && halves[1].Length <= 256 &&
                        int.TryParse(halves[0], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var fmtpType) && fmtpType is >= 0 and <= 127 &&
                        currentMedia.Fmtps.Count < RtcMediaDescription.MaxCodecs)
                        currentMedia.Fmtps[fmtpType] = halves[1].Trim();
                }
                else if (currentMedia is not null && attrName.Equals("rtcp-fb", StringComparison.OrdinalIgnoreCase))
                {
                    // a=rtcp-fb:<pt|*> <type> [<subtype>] (RFC 4585 section 4.2). Only what Folio acts on is kept, and bounded.
                    var parts = attrValue.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length is 2 or 3 && (parts[0] == "*" || int.TryParse(parts[0], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out _)))
                    {
                        var key = parts[0] == "*" ? -1 : int.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture);
                        var feedback = string.Join(' ', parts.Skip(1)).ToLowerInvariant();
                        if (feedback is "nack" or "nack pli" or "ccm fir") currentMedia.AddFeedback(key, feedback);
                    }
                }
                else if (currentMedia is not null && attrName.Equals("ssrc", StringComparison.OrdinalIgnoreCase))
                {
                    var id = attrValue.Split(' ', 2)[0];
                    if (uint.TryParse(id, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var ssrc) &&
                        !currentMedia.Ssrcs.Contains(ssrc) && currentMedia.Ssrcs.Count < RtcMediaDescription.MaxSsrcs)
                        currentMedia.Ssrcs.Add(ssrc);
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
                if (Media[i].Port == 0) continue; // rejected sections leave the group
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
            else if (m.Codecs.Count > 0) sb.Append(' ').Append(string.Join(' ', m.Codecs.Select(codec => codec.PayloadType))).Append("\r\n");
            else if (m.PayloadTypes.Count > 0) sb.Append(' ').Append(string.Join(' ', m.PayloadTypes)).Append("\r\n");
            else sb.Append(" 96\r\n");
            if (m.Port == 0) continue;

            sb.Append("c=IN IP4 0.0.0.0\r\n");
            sb.Append("a=mid:").Append(mid).Append("\r\n");

            if (m.Kind == "application")
            {
                sb.Append("a=sctp-port:").Append(m.SctpPort > 0 ? m.SctpPort : SctpPort).Append("\r\n");
                if (m.MaximumMessageSize is { } maximum) sb.Append("a=max-message-size:").Append(maximum).Append("\r\n");
            }
            else
            {
                sb.Append("a=").Append(m.Direction).Append("\r\n");
                sb.Append("a=rtcp-mux\r\n");
                foreach (var codec in m.Codecs)
                {
                    sb.Append("a=rtpmap:").Append(codec.PayloadType).Append(' ').Append(codec.Name).Append('/').Append(codec.ClockRate);
                    if (codec.Channels > 1) sb.Append('/').Append(codec.Channels);
                    sb.Append("\r\n");
                    if (!string.IsNullOrEmpty(codec.Fmtp)) sb.Append("a=fmtp:").Append(codec.PayloadType).Append(' ').Append(codec.Fmtp).Append("\r\n");
                }
                foreach (var codec in m.Codecs)
                    if (m.Feedback.TryGetValue(codec.PayloadType, out var feedback))
                        foreach (var item in feedback) sb.Append("a=rtcp-fb:").Append(codec.PayloadType).Append(' ').Append(item).Append("\r\n");
                foreach (var ssrc in m.Ssrcs)
                    sb.Append("a=ssrc:").Append(ssrc).Append(" cname:").Append(m.Cname ?? "folio").Append("\r\n");
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

    internal const int MaxCodecs = 32;
    internal const int MaxSsrcs = 8;
    /// <summary>a=sendrecv, sendonly, recvonly or inactive (RFC 3264); sendrecv when absent.</summary>
    public string Direction { get; set; } = "sendrecv";
    public bool RtcpMux { get; set; }
    public string? Cname { get; set; }
    /// <summary>Payload types from the m= line, in preference order.</summary>
    public List<int> PayloadTypes { get; } = [];
    public Dictionary<int, RtcCodec> RtpMaps { get; } = [];
    /// <summary>a=fmtp parameter strings by payload type.</summary>
    public Dictionary<int, string> Fmtps { get; } = [];
    /// <summary>Codecs to write when generating SDP.</summary>
    public List<RtcCodec> Codecs { get; } = [];
    public List<uint> Ssrcs { get; } = [];
    internal const int MaxFeedbackEntries = 64;
    /// <summary>RTCP feedback (a=rtcp-fb) per payload type; key -1 stands for "*", every payload type.</summary>
    public Dictionary<int, List<string>> Feedback { get; } = [];

    internal void AddFeedback(int payloadType, string feedback)
    {
        if (Feedback.Values.Sum(list => list.Count) >= MaxFeedbackEntries) return;
        if (!Feedback.TryGetValue(payloadType, out var list)) Feedback[payloadType] = list = [];
        if (!list.Contains(feedback)) list.Add(feedback);
    }

    /// <summary>True when the section lists the feedback mechanism ("nack", "pli" or "fir") for the payload type or for "*".</summary>
    internal bool HasFeedback(int payloadType, string mechanism)
    {
        var wanted = mechanism switch { "pli" => "nack pli", "fir" => "ccm fir", _ => mechanism };
        return (Feedback.TryGetValue(payloadType, out var own) && own.Contains(wanted)) || (Feedback.TryGetValue(-1, out var all) && all.Contains(wanted));
    }

    /// <summary>The codec for a payload type, from a=rtpmap or the RFC 3551 static assignments Folio understands.</summary>
    internal RtcCodec? CodecFor(int payloadType)
    {
        var codec = RtpMaps.TryGetValue(payloadType, out var mapped) ? mapped : payloadType switch
        {
            0 => new RtcCodec(0, "PCMU", 8000, 1),
            8 => new RtcCodec(8, "PCMA", 8000, 1),
            _ => null
        };
        return codec is not null && codec.Fmtp is null && Fmtps.TryGetValue(payloadType, out var fmtp) ? codec with { Fmtp = fmtp } : codec;
    }

    internal bool Sends => Direction is "sendrecv" or "sendonly";
    internal bool Receives => Direction is "sendrecv" or "recvonly";
}

internal sealed record RtcCodec(int PayloadType, string Name, int ClockRate, int Channels)
{
    /// <summary>The a=fmtp parameters, when the section carries any.</summary>
    internal string? Fmtp { get; init; }

    /// <summary>Opus as WebRTC signals it: opus/48000/2 (the channel count in the rtpmap is always 2; a=fmtp says what is actually sent).</summary>
    internal bool IsOpus => Name.Equals("opus", StringComparison.OrdinalIgnoreCase) && ClockRate == 48000 && Channels == 2;

    /// <summary>Reads one parameter of the a=fmtp list (RFC 7587), for example <c>sprop-stereo</c>.</summary>
    internal string? FmtpParameter(string name)
    {
        if (Fmtp is null) return null;
        foreach (var part in Fmtp.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = part.IndexOf('=');
            if (eq > 0 && part[..eq].Trim().Equals(name, StringComparison.OrdinalIgnoreCase)) return part[(eq + 1)..].Trim();
        }
        return null;
    }

    internal bool IsVp8 => Name == "VP8";

    /// <summary>H.264 at the 90000 Hz video clock, whatever its profile; <see cref="IsDecodableH264"/> says whether Folio can decode it.</summary>
    internal bool IsH264 => Name == "H264" && ClockRate == 90000;

    /// <summary>A video codec the media engine decodes: VP8, or H.264 Constrained Baseline in packetization mode 0 or 1.</summary>
    internal bool IsVideoDecodable => IsVp8 || IsDecodableH264;

    /// <summary>The a=fmtp profile-level-id (RFC 6184 section 8.1); a missing one means 42000a, Baseline level 1.</summary>
    internal string H264ProfileLevelId => FmtpParameter("profile-level-id")?.ToLowerInvariant() ?? "42000a";

    /// <summary>The a=fmtp packetization-mode: 0 (single NAL) when absent. Folio decodes 0 and 1 (single NAL, STAP-A, FU-A); mode 2 interleaving is not supported.</summary>
    internal int H264PacketizationMode => FmtpParameter("packetization-mode") is not { } mode ? 0
        : mode.Length == 1 && mode[0] is >= '0' and <= '9' ? mode[0] - '0' : -1; // -1: present but not a single digit

    /// <summary>
    /// True for H.264 whose profile-level-id names Baseline or Constrained Baseline (profile_idc 0x42, or 0x4d / 0x58 with
    /// constraint_set0 set, RFC 6184 section 8.1) in packetization mode 0 or 1. The level is not checked: Folio's limits are
    /// host policy, not a level conformance rule.
    /// </summary>
    internal bool IsDecodableH264
    {
        get
        {
            if (!IsH264 || H264PacketizationMode is not (0 or 1)) return false;
            // A present profile-level-id must be exactly six hex digits (RFC 6184); anything else is refused, not defaulted.
            var text = H264ProfileLevelId;
            if (text.Length != 6 || !text.All(Uri.IsHexDigit)) return false;
            var id = Convert.ToInt32(text, 16);
            var profile = id >> 16 & 0xFF; var constraints = id >> 8 & 0xFF;
            return profile == 0x42 || (profile is 0x4D or 0x58 && (constraints & 0x80) != 0);
        }
    }

    /// <summary>PCMU or PCMA at exactly 8000 Hz mono. A peer's rtpmap claiming another clock rate is not G.711 as Folio decodes it.</summary>
    internal bool IsG711 => Name is "PCMU" or "PCMA" && ClockRate == 8000 && Channels == 1;
}
