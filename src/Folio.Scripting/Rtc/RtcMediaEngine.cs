using System.Buffers.Binary;
using System.Security.Cryptography;
using Folio;
using Folio.Scripting.Rtc.Opus;

namespace Folio.Scripting.Rtc;

/// <summary>A remote audio or video track created from a negotiated m= section that the peer sends on.</summary>
internal sealed class RtcRemoteTrack
{
    public string Kind { get; }
    public string? Mid { get; }
    /// <summary>True when Folio can turn this track's payload into frames (audio Opus, PCMU or PCMA). Video is negotiated but not decoded.</summary>
    public bool Decodable { get; internal set; }
    public event Action<MediaAudioFrame>? AudioFrame;
    public event Action? Ended;
    public long PacketsReceived { get; internal set; }
    public long BytesReceived { get; internal set; }
    public long PacketsLost { get; internal set; }
    public long PacketsDropped { get; internal set; }
    public long FramesDelivered { get; internal set; }
    internal List<int> ReceiveTypes { get; } = [];
    internal bool Closed;

    internal RtcRemoteTrack(string kind, string? mid) { Kind = kind; Mid = mid; }
    internal void RaiseAudio(MediaAudioFrame frame) => AudioFrame?.Invoke(frame);
    internal void RaiseEnded() => Ended?.Invoke();
}

/// <summary>A local audio sender. Created by <see cref="RtcMediaEngine.AddLocalAudio"/>; packets leave through <see cref="RtcMediaEngine.OnPacketToSend"/>.</summary>
internal sealed class RtcLocalTrack
{
    public string Kind => "audio";
    public string? Mid { get; internal set; }
    public uint Ssrc { get; }
    public long PacketsSent { get; internal set; }
    public long BytesSent { get; internal set; }
    internal int PayloadType = -1;
    internal bool ALaw;
    internal bool Opus;
    internal OpusEncoder? Encoder;
    internal readonly float[] OpusPending = new float[OpusEncoder.FrameSamples];
    internal ushort Sequence;
    internal uint Timestamp;
    internal bool Marker = true;
    internal double Phase;
    internal float Previous;
    internal readonly short[] Pending = new short[RtcMediaEngine.SamplesPerPacket];
    internal int PendingCount;
    internal bool Released;
    internal RtcLocalTrack(uint ssrc) { Ssrc = ssrc; Timestamp = (uint)RandomNumberGenerator.GetInt32(int.MaxValue); Sequence = (ushort)RandomNumberGenerator.GetInt32(0x8000); }
}

/// <summary>
/// The media plane of one peer connection, behind the DTLS-SRTP transport: SRTP/SRTCP in and out, RTP demultiplexing by
/// payload type and SSRC, a reordering jitter buffer, G.711 audio decode and encode, and RTCP sender/receiver reports.
/// It never touches a socket: datagrams arrive through <see cref="ProcessDatagram"/> and leave through
/// <see cref="OnPacketToSend"/>, which the controller connects to the ICE transport (host-mediated in the sandbox).
/// All time comes from the injected <see cref="TimeProvider"/>; <see cref="Tick"/> drives playout and reports.
/// </summary>
internal sealed class RtcMediaEngine : IDisposable
{
    internal const int SamplesPerPacket = 160; // 20 ms at 8 kHz
    internal const int MaxPayloadBytes = 1200;
    internal const int MaxJitterPackets = 64;
    internal const int MaxStreams = 16;
    internal const int MaxSendSamplesPerCall = 96_000;
    internal static readonly TimeSpan JitterDelay = TimeSpan.FromMilliseconds(60);
    internal static readonly TimeSpan ReportInterval = TimeSpan.FromSeconds(1);

    private readonly TimeProvider _time;
    private readonly RtcMediaBudget _budget;
    private readonly object _gate = new();
    private readonly long _start;
    private readonly string _cname;
    private readonly List<Section> _sections = [];
    private readonly Dictionary<uint, RecvStream> _streams = [];
    private readonly List<RtcLocalTrack> _locals = [];
    private SrtpTransport? _srtp;
    private long _lastReport;
    private bool _disposed;
    private RtcSdp? _local, _remote;

    public event Action<byte[]>? OnPacketToSend;
    public event Action<RtcRemoteTrack>? OnRemoteTrack;

