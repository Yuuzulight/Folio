namespace Folio.Scripting.Rtc;

/// <summary>
/// Coordinates Folio's ICE agent, DTLS transport, SRTP transport, and SCTP data channels for an RTCPeerConnection.
/// </summary>
internal sealed class RtcPeerConnectionController : IDisposable
{
    public IceAgent Ice { get; }
    public DtlsTransport Dtls { get; }
    public SctpDataChannel Sctp { get; }
    public SrtpTransport? Srtp { get; private set; }
    /// <summary>RTP/RTCP plane: receives demultiplexed media datagrams from ICE and sends SRTP back through it.</summary>
    public RtcMediaEngine Media { get; }

    public string ConnectionState { get; private set; } = "new";
    public string IceConnectionState { get; private set; } = "new";
    public string IceGatheringState => Ice.GatheringState;
    public string SignalingState { get; private set; } = "stable";

    public RtcSdp? LocalDescription { get; private set; }
    public RtcSdp? RemoteDescription { get; private set; }

    public event Action<string>? OnConnectionStateChange;
    public event Action<string>? OnIceConnectionStateChange;
    public event Action<string>? OnIceGatheringStateChange;
    public event Action<string>? OnSignalingStateChange;
    public event Action<IceCandidate?>? OnIceCandidate;
    public event Action<SctpDataChannel.ChannelState>? OnDataChannel;
    /// <summary>A negotiated remote audio/video track exists (raised from SetRemoteDescription).</summary>
    public event Action<RtcRemoteTrack>? OnRemoteTrack;

    private readonly object _gate = new();
    private bool _disposed;

    public RtcPeerConnectionController(IReadOnlyList<string>? iceServers = null, RtcMediaBudget? mediaBudget = null, TimeProvider? time = null)
    {
        Ice = new IceAgent(iceServers);
        Media = new RtcMediaEngine(time, mediaBudget);
        Media.OnPacketToSend += bytes => Ice.SendDatagram(bytes);
        Media.OnRemoteTrack += track => OnRemoteTrack?.Invoke(track);
        Dtls = new DtlsTransport(TimeProvider.System, enableRetryTimer: false);
        Sctp = new SctpDataChannel(TimeProvider.System, enableRetryTimer: false);
        Ice.OnMaintenance += () =>
        {
            Dtls.RetryDueFlight(); Sctp.RetryDueHandshake(); Sctp.RetryDueData(); Media.Tick();
        };

        Ice.OnCandidateGathered += candidate => OnIceCandidate?.Invoke(candidate);
        Ice.OnGatheringStateChanged += state =>
        {
            OnIceGatheringStateChange?.Invoke(state);
            if (state == "complete") OnIceCandidate?.Invoke(null); // End-of-candidates indicator
        };

        Ice.OnConnectionStateChanged += state =>
        {
            IceConnectionState = state;
            OnIceConnectionStateChange?.Invoke(state);

            if (state == "connected")
            {
                // ICE connected: initiate DTLS handshake
                Dtls.StartHandshake();
            }
            else if (state == "closed")
            {
                ConnectionState = "closed";
                OnConnectionStateChange?.Invoke("closed");
            }
        };

        // Wire datagram flow: ICE <-> DTLS
        // RFC 7983: first byte 128..191 is RTP/RTCP; everything else stays with DTLS.
        Ice.OnDatagramReceived += bytes =>
        {
            if (RtcMediaEngine.IsMediaDatagram(bytes)) Media.ProcessDatagram(bytes);
            else Dtls.ProcessIncomingDatagram(bytes);
        };
        Dtls.OnDatagramToSend += bytes => Ice.SendDatagram(bytes);

        // Wire application data flow: DTLS <-> SCTP
        Dtls.OnApplicationDataReceived += bytes => Sctp.ProcessIncomingPacket(bytes);
        Sctp.OnPacketToSend += bytes => Dtls.SendApplicationData(bytes);

        Dtls.OnStateChanged += state =>
        {
            if (state == "connected")
            {
                ConnectionState = "connected";
                OnConnectionStateChange?.Invoke("connected");

                // Initialize SRTP with keys derived from DTLS
                if (Dtls.SrtpLocalMasterKey is not null && Dtls.SrtpRemoteMasterKey is not null &&
                    Dtls.SrtpLocalMasterSalt is not null && Dtls.SrtpRemoteMasterSalt is not null)
                {
                    Srtp = new SrtpTransport(Dtls.SrtpLocalMasterKey, Dtls.SrtpLocalMasterSalt,
                        Dtls.SrtpRemoteMasterKey, Dtls.SrtpRemoteMasterSalt);
                    Media.SetSrtp(Srtp);
                }

                // Start SCTP association
                Sctp.StartAssociation(Dtls.IsClient);
            }
            else if (state == "closed")
            {
                ConnectionState = "closed";
                OnConnectionStateChange?.Invoke("closed");
            }
        };

        Sctp.OnDataChannelCreated += channel => OnDataChannel?.Invoke(channel);
    }

