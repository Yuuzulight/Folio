using Folio.Scripting.Rtc;

namespace Folio.Scripting.Tests;

public sealed class RtcSdpTransportTests
{
    [Fact]
    public void RejectedDescriptionsPreserveTheCommittedTransport()
    {
        using var peer = new RtcPeerConnectionController();
        const string offer = "v=0\na=setup:actpass\nm=application 9 UDP/DTLS/SCTP webrtc-datachannel\na=max-message-size:1234\n";
        peer.SetRemoteDescription("offer", offer);
        var remote = peer.RemoteDescription;
        var notifications = 0;
        peer.OnSignalingStateChange += _ => notifications++;
        Assert.Throws<InvalidOperationException>(() => peer.SetRemoteDescription("answer", offer.Replace("1234", "9999")));
        Assert.Same(remote, peer.RemoteDescription);
        Assert.Equal("have-remote-offer", peer.SignalingState);
        Assert.Equal(1234, peer.Sctp.MaximumSendMessageBytes);
        Assert.False(peer.Ice.IsControlling);
        Assert.True(peer.Dtls.IsClient);
        Assert.Throws<InvalidOperationException>(() => peer.SetLocalDescription("answer", offer));
        Assert.Null(peer.LocalDescription);
        Assert.Equal(0, notifications);
    }

    [Theory]
    [InlineData("", 65536)]
    [InlineData("a=max-message-size:0\n", 262144)]
    [InlineData("a=max-message-size:1234\n", 1234)]
    [InlineData("a=max-message-size:18446744073709551615\n", 262144)]
    public void AppliesTheRemoteMessageLimitAndSctpPort(string attribute, int expected)
    {
        using var peer = new RtcPeerConnectionController();
        peer.SetRemoteDescription("offer", "v=0\na=setup:actpass\nm=application 9 UDP/DTLS/SCTP webrtc-datachannel\na=sctp-port:6001\n" + attribute);
        Assert.Equal(expected, peer.Sctp.MaximumSendMessageBytes);
        using var sctp = new SctpDataChannel(); sctp.ConfigureRemote(peer.RemoteDescription!.RemoteMaximumMessageSize, 6001);
        byte[]? packet = null; sctp.OnPacketToSend += value => packet = value; sctp.StartAssociation(true);
        Assert.NotNull(packet);
        Assert.Equal(5000, System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(packet));
        Assert.Equal(6001, System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(2)));
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("+1")]
    [InlineData("1.5")]
    [InlineData("invalid")]
    [InlineData("1\na=max-message-size:2")]
    public void RefusesMalformedOrDuplicateMessageLimits(string value)
        => Assert.Throws<InvalidDataException>(() => RtcSdp.Parse("m=application 9 UDP/DTLS/SCTP webrtc-datachannel\na=max-message-size:" + value));

    [Fact]
    public void MediaLevelTransportOverridesSessionDefaultsAndRoundTrips()
    {
        var sdp = RtcSdp.Parse("""
            v=0
            a=group:BUNDLE data
            a=ice-ufrag:session
            a=ice-pwd:session-password
            a=setup:actpass
            a=fingerprint:sha-256 SESSION
            m=application 9 UDP/DTLS/SCTP webrtc-datachannel
            a=mid:data
            a=ice-ufrag:media
            a=ice-pwd:media-password
            a=setup:active
            a=fingerprint:sha-256 MEDIA
            a=candidate:1 1 udp 123 127.0.0.1 5000 typ host
            """);
        Assert.Equal("active", sdp.TransportSetup);
        Assert.Equal("media", sdp.TransportIceUfrag);
        Assert.Equal("media-password", sdp.TransportIcePassword);
        Assert.Equal("MEDIA", sdp.TransportFingerprint);
        Assert.Single(sdp.TransportCandidates);
        var roundTrip = RtcSdp.Parse(sdp.ToSdp());
        Assert.Equal("active", roundTrip.TransportSetup);
        Assert.Equal("MEDIA", roundTrip.TransportFingerprint);
        using var peer = new RtcPeerConnectionController();
        peer.SetRemoteDescription("answer", sdp.ToSdp());
        Assert.False(peer.Dtls.IsClient);
        Assert.Equal("media", peer.Ice.RemoteUfrag);
        Assert.Equal("media-password", peer.Ice.RemotePassword);
        Assert.Equal("MEDIA", peer.Dtls.RemoteFingerprint);
    }

    [Fact]
    public void BundleTagChoosesTransportRatherThanLastOrFirstMediaSection()
    {
        var sdp = RtcSdp.Parse("""
            v=0
            a=group:BUNDLE data audio
            m=audio 9 UDP/TLS/RTP/SAVPF 96
            a=mid:audio
            a=ice-ufrag:wrong-transport
            a=setup:passive
            m=application 9 UDP/DTLS/SCTP webrtc-datachannel
            a=mid:data
            a=ice-ufrag:selected-transport
            a=setup:active
            """);
        Assert.Equal("selected-transport", sdp.TransportIceUfrag);
        Assert.Equal("active", sdp.TransportSetup);
    }

    [Fact]
    public void SeparateUnimplementedTransportsAreRefusedRatherThanSilentlyMixed()
    {
        var sdp = RtcSdp.Parse("m=audio 9 UDP/TLS/RTP/SAVPF 96\nm=application 9 UDP/DTLS/SCTP webrtc-datachannel");
        Assert.Throws<NotSupportedException>(() => sdp.TransportMedia());
    }
}