    public long RtpPacketsDropped { get; private set; }
    public long RtcpPacketsReceived { get; private set; }
    public IReadOnlyList<RtcpPacket> LastReports { get; private set; } = [];

    private sealed class Section
    {
        public string Kind = "", Mid = "";
        public bool Receives, Sends;
        public RtcRemoteTrack? Track;
        public readonly List<uint> DeclaredSsrcs = [];
        public readonly List<RtcCodec> ReceiveCodecs = [];
        public RtcCodec? SendCodec;
        public RtcLocalTrack? Local;
        public int OpusChannels = 1;
    }

    private sealed class RecvStream
    {
        public uint Ssrc;
        public Section Section = null!;
        public RtcCodec Codec = null!;
        public OpusDecoder? Opus;
        public bool Started;
        public ushort BaseSeq;
        public long MaxExt, Next;
        public long Received, ReceivedPrior, ExpectedPrior;
        public double Jitter;
        public long PrevTransit;
        public bool HasTransit;
        public uint LastSr;
        public long LastSrStamp;
        public readonly SortedDictionary<long, (byte[] Payload, long Arrival)> Buffer = [];
    }

    public RtcMediaEngine(TimeProvider? time = null, RtcMediaBudget? budget = null)
    {
        _time = time ?? TimeProvider.System;
        _budget = budget ?? new RtcMediaBudget(_time);
        _start = _lastReport = _time.GetTimestamp();
        _cname = "folio-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();
    }

    public string Cname => _cname;

    /// <summary>True for a datagram that RFC 7983 classes as RTP or RTCP (first byte 128..191).</summary>
    public static bool IsMediaDatagram(ReadOnlySpan<byte> datagram) => datagram.Length > 0 && datagram[0] is >= 128 and <= 191;

    private static bool IsRtcp(ReadOnlySpan<byte> datagram) => datagram.Length >= 2 && datagram[1] is >= 192 and <= 223;

    /// <summary>Installs the SRTP context after the DTLS handshake. Media that arrives earlier is dropped.</summary>
    public void SetSrtp(SrtpTransport srtp) { lock (_gate) _srtp = srtp; }

    /// <summary>Creates a sender for the next audio section that Folio may send on. Returns null when the track budget is exhausted.</summary>
    public RtcLocalTrack? AddLocalAudio()
    {
        if (!_budget.TryAcquireTrack()) return null;
        lock (_gate)
        {
            if (_disposed) { _budget.ReleaseTrack(); return null; }
            var track = new RtcLocalTrack(NonZeroRandom());
            _locals.Add(track);
            Bind(track);
            return track;
        }
    }

    public void RemoveLocal(RtcLocalTrack track)
    {
        lock (_gate)
        {
            if (!_locals.Remove(track) || track.Released) return;
            track.Released = true;
            foreach (var section in _sections) if (section.Local == track) section.Local = null;
        }
        _budget.ReleaseTrack();
    }

    /// <summary>The SSRC an offer or answer should advertise for the local sender, if any.</summary>
    public IReadOnlyList<RtcLocalTrack> LocalTracks { get { lock (_gate) return _locals.ToArray(); } }

    private static uint NonZeroRandom() { uint value; do value = (uint)RandomNumberGenerator.GetInt32(int.MinValue, int.MaxValue); while (value == 0); return value; }