    private readonly List<RtcLocalTrack> _localAudio = [];

    private RtcLocalTrack? LocalAudio(int index)
    {
        while (_localAudio.Count <= index)
        {
            if (Media.AddLocalAudio() is not { } track) return null;
            _localAudio.Add(track);
        }
        return _localAudio[index];
    }

    /// <summary>The i-th audio sender, in addTrack order, once an offer or answer has created it.</summary>
    public RtcLocalTrack? LocalAudioTrack(int index) { lock (_gate) return index >= 0 && index < _localAudio.Count ? _localAudio[index] : null; }

    private static readonly RtcCodec[] AudioCodecs = [new(0, "PCMU", 8000, 1), new(8, "PCMA", 8000, 1)];
    private static readonly RtcCodec[] VideoCodecs = [new(96, "VP8", 90000, 1)];

    private RtcMediaDescription MediaSection(string kind, string mid, string direction, RtcLocalTrack? sender)
    {
        var media = new RtcMediaDescription
        {
            Kind = kind, Port = 9, Protocol = "UDP/TLS/RTP/SAVPF", Mid = mid, Direction = direction, RtcpMux = true, Cname = Media.Cname
        };
        media.Codecs.AddRange(kind == "audio" ? AudioCodecs : VideoCodecs);
        if (sender is not null) media.Ssrcs.Add(sender.Ssrc);
        return media;
    }

    private static RtcMediaDescription DataSection(string mid) => new()
    {
        Kind = "application", Port = 9, Protocol = "UDP/DTLS/SCTP", Mid = mid, SctpPort = 5000, MaximumMessageSize = SctpDataChannel.MaxMessageBytes
    };

    private void AddMediaSections(RtcSdp sdp, bool hasDataChannels, int audioSenders, int videoSenders, int audioReceivers, int videoReceivers)
    {
        var midIndex = hasDataChannels ? 1 : 0;
        if (hasDataChannels) sdp.Media.Add(DataSection("0"));
        // Folio sends and decodes G.711 audio only. Video sections keep the m-line order but are negotiated inactive.
        for (var i = 0; i < audioSenders && sdp.Media.Count < RtcSdp.MaxMediaSections; i++)
            sdp.Media.Add(MediaSection("audio", (midIndex++).ToString(), "sendrecv", LocalAudio(i)));
        for (var i = 0; i < audioReceivers && sdp.Media.Count < RtcSdp.MaxMediaSections; i++)
            sdp.Media.Add(MediaSection("audio", (midIndex++).ToString(), "recvonly", null));
        for (var i = 0; i < videoSenders + videoReceivers && sdp.Media.Count < RtcSdp.MaxMediaSections; i++)
            sdp.Media.Add(MediaSection("video", (midIndex++).ToString(), "inactive", null));
    }

    public async Task<string> CreateOfferAsync(bool hasDataChannels, int audioSenders, int videoSenders, int audioReceivers = 0, int videoReceivers = 0)
    {
        var sdp = new RtcSdp
        {
            Type = "offer",
            IceUfrag = Ice.LocalUfrag,
            IcePassword = Ice.LocalPassword,
            FingerprintValue = Dtls.Fingerprint,
            Setup = "actpass"
        };
        lock (_gate) AddMediaSections(sdp, hasDataChannels, Math.Max(0, audioSenders), Math.Max(0, videoSenders), Math.Max(0, audioReceivers), Math.Max(0, videoReceivers));

        await Ice.StartGatheringAsync().ConfigureAwait(false);
        sdp.Candidates.AddRange(Ice.LocalCandidates);

        return sdp.ToSdp();
    }

