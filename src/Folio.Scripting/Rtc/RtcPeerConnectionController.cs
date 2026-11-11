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

    private readonly object _gate = new();
    private bool _disposed;

    public RtcPeerConnectionController(IReadOnlyList<string>? iceServers = null)
    {
        Ice = new IceAgent(iceServers);
        Dtls = new DtlsTransport(TimeProvider.System, enableRetryTimer: false);
        Sctp = new SctpDataChannel(TimeProvider.System, enableRetryTimer: false);
        Ice.OnMaintenance += () =>
        {
            Dtls.RetryDueFlight(); Sctp.RetryDueHandshake(); Sctp.RetryDueData();
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
        Ice.OnDatagramReceived += bytes => Dtls.ProcessIncomingDatagram(bytes);
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

    public async Task<string> CreateOfferAsync(bool hasDataChannels, int audioSenders, int videoSenders)
    {
        var sdp = new RtcSdp
        {
            Type = "offer",
            IceUfrag = Ice.LocalUfrag,
            IcePassword = Ice.LocalPassword,
            FingerprintValue = Dtls.Fingerprint,
            Setup = "actpass"
        };

        if (hasDataChannels)
        {
            sdp.Media.Add(new RtcMediaDescription
            {
                Kind = "application",
                Port = 9,
                Protocol = "UDP/DTLS/SCTP",
                Mid = "0",
                SctpPort = 5000,
                MaximumMessageSize = SctpDataChannel.MaxMessageBytes
            });
        }

        var midIndex = hasDataChannels ? 1 : 0;
        for (var i = 0; i < audioSenders; i++)
        {
            sdp.Media.Add(new RtcMediaDescription
            {
                Kind = "audio",
                Port = 9,
                Protocol = "UDP/TLS/RTP/SAVPF",
                Mid = (midIndex++).ToString()
            });
        }

        for (var i = 0; i < videoSenders; i++)
        {
            sdp.Media.Add(new RtcMediaDescription
            {
                Kind = "video",
                Port = 9,
                Protocol = "UDP/TLS/RTP/SAVPF",
                Mid = (midIndex++).ToString()
            });
        }

        await Ice.StartGatheringAsync().ConfigureAwait(false);
        sdp.Candidates.AddRange(Ice.LocalCandidates);

        return sdp.ToSdp();
    }

    public async Task<string> CreateAnswerAsync(bool hasDataChannels, int audioSenders, int videoSenders)
    {
        // An answer must retain the offered data m-line even when this peer
        // has not created a local channel of its own.
        hasDataChannels |= RemoteDescription?.Media.Any(media => media.Kind == "application") == true;
        var sdp = new RtcSdp
        {
            Type = "answer",
            IceUfrag = Ice.LocalUfrag,
            IcePassword = Ice.LocalPassword,
            FingerprintValue = Dtls.Fingerprint,
            Setup = RemoteDescription?.TransportSetup == "active" ? "passive" : "active"
        };

        if (hasDataChannels)
        {
            sdp.Media.Add(new RtcMediaDescription
            {
                Kind = "application",
                Port = 9,
                Protocol = "UDP/DTLS/SCTP",
                Mid = "0",
                SctpPort = 5000,
                MaximumMessageSize = SctpDataChannel.MaxMessageBytes
            });
        }

        var midIndex = hasDataChannels ? 1 : 0;
        for (var i = 0; i < audioSenders; i++)
        {
            sdp.Media.Add(new RtcMediaDescription
            {
                Kind = "audio",
                Port = 9,
                Protocol = "UDP/TLS/RTP/SAVPF",
                Mid = (midIndex++).ToString()
            });
        }

        for (var i = 0; i < videoSenders; i++)
        {
            sdp.Media.Add(new RtcMediaDescription
            {
                Kind = "video",
                Port = 9,
                Protocol = "UDP/TLS/RTP/SAVPF",
                Mid = (midIndex++).ToString()
            });
        }

        await Ice.StartGatheringAsync().ConfigureAwait(false);
        sdp.Candidates.AddRange(Ice.LocalCandidates);

        return sdp.ToSdp();
    }

    public void SetLocalDescription(string type, string sdp)
    {
        lock (_gate)
        {
            LocalDescription = RtcSdp.Parse(sdp);
            LocalDescription.Type = type;

            if (type == "offer")
            {
                Ice.IsControlling = true;
                SignalingState = "have-local-offer";
                OnSignalingStateChange?.Invoke(SignalingState);
            }
            else if (type == "answer")
            {
                Dtls.IsClient = LocalDescription.TransportSetup switch
                {
                    "active" => true,
                    "passive" => false,
                    // Empty descriptions are retained only by the old in-page
                    // loopback fixture; they contain no remote credentials or candidates.
                    _ when string.IsNullOrWhiteSpace(sdp) => Dtls.IsClient,
                    _ => throw new InvalidOperationException("A DTLS answer must select active or passive setup.")
                };
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
            RemoteDescription = RtcSdp.Parse(sdp);
            RemoteDescription.Type = type;
            Sctp.ConfigureRemote(RemoteDescription.RemoteMaximumMessageSize, RemoteDescription.RemoteSctpPort);

            if (type == "offer")
            {
                Ice.IsControlling = false;
                SignalingState = "have-remote-offer";
                OnSignalingStateChange?.Invoke(SignalingState);
                // Our generated answer selects active, so we initiate DTLS.
                Dtls.IsClient = RemoteDescription.TransportSetup switch
                {
                    "actpass" or "passive" => true,
                    "active" => false,
                    _ => throw new InvalidOperationException("Unsupported remote DTLS setup.")
                };
            }
            else if (type == "answer")
            {
                SignalingState = "stable";
                OnSignalingStateChange?.Invoke(SignalingState);
                Dtls.IsClient = RemoteDescription.TransportSetup switch
                {
                    "active" => false,
                    "passive" => true,
                    _ when string.IsNullOrWhiteSpace(sdp) => Dtls.IsClient,
                    _ => throw new InvalidOperationException("A remote DTLS answer must select active or passive setup.")
                };
            }

            if (!string.IsNullOrEmpty(RemoteDescription.TransportIceUfrag))
                Ice.RemoteUfrag = RemoteDescription.TransportIceUfrag;
            if (!string.IsNullOrEmpty(RemoteDescription.TransportIcePassword))
                Ice.RemotePassword = RemoteDescription.TransportIcePassword;
            if (!string.IsNullOrEmpty(RemoteDescription.TransportFingerprint))
                Dtls.RemoteFingerprint = RemoteDescription.TransportFingerprint;

            foreach (var candidate in RemoteDescription.TransportCandidates)
            {
                Ice.AddRemoteCandidate(candidate);
            }
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
        Srtp?.Dispose();
        OnConnectionStateChange?.Invoke("closed");
    }
}