    /// <summary>Applies the current descriptions. Safe to call repeatedly; remote tracks are created once per remote section.</summary>
    public void Apply(RtcSdp? local, RtcSdp? remote)
    {
        var raised = new List<RtcRemoteTrack>();
        lock (_gate)
        {
            if (_disposed) return;
            _local = local ?? _local;
            _remote = remote ?? _remote;
            if (_remote is null) return;
            for (var i = 0; i < _remote.Media.Count; i++)
            {
                var remoteMedia = _remote.Media[i];
                if (remoteMedia.Kind is not ("audio" or "video") || remoteMedia.Port == 0) continue;
                var mid = remoteMedia.Mid ?? i.ToString();
                var section = _sections.FirstOrDefault(s => s.Mid == mid);
                if (section is null)
                {
                    if (_sections.Count >= RtcSdp.MaxMediaSections) continue;
                    section = new Section { Kind = remoteMedia.Kind, Mid = mid };
                    _sections.Add(section);
                }
                var localMedia = _local is not null && i < _local.Media.Count && _local.Media[i].Kind == remoteMedia.Kind ? _local.Media[i] : null;
                section.DeclaredSsrcs.Clear(); section.DeclaredSsrcs.AddRange(remoteMedia.Ssrcs);
                section.ReceiveCodecs.Clear();
                section.SendCodec = null;
                section.Receives = false; section.Sends = false;
                if (localMedia is not null && localMedia.Port != 0)
                {
                    // What the peer sends us is limited to the payload types in our section; what we send must be listed by the peer too.
                    section.Receives = localMedia.Receives && remoteMedia.Sends;
                    foreach (var pt in localMedia.PayloadTypes)
                        if (localMedia.CodecFor(pt) is { } codec) section.ReceiveCodecs.Add(codec);
                    // a peer that declares sprop-stereo=1 may send stereo: decode to two channels then, otherwise mono
                    section.OpusChannels = remoteMedia.PayloadTypes.Any(pt => remoteMedia.CodecFor(pt) is { IsOpus: true } r && r.FmtpParameter("sprop-stereo") == "1") ? 2 : 1;
                    if (localMedia.Sends && remoteMedia.Receives && remoteMedia.RtcpMux)
                        foreach (var pt in localMedia.PayloadTypes)
                            if (remoteMedia.PayloadTypes.Contains(pt) && localMedia.CodecFor(pt) is { } codec && (codec.IsG711 || codec.IsOpus)) { section.SendCodec = codec; break; }
                }
                else section.Receives = remoteMedia.Sends; // answerer before its own description exists
                if (section.Track is null && remoteMedia.Sends && _budget.TryAcquireTrack())
                {
                    section.Track = new RtcRemoteTrack(section.Kind, mid);
                    raised.Add(section.Track);
                }
                if (section.Track is not null)
                {
                    section.Track.Decodable = section.Kind == "audio" && section.ReceiveCodecs.Any(c => c.IsG711 || c.IsOpus);
                    section.Track.ReceiveTypes.Clear();
                    section.Track.ReceiveTypes.AddRange(section.ReceiveCodecs.Select(c => c.PayloadType));
                }
            }
            foreach (var track in _locals) Bind(track);
        }
        foreach (var track in raised) OnRemoteTrack?.Invoke(track);
    }

    private void Bind(RtcLocalTrack track)
    {
        if (track.Released) return;
        var section = _sections.FirstOrDefault(s => s.Local == track)
            ?? _sections.FirstOrDefault(s => s.Kind == "audio" && s.Local is null && s.SendCodec is not null);
        if (section is null || section.SendCodec is not { } codec) return;
        section.Local = track;
        track.Mid = section.Mid;
        track.PayloadType = codec.PayloadType;
        track.ALaw = codec.Name == "PCMA";
        track.Opus = codec.IsOpus;
        if (track.Opus) track.Encoder ??= new OpusEncoder { Bitrate = 32000 };
    }

    /// <summary>Processes one datagram from the ICE transport that is RTP or RTCP. Malformed, unauthenticated, over-budget or unknown packets are dropped.</summary>
    public void ProcessDatagram(byte[] datagram)
    {
        ArgumentNullException.ThrowIfNull(datagram);
        if (!IsMediaDatagram(datagram) || datagram.Length > SrtpTransport.MaxPacketBytes) return;
        if (!_budget.TryConsume(receive: true, datagram.Length)) return;
        var frames = new List<(RtcRemoteTrack Track, MediaAudioFrame Frame)>();
        lock (_gate)
        {
            if (_disposed || _srtp is null) return;
            if (IsRtcp(datagram))
            {
                if (_srtp.TryUnprotectRtcp(datagram, out var rtcp) && rtcp is not null) HandleRtcp(rtcp);
                else RtpPacketsDropped++;
            }
            else if (_srtp.TryUnprotectRtp(datagram, out var rtp) && rtp is not null) HandleRtp(rtp);
            else RtpPacketsDropped++;
            Drain(frames);
        }
        Deliver(frames);
    }

