using Folio.Scripting.Rtc;

namespace Folio.Scripting.Tests;

public sealed class RtcSdpTransportTests
{
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