    public async Task<string> CreateAnswerAsync(bool hasDataChannels, int audioSenders, int videoSenders)
    {
        var sdp = new RtcSdp
        {
            Type = "answer",
            IceUfrag = Ice.LocalUfrag,
            IcePassword = Ice.LocalPassword,
            FingerprintValue = Dtls.Fingerprint,
            Setup = RemoteDescription?.TransportSetup == "active" ? "passive" : "active"
        };

        lock (_gate)
        {
            if (RemoteDescription is not { Media.Count: > 0 } remote)
                AddMediaSections(sdp, hasDataChannels, Math.Max(0, audioSenders), Math.Max(0, videoSenders), 0, 0);
            else
            {
                // An answer mirrors the offer: same sections, same order, same mids (RFC 3264). Sections Folio cannot serve are rejected.
                var audioIndex = 0;
                for (var i = 0; i < remote.Media.Count; i++)
                {
                    var offered = remote.Media[i];
                    var mid = offered.Mid ?? i.ToString();
                    if (offered.Kind == "application" && offered.Port != 0) { sdp.Media.Add(DataSection(mid)); continue; }
                    var accepted = offered.Kind == "audio" && offered.Port != 0
                        ? offered.PayloadTypes.Select(pt => offered.CodecFor(pt)).Where(c => c is { Name: "PCMU" or "PCMA" }).Cast<RtcCodec>().ToArray()
                        : [];
                    if (accepted.Length == 0)
                    {
                        var rejected = new RtcMediaDescription { Kind = offered.Kind, Port = 0, Protocol = offered.Protocol, Mid = mid };
                        if (offered.Kind is "audio" or "video") rejected.Codecs.Add(offered.PayloadTypes.Count > 0 && offered.CodecFor(offered.PayloadTypes[0]) is { } first ? first : new RtcCodec(0, "PCMU", 8000, 1));
                        sdp.Media.Add(rejected);
                        continue;
                    }
                    var sender = offered.Receives && audioIndex < Math.Max(0, audioSenders) ? LocalAudio(audioIndex++) : null;
                    var direction = (sender is not null, offered.Sends) switch
                    {
                        (true, true) => "sendrecv",
                        (true, false) => "sendonly",
                        (false, true) => "recvonly",
                        _ => "inactive"
                    };
                    var media = MediaSection("audio", mid, direction, sender);
                    media.Codecs.Clear();
                    media.Codecs.AddRange(accepted.Take(1)); // one codec keeps both directions on the same payload type
                    sdp.Media.Add(media);
                }
            }
        }

        await Ice.StartGatheringAsync().ConfigureAwait(false);
        sdp.Candidates.AddRange(Ice.LocalCandidates);

        return sdp.ToSdp();
    }

    public void SetLocalDescription(string type, string sdp)
    {
        lock (_gate)
        {
            var description = RtcSdp.Parse(sdp);
            description.Type = type;
            var transport = description.TransportMedia();
            if (type == "answer")
            {
                Dtls.IsClient = (transport?.Setup ?? description.Setup) switch
                {
                    "active" => true,
                    "passive" => false,
                    _ when string.IsNullOrWhiteSpace(sdp) => Dtls.IsClient,
                    _ => throw new InvalidOperationException("A DTLS answer must select active or passive setup.")
                };
            }

            LocalDescription = description;
            Media.Apply(description, null);
            if (type == "offer")
            {
                Ice.IsControlling = true;
                SignalingState = "have-local-offer";
                OnSignalingStateChange?.Invoke(SignalingState);
            }
            else if (type == "answer")
            {
                SignalingState = "stable";
                OnSignalingStateChange?.Invoke(SignalingState);
            }
        }

        _ = Ice.StartGatheringAsync();
    }

    public void SetRemoteDescription(string type, string sdp)
    {
        lock (_gate)
        {
            var description = RtcSdp.Parse(sdp);
            description.Type = type;
            var setup = description.TransportSetup;
            var isClient = type switch
            {
                "offer" => setup switch
                {
                    "actpass" or "passive" => true,
                    "active" => false,
                    _ => throw new InvalidOperationException("Unsupported remote DTLS setup.")
                },
                "answer" => setup switch
                {
                    "active" => false,
                    "passive" => true,
                    _ when string.IsNullOrWhiteSpace(sdp) => Dtls.IsClient,
                    _ => throw new InvalidOperationException("A remote DTLS answer must select active or passive setup.")
                },
                _ => Dtls.IsClient
            };
            var candidates = description.TransportCandidates;
            var ufrag = description.TransportIceUfrag;
            var password = description.TransportIcePassword;
            var fingerprint = description.TransportFingerprint;
            Sctp.ConfigureRemote(description.RemoteMaximumMessageSize, description.RemoteSctpPort);

            RemoteDescription = description;
            Dtls.IsClient = isClient;
            if (!string.IsNullOrEmpty(ufrag)) Ice.RemoteUfrag = ufrag;
            if (!string.IsNullOrEmpty(password)) Ice.RemotePassword = password;
            if (!string.IsNullOrEmpty(fingerprint)) Dtls.RemoteFingerprint = fingerprint;

            if (type == "offer")
            {
                Ice.IsControlling = false;
                SignalingState = "have-remote-offer";
                OnSignalingStateChange?.Invoke(SignalingState);
            }
            else if (type == "answer")
            {
                SignalingState = "stable";
                OnSignalingStateChange?.Invoke(SignalingState);
            }

            foreach (var candidate in candidates)
                Ice.AddRemoteCandidate(candidate);
            Media.Apply(null, description);
        }
    }

    public void AddIceCandidate(string candidateInit)
    {
        if (IceCandidate.TryParse(candidateInit, out var candidate))
        {
            Ice.AddRemoteCandidate(candidate);
        }
    }

    public SctpDataChannel.ChannelState CreateDataChannel(string label, string protocol = "", ushort? id = null)
    {
        return Sctp.CreateDataChannel(label, protocol, id);
    }

    public void Close() => Dispose();

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            ConnectionState = "closed";
            SignalingState = "closed";
        }
        Ice.Dispose();
        Dtls.Dispose();
        Sctp.Dispose();
        Media.Dispose();
        Srtp?.Dispose();
        OnConnectionStateChange?.Invoke("closed");
    }
}