    private void HandleRtp(byte[] packet)
    {
        var headerLength = SrtpTransport.RtpHeaderLength(packet);
        if (headerLength < 0) { RtpPacketsDropped++; return; }
        var end = packet.Length;
        if ((packet[0] & 0x20) != 0)
        {
            var padding = packet[^1];
            if (padding == 0 || headerLength + padding > end) { RtpPacketsDropped++; return; }
            end -= padding;
        }
        var payloadLength = end - headerLength;
        var payloadType = packet[1] & 0x7F;
        var seq = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(2));
        var timestamp = BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(4));
        var ssrc = BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(8));
        if (payloadLength < 0 || payloadLength > MaxPayloadBytes) { RtpPacketsDropped++; return; }

        if (!_streams.TryGetValue(ssrc, out var stream))
        {
            var section = SelectSection(ssrc, payloadType);
            if (section?.Track is null || _streams.Count >= MaxStreams) { RtpPacketsDropped++; return; }
            stream = new RecvStream { Ssrc = ssrc, Section = section, Codec = section.ReceiveCodecs.First(c => c.PayloadType == payloadType) };
            _streams[ssrc] = stream;
        }
        var track = stream.Section.Track!;
        if (stream.Section.ReceiveCodecs.FirstOrDefault(c => c.PayloadType == payloadType) is not { } codec) { track.PacketsDropped++; RtpPacketsDropped++; return; }
        stream.Codec = codec;

        var now = _time.GetTimestamp();
        long ext;
        if (!stream.Started)
        {
            stream.Started = true; stream.BaseSeq = seq; stream.MaxExt = stream.Next = ext = seq;
        }
        else
        {
            var diff = (short)(seq - (ushort)stream.MaxExt);
            ext = stream.MaxExt + diff;
            if (ext < 0) { track.PacketsDropped++; return; }
            if (ext > stream.MaxExt) stream.MaxExt = ext;
        }
        stream.Received++;
        track.PacketsReceived++;
        track.BytesReceived += payloadLength;

        // RFC 3550 A.8 interarrival jitter, in RTP clock units.
        var arrival = (long)(_time.GetElapsedTime(_start, now).TotalSeconds * codec.ClockRate);
        var transit = arrival - timestamp;
        if (stream.HasTransit) stream.Jitter += (Math.Abs(transit - stream.PrevTransit) - stream.Jitter) / 16.0;
        stream.PrevTransit = transit; stream.HasTransit = true;

        if (ext < stream.Next || stream.Buffer.ContainsKey(ext)) { track.PacketsDropped++; return; } // late or duplicate
        if (!track.Decodable || !(codec.IsG711 || codec.IsOpus)) return; // negotiated and counted, but there is no decoder for this payload
        stream.Buffer[ext] = (packet.AsSpan(headerLength, payloadLength).ToArray(), now);
        while (stream.Buffer.Count > MaxJitterPackets) // bounded: drop the oldest, counting the gap as loss
        {
            var oldest = stream.Buffer.First();
            stream.Buffer.Remove(oldest.Key);
            track.PacketsLost += Math.Max(0, oldest.Key - stream.Next);
            stream.Next = oldest.Key + 1;
            track.PacketsDropped++;
        }
    }

    private Section? SelectSection(uint ssrc, int payloadType)
    {
        var candidates = _sections.Where(s => s.Track is { Closed: false } && s.Receives && s.ReceiveCodecs.Any(c => c.PayloadType == payloadType)).ToArray();
        return candidates.FirstOrDefault(s => s.DeclaredSsrcs.Contains(ssrc))
            ?? candidates.FirstOrDefault(s => !_streams.Values.Any(v => v.Section == s))
            ?? candidates.FirstOrDefault();
    }

    private void Drain(List<(RtcRemoteTrack, MediaAudioFrame)> frames)
    {
        var now = _time.GetTimestamp();
        foreach (var stream in _streams.Values)
        {
            while (stream.Buffer.Count > 0)
            {
                var first = stream.Buffer.First();
                long gap = 0;
                if (first.Key != stream.Next)
                {
                    if (_time.GetElapsedTime(first.Value.Arrival, now) < JitterDelay) break;
                    gap = first.Key - stream.Next;
                    stream.Section.Track!.PacketsLost += gap;
                    stream.Next = first.Key; // waited long enough: skip the gap
                }
                stream.Buffer.Remove(first.Key);
                stream.Next = first.Key + 1;
                var payload = first.Value.Payload;
                if (stream.Codec.IsOpus) { DrainOpus(stream, payload, (int)Math.Min(gap, MaxConcealedFrames), frames); continue; }
                var samples = new float[payload.Length];
                var alaw = stream.Codec.Name == "PCMA";
                for (var i = 0; i < payload.Length; i++) samples[i] = (alaw ? G711.DecodeALaw(payload[i]) : G711.DecodeMuLaw(payload[i])) / 32768f;
                stream.Section.Track!.FramesDelivered++;
                frames.Add((stream.Section.Track, new MediaAudioFrame(stream.Codec.ClockRate, 1, samples)));
            }
        }
    }

    internal const int MaxConcealedFrames = 6;

    /// <summary>
    /// Decodes one Opus packet. When packets were lost before it, the gap is concealed frame by frame, and the last lost frame is
    /// recovered from this packet's in-band redundancy when it has any (RFC 6716 section 2.1.7).
    /// </summary>
    private void DrainOpus(RecvStream stream, byte[] payload, int lostFrames, List<(RtcRemoteTrack, MediaAudioFrame)> frames)
    {
        var track = stream.Section.Track!;
        try
        {
            var samples = OpusPacket.GetNbSamples(payload);
            if (samples <= 0 || samples > 5760) { track.PacketsDropped++; return; }
            var decoder = stream.Opus ??= new OpusDecoder(stream.Section.OpusChannels);
            var channels = decoder.Channels;
            const int lostSamples = 960;
            for (var i = 0; i < lostFrames; i++)
            {
                var pcm = new float[lostSamples * channels];
                var n = i == lostFrames - 1 && OpusPacket.GetSamplesPerFrame(payload[0]) <= lostSamples
                    ? decoder.Decode(payload, pcm, lostSamples, fec: true)
                    : decoder.Decode(default, pcm, lostSamples);
                if (n > 0) { track.FramesDelivered++; frames.Add((track, new MediaAudioFrame(48000, channels, pcm.AsSpan(0, n * channels).ToArray()))); }
            }
            var output = new float[samples * channels];
            var decoded = decoder.Decode(payload, output, samples);
            if (decoded <= 0) { track.PacketsDropped++; decoder.Reset(); return; }
            track.FramesDelivered++;
            frames.Add((track, new MediaAudioFrame(48000, channels, decoded == samples ? output : output.AsSpan(0, decoded * channels).ToArray())));
        }
        catch (Exception)
        {
            // an unexpected decoder fault must not stop the media plane; start the decoder over
            track.PacketsDropped++;
            stream.Opus = null;
        }
    }

    private static void Deliver(List<(RtcRemoteTrack Track, MediaAudioFrame Frame)> frames)
    {
        foreach (var (track, frame) in frames)
        {
            try { track.RaiseAudio(frame); } catch { /* a sink fault must not stop the media plane */ }
        }
    }

    private void HandleRtcp(byte[] rtcp)
    {
        var packets = RtcpCodec.Parse(rtcp);
        RtcpPacketsReceived++;
        LastReports = packets;
        var now = _time.GetTimestamp();
        foreach (var packet in packets)
        {
            if (packet.Kind == RtcpKind.SenderReport && _streams.TryGetValue(packet.Ssrc, out var stream))
            {
                stream.LastSr = packet.NtpMiddle; stream.LastSrStamp = now;
            }
            else if (packet.Kind == RtcpKind.Bye)
            {
                foreach (var ssrc in packet.ByeSsrcs)
                {
                    _srtp?.RemoveRemoteStream(ssrc); // the peer is done with this SSRC: free its SRTP slot
                    if (_streams.Remove(ssrc, out var gone) && !_streams.Values.Any(s => s.Section == gone.Section))
                        gone.Section.Track?.RaiseEnded();
                }
            }
        }
    }

    /// <summary>Releases jitter-buffered audio whose wait has elapsed and sends reports that are due. Called from the maintenance timer.</summary>
    public void Tick()
    {
        var frames = new List<(RtcRemoteTrack, MediaAudioFrame)>();
        byte[]? report = null;
        lock (_gate)
        {
            if (_disposed) return;
            Drain(frames);
            var now = _time.GetTimestamp();
            if (_srtp is not null && _time.GetElapsedTime(_lastReport, now) >= ReportInterval)
            {
                _lastReport = now;
                report = BuildReport(now);
            }
        }
        Deliver(frames);
        SendRtcp(report);
    }

    private byte[]? BuildReport(long now)
    {
        var sender = _locals.FirstOrDefault(t => t.PacketsSent > 0);
        var blocks = new List<RtcpReportBlock>();
        foreach (var stream in _streams.Values.Take(RtcpCodec.MaxBlocks))
        {
            if (!stream.Started) continue;
            var expected = stream.MaxExt - stream.BaseSeq + 1;
            var lost = (int)Math.Clamp(expected - stream.Received, -0x800000, 0x7FFFFF);
            var expectedInterval = expected - stream.ExpectedPrior;
            var receivedInterval = stream.Received - stream.ReceivedPrior;
            stream.ExpectedPrior = expected; stream.ReceivedPrior = stream.Received;
            var lostInterval = expectedInterval - receivedInterval;
            var fraction = expectedInterval <= 0 || lostInterval <= 0 ? (byte)0 : (byte)Math.Min(255, (lostInterval << 8) / expectedInterval);
            var dlsr = stream.LastSrStamp == 0 ? 0u : (uint)(_time.GetElapsedTime(stream.LastSrStamp, now).TotalSeconds * 65536);
            blocks.Add(new RtcpReportBlock(stream.Ssrc, fraction, lost, (uint)stream.MaxExt, (uint)stream.Jitter, stream.LastSr, dlsr));
        }
        if (sender is null && blocks.Count == 0) return null;
        var ssrc = sender?.Ssrc ?? _locals.FirstOrDefault()?.Ssrc ?? NonZeroRandomStable();
        var ntp = ToNtp(_time.GetUtcNow());
        return RtcpCodec.BuildReport(ssrc, sender is not null, ntp, sender?.Timestamp ?? 0, (uint)(sender?.PacketsSent ?? 0), (uint)(sender?.BytesSent ?? 0), blocks, _cname);
    }

    private uint _reporterSsrc;
    private uint NonZeroRandomStable() => _reporterSsrc != 0 ? _reporterSsrc : _reporterSsrc = NonZeroRandom();

    private static long ToNtp(DateTimeOffset time)
    {
        var seconds = (ulong)(time.ToUnixTimeSeconds() + 2_208_988_800L);
        var fraction = (ulong)(time.Ticks % TimeSpan.TicksPerSecond) * (1UL << 32) / (ulong)TimeSpan.TicksPerSecond;
        return (long)(seconds << 32 | fraction);
    }

    /// <summary>
    /// Sends interleaved float PCM on a local track as G.711: downmixed to mono, resampled to 8 kHz and cut into 20 ms packets.
    /// Returns the number of packets emitted; zero when the track is unbound, the transport is not ready, or a budget refused.
    /// </summary>
    public int SendAudio(RtcLocalTrack track, ReadOnlySpan<float> interleaved, int sampleRate, int channels)
    {
        if (channels is < 1 or > 8 || sampleRate is < 8000 or > 192000 || interleaved.Length > MaxSendSamplesPerCall * channels) return 0;
        var packets = new List<byte[]>();
        lock (_gate)
        {
            if (_disposed || _srtp is null || track.Released || track.PayloadType < 0) return 0;
            // ponytail: linear interpolation, no anti-alias low-pass; add a polyphase filter if 48 kHz capture aliasing matters.
            // Opus runs at 48 kHz (a 48 kHz capture passes through unchanged); G.711 at 8 kHz.
            var step = sampleRate / (track.Opus ? 48000.0 : 8000.0);
            var n = interleaved.Length / channels;
            var p = track.Phase; // position of the next output relative to this call's first frame; frame -1 is the carried sample
            while (p < n - 1)
            {
                var i0 = (int)Math.Floor(p);
                var x0 = i0 < 0 ? track.Previous : Mono(interleaved, i0, channels);
                var x1 = Mono(interleaved, i0 + 1, channels);
                Push(track, (float)(x0 + (x1 - x0) * (p - i0)), packets);
                p += step;
            }
            if (n > 0) { track.Previous = Mono(interleaved, n - 1, channels); track.Phase = p - n; }
        }
        var sent = 0;
        foreach (var packet in packets)
        {
            if (!_budget.TryConsume(receive: false, packet.Length)) break;
            Send(packet);
            sent++;
        }
        return sent;
    }

    private static float Mono(ReadOnlySpan<float> data, int frame, int channels)
    {
        var sum = 0f;
        for (var c = 0; c < channels; c++) sum += data[frame * channels + c];
        return sum / channels;
    }

    private void Push(RtcLocalTrack track, float sample, List<byte[]> packets)
    {
        if (float.IsNaN(sample)) sample = 0;
        if (track.Opus) { PushOpus(track, sample, packets); return; }
        track.Pending[track.PendingCount++] = (short)Math.Clamp((int)Math.Round(sample * 32767f), short.MinValue, short.MaxValue);
        if (track.PendingCount < SamplesPerPacket) return;
        track.PendingCount = 0;
        var rtp = new byte[12 + SamplesPerPacket];
        rtp[0] = 0x80;
        rtp[1] = (byte)(track.PayloadType | (track.Marker ? 0x80 : 0));
        track.Marker = false;
        BinaryPrimitives.WriteUInt16BigEndian(rtp.AsSpan(2), track.Sequence++);
        BinaryPrimitives.WriteUInt32BigEndian(rtp.AsSpan(4), track.Timestamp);
        BinaryPrimitives.WriteUInt32BigEndian(rtp.AsSpan(8), track.Ssrc);
        track.Timestamp += SamplesPerPacket;
        for (var i = 0; i < SamplesPerPacket; i++) rtp[12 + i] = track.ALaw ? G711.EncodeALaw(track.Pending[i]) : G711.EncodeMuLaw(track.Pending[i]);
        track.PacketsSent++;
        track.BytesSent += SamplesPerPacket;
        packets.Add(_srtp!.ProtectRtp(rtp));
    }

    private void PushOpus(RtcLocalTrack track, float sample, List<byte[]> packets)
    {
        track.OpusPending[track.PendingCount++] = Math.Clamp(sample, -1f, 1f);
        if (track.PendingCount < OpusEncoder.FrameSamples) return;
        track.PendingCount = 0;
        Span<byte> payload = stackalloc byte[OpusEncoder.MaxBytes + 1];
        var length = track.Encoder!.Encode(track.OpusPending, payload, out _);
        var rtp = new byte[12 + length];
        rtp[0] = 0x80;
        rtp[1] = (byte)(track.PayloadType | (track.Marker ? 0x80 : 0));
        track.Marker = false;
        BinaryPrimitives.WriteUInt16BigEndian(rtp.AsSpan(2), track.Sequence++);
        BinaryPrimitives.WriteUInt32BigEndian(rtp.AsSpan(4), track.Timestamp);
        BinaryPrimitives.WriteUInt32BigEndian(rtp.AsSpan(8), track.Ssrc);
        track.Timestamp += OpusEncoder.FrameSamples;
        payload[..length].CopyTo(rtp.AsSpan(12));
        track.PacketsSent++;
        track.BytesSent += length;
        packets.Add(_srtp!.ProtectRtp(rtp));
    }

    private void Send(byte[] datagram)
    {
        try { OnPacketToSend?.Invoke(datagram); } catch { /* transport faults are the transport's to report */ }
    }

    private void SendRtcp(byte[]? plain)
    {
        if (plain is null) return;
        byte[] protectedPacket;
        lock (_gate)
        {
            if (_srtp is null) return;
            try { protectedPacket = _srtp.ProtectRtcp(plain); } catch (InvalidOperationException) { return; }
        }
        if (_budget.TryConsume(receive: false, protectedPacket.Length)) Send(protectedPacket);
    }

    public void Dispose()
    {
        RtcRemoteTrack[] tracks;
        int release;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            tracks = _sections.Select(s => s.Track).Where(t => t is not null).Select(t => t!).ToArray();
            release = tracks.Length + _locals.Count(l => !l.Released);
            foreach (var local in _locals) local.Released = true;
            _streams.Clear();
        }
        for (var i = 0; i < release; i++) _budget.ReleaseTrack();
        foreach (var track in tracks) { track.Closed = true; track.RaiseEnded(); }
    }
}
