using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Folio.Resources;
using Folio.Scripting.Ipc;
using Folio.Scripting.Rtc;

namespace Folio.Scripting.Tests;

[Collection("Content process round trips")]
public sealed class ContentHostPolicyTests
{
    private static readonly FeatureCapabilities Grants = new() { Iframes = true, WebRtc = true };
    private const string IceScript = "function probe(url){try{var p=new RTCPeerConnection({iceServers:[{urls:url}]});p.close();return 'allowed';}catch(e){return e.name;}} probe('stun:allowed.example')+'|'+probe('stun:blocked.example')";

    public static bool HasPublishedWindowsHost => OperatingSystem.IsWindows() &&
        Environment.GetEnvironmentVariable("FOLIO_CONTENT_HOST") is { } path &&
        string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase);

    [Fact]
    public async Task DiagramStandInRequiresTrustedHostConfiguration()
    {
        const string url = "https://example.com/mermaid.js";
        const string html = "<div class='mermaid'>sequenceDiagram\nAlice->>Bob: Hello</div>" +
            "<script src='https://example.com/mermaid.js'></script><script>if(typeof mermaid!=='undefined')mermaid.run();</script>";
        const string script = "document.querySelector('svg.folio-diagram') !== null";
        await using var trusted = Start(new FolioOptions { DiagramLibraryUrls = new HashSet<string> { url } });
        Assert.Equal("true", Value(await Exchange(trusted, new { html, script })));

        await using var untrusted = Start(new FolioOptions());
        Assert.Equal("false", Value(await Exchange(untrusted, new
        {
            html, script, hostPolicy = new { diagramLibraryUrls = new[] { url } }
        })));
    }

    [Fact(Skip = "Requires the published Windows content host.", SkipUnless = nameof(HasPublishedWindowsHost), Timeout = 60000)]
    public async Task PublishedHostExchangesRtcDataWithAnIndependentLocalPeer()
    {
        await using var client = Start(new FolioOptions());
        WorkerIsolationCapabilityMatrixTests.AssertRestricted(client.Process);
        await ExchangeRtcDataThroughTheHostBroker(client);
    }

    [Fact(Skip = "Requires the published Windows content host.", SkipUnless = nameof(HasPublishedWindowsHost), Timeout = 60000)]
    public async Task PublishedHostIframeWithFeaturePolicyExchangesRtcDataWithAnIndependentLocalPeer()
    {
        _ = TestContext.Current.CancellationToken;
        await using var client = Start(new FolioOptions());
        WorkerIsolationCapabilityMatrixTests.AssertRestricted(client.Process);
        await ExchangeIframeRtcDataThroughTheHostBroker(client);
    }

    // The same exchange against whichever host the run selected, so it also covers the developer build in seconds.
    [Fact(Timeout = 60000)]
    public async Task ChildExchangesRtcDataWithALocalPeerThroughTheHostUdpBroker()
    {
        await using var client = Start(new FolioOptions());
        await ExchangeRtcDataThroughTheHostBroker(client);
    }

    [Fact(Timeout = 60000)]
    public async Task IframeWithFeaturePolicyExchangesRtcDataWithALocalPeerThroughTheHostUdpBroker()
    {
        _ = TestContext.Current.CancellationToken;
        await using var client = Start(new FolioOptions());
        await ExchangeIframeRtcDataThroughTheHostBroker(client);
    }

    // Remote audio end to end: a peer outside the sandbox sends G.711 over SRTP; the child (no socket) receives it through the
    // host UDP broker, surfaces ontrack, and its SRTCP receiver report comes back through the same broker.
    [Fact(Timeout = 60000)]
    public async Task ChildReceivesRemoteAudioOverSrtpThroughTheHostUdpBroker()
    {
        await using var client = Start(new FolioOptions());
        client.UdpOptions = new UdpBrokerOptions { AllowLoopback = true };
        using var remote = new RtcPeerConnectionController();
        await Exchange(client, new
        {
            html = "<body></body>",
            capabilities = new FeatureCapabilities { WebRtc = true },
            script = """
                var offer='', error='', tracks=[];
                var pc=new RTCPeerConnection();
                pc.ontrack=function(event){tracks.push(event.track.kind+':'+event.streams.length);};
                pc.addTransceiver('audio',{direction:'recvonly'});
                pc.createOffer().then(function(description){
                    return pc.setLocalDescription(description).then(function(){offer=description.sdp;});
                }).catch(function(e){error=String(e);});
                'started'
                """
        }, "started");
        var description = Value(await Exchange(client, new { script = "error?'ERROR:'+error:offer" }));
        Assert.StartsWith("v=0", description);
        Assert.Contains("m=audio 9 UDP/TLS/RTP/SAVPF 111 0 8", description);
        Assert.Contains("a=recvonly", description);
        remote.SetRemoteDescription("offer", description!);
        var answer = await remote.CreateAnswerAsync(false, 1, 0);
        Assert.Contains("a=sendonly", answer);
        remote.SetLocalDescription("answer", answer);
        await Exchange(client, new
        {
            script = "pc.setRemoteDescription({type:'answer',sdp:" + JsonSerializer.Serialize(answer) + "}).catch(function(e){error=String(e);});'queued'"
        }, "queued");

        var cancellation = TestContext.Current.CancellationToken;
        var sender = remote.LocalAudioTrack(0)!;
        var tone = Enumerable.Range(0, 160).Select(i => (float)(0.4 * Math.Sin(2 * Math.PI * 440 * i / 8000))).ToArray();
        var watch = Stopwatch.StartNew();
        RtcpPacket? report = null;
        while (report is null && watch.Elapsed < TimeSpan.FromSeconds(20))
        {
            if (remote.Dtls.State == "connected") remote.Media.SendAudio(sender, tone, 8000, 1);
            report = remote.Media.LastReports.FirstOrDefault(r => r.Kind == RtcpKind.ReceiverReport && r.Blocks.Count > 0);
            await Task.Delay(20, cancellation);
        }
        Assert.NotNull(report);
        var block = report.Blocks[0];
        Assert.Equal(sender.Ssrc, block.Ssrc);          // the child reported on the SSRC it unprotected
        // A report block only exists for a stream the child has received packets on, so this proves reception without depending on the random sequence base.
        Assert.Equal("audio:1", Value(await Exchange(client, new { script = "error?'ERROR:'+error:tracks.join(',')" })));
    }

    // Remote video end to end: VP8 over SRTP through the host broker into the sandboxed child, onto a <video srcObject>,
    // and from the video into a canvas with drawImage.
    [Fact(Timeout = 90000)]
    public async Task ChildRendersRemoteVp8VideoInAVideoElementAndCanvas()
    {
        var frames = Vp8DecoderTests.IvfFrames(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Vp8Vectors", "vp80-00-comprehensive-001.ivf")), out _, out _);
        using var reference = new Folio.Scripting.Rtc.Vp8.Vp8Decoder(1 << 22);
        byte[]? expected = null;
        foreach (var frame in frames) { reference.Decode(frame); expected = reference.ToRgba(); }
        const int x = 88, y = 72;
        var offsetPixel = (y * 176 + x) * 4;
        var want = $"{expected![offsetPixel]},{expected[offsetPixel + 1]},{expected[offsetPixel + 2]}";

        await using var client = Start(new FolioOptions());
        client.UdpOptions = new UdpBrokerOptions { AllowLoopback = true };
        using var remote = new RtcPeerConnectionController();
        await Exchange(client, new
        {
            html = "<body></body>",
            capabilities = new FeatureCapabilities { WebRtc = true, Media = true },
            script = """
                var offer='', error='', info='', video=document.createElement('video'), stream=null;
                var pc=new RTCPeerConnection();
                pc.ontrack=function(event){ stream=event.streams[0]; video.srcObject=stream; info=event.track.kind; };
                pc.addTransceiver('video',{direction:'recvonly'});
                pc.createOffer().then(function(d){return pc.setLocalDescription(d).then(function(){offer=d.sdp;});}).catch(function(e){error=String(e);});
                var sendError='';
                try { pc.addTrack(new MediaStreamTrack('video')); sendError='none'; } catch(e) { sendError=e.name; }
                'started'
                """
        }, "started");
        var description = Value(await Exchange(client, new { script = "error?'ERROR:'+error:offer" }));
        Assert.Contains("m=video 9 UDP/TLS/RTP/SAVPF 96", description);
        Assert.Contains("a=rtcp-fb:96 nack pli", description);
        remote.SetRemoteDescription("offer", description!);
        var answer = await remote.CreateAnswerAsync(false, 0, 0);
        Assert.Contains("a=inactive", answer); // Folio cannot send video
        answer = answer.Replace("a=inactive", "a=sendonly"); // this test injects the RTP a real sender would produce
        remote.SetLocalDescription("answer", answer);
        await Exchange(client, new
        {
            script = "pc.setRemoteDescription({type:'answer',sdp:" + JsonSerializer.Serialize(answer) + "}).catch(function(e){error=String(e);});'queued'"
        }, "queued");

        var cancellation = TestContext.Current.CancellationToken;
        var watch = Stopwatch.StartNew();
        while (remote.Dtls.State != "connected" && watch.Elapsed < TimeSpan.FromSeconds(20)) await Task.Delay(20, cancellation);
        Assert.Equal("connected", remote.Dtls.State);
        ushort sequence = 1; uint timestamp = 1000;
        var ssrc = 0xABCDu;
        string? pixel = null;
        const string probe = "(function(){ if(error) return 'ERROR:'+error; if(!video.videoWidth) return 'none'; var c=document.createElement('canvas'); c.width=176; c.height=144; var g=c.getContext('2d'); g.drawImage(video,0,0); var d=g.getImageData(" + "88,72" + ",1,1).data; return video.videoWidth+'x'+video.videoHeight+':'+d[0]+','+d[1]+','+d[2]; })()";
        for (var round = 0; pixel is null && round < 40; round++)
        {
            foreach (var frame in frames)
            {
                for (var offset = 0; offset < frame.Length; offset += 1000)
                {
                    var length = Math.Min(1000, frame.Length - offset);
                    var rtp = new byte[13 + length];
                    rtp[0] = 0x80; rtp[1] = (byte)(96 | (offset + length == frame.Length ? 0x80 : 0));
                    System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(rtp.AsSpan(2), sequence++);
                    System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(rtp.AsSpan(4), timestamp);
                    System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(rtp.AsSpan(8), ssrc);
                    rtp[12] = (byte)(offset == 0 ? 0x10 : 0);
                    frame.AsSpan(offset, length).CopyTo(rtp.AsSpan(13));
                    remote.Ice.SendDatagram(remote.Srtp!.ProtectRtp(rtp));
                }
                timestamp += 3000;
                await Task.Delay(5, cancellation);
            }
            var seen = Value(await Exchange(client, new { script = probe }));
            if (seen is not null && seen.StartsWith("176x144:")) pixel = seen;
            else Assert.False(seen is not null && seen.StartsWith("ERROR"), seen);
            if (watch.Elapsed > TimeSpan.FromSeconds(60)) break;
        }
        Assert.Equal("176x144:" + want, pixel);
        Assert.Equal("video", Value(await Exchange(client, new { script = "info" })));
        Assert.Equal("NotSupportedError", Value(await Exchange(client, new { script = "sendError" })));
    }

    // The same path for H.264: the child offers VP8 and H.264, the remote peer is left with H.264 only, so the answer picks it, and
    // Constrained Baseline pictures (x264 fixture) travel as RTP through the host broker onto a <video srcObject> and a canvas.
    [Fact(Timeout = 90000)]
    public async Task ChildRendersRemoteH264VideoInAVideoElementAndCanvas()
    {
        var units = H264DecoderTests.AccessUnits(H264Fixtures.QcifLife());
        var reference = new H264Decoder();
        DecodedVideoFrame? last = null;
        foreach (var unit in units) foreach (var frame in reference.Decode(H264DecoderTests.AnnexB(unit))) last = frame;
        var expected = H264Receiver.ToRgba(last!);
        const int x = 64, y = 48;
        var offsetPixel = (y * 128 + x) * 4;
        var want = $"{expected[offsetPixel]},{expected[offsetPixel + 1]},{expected[offsetPixel + 2]}";

        await using var client = Start(new FolioOptions());
        client.UdpOptions = new UdpBrokerOptions { AllowLoopback = true };
        using var remote = new RtcPeerConnectionController();
        await Exchange(client, new
        {
            html = "<body></body>",
            capabilities = new FeatureCapabilities { WebRtc = true, Media = true },
            script = """
                var offer='', error='', info='', video=document.createElement('video'), stream=null;
                var pc=new RTCPeerConnection();
                pc.ontrack=function(event){ stream=event.streams[0]; video.srcObject=stream; info=event.track.kind; };
                pc.addTransceiver('video',{direction:'recvonly'});
                pc.createOffer().then(function(d){return pc.setLocalDescription(d).then(function(){offer=d.sdp;});}).catch(function(e){error=String(e);});
                'started'
                """
        }, "started");
        var description = Value(await Exchange(client, new { script = "error?'ERROR:'+error:offer" }));
        Assert.Contains("m=video 9 UDP/TLS/RTP/SAVPF 96 97 98", description);
        Assert.Contains("packetization-mode=1;profile-level-id=42e01f", description);
        // A peer that only has H.264: take VP8 out of the offer.
        var h264Only = string.Join("\r\n", description!.Split("\r\n").Where(line => !line.StartsWith("a=rtpmap:96 ") && !line.StartsWith("a=rtcp-fb:96 ") && !line.StartsWith("a=fmtp:96 ")))
            .Replace("SAVPF 96 97 98", "SAVPF 97 98");
        remote.SetRemoteDescription("offer", h264Only);
        var answer = await remote.CreateAnswerAsync(false, 0, 0);
        Assert.Contains("m=video 9 UDP/TLS/RTP/SAVPF 97", answer);
        Assert.Contains("a=rtpmap:97 H264/90000", answer);
        Assert.DoesNotContain("VP8", answer);
        Assert.Contains("a=inactive", answer); // Folio cannot send video
        answer = answer.Replace("a=inactive", "a=sendonly"); // this test injects the RTP a real sender would produce
        remote.SetLocalDescription("answer", answer);
        await Exchange(client, new
        {
            script = "pc.setRemoteDescription({type:'answer',sdp:" + JsonSerializer.Serialize(answer) + "}).catch(function(e){error=String(e);});'queued'"
        }, "queued");

        var cancellation = TestContext.Current.CancellationToken;
        var watch = Stopwatch.StartNew();
        while (remote.Dtls.State != "connected" && watch.Elapsed < TimeSpan.FromSeconds(20)) await Task.Delay(20, cancellation);
        Assert.Equal("connected", remote.Dtls.State);
        ushort sequence = 1; uint timestamp = 1000;
        var ssrc = 0xBEEFu;
        string? pixel = null;
        const string probe = "(function(){ if(error) return 'ERROR:'+error; if(!video.videoWidth) return 'none'; var c=document.createElement('canvas'); c.width=128; c.height=96; var g=c.getContext('2d'); g.drawImage(video,0,0); var d=g.getImageData(64,48,1,1).data; return video.videoWidth+'x'+video.videoHeight+':'+d[0]+','+d[1]+','+d[2]; })()";
        for (var round = 0; pixel is null && round < 40; round++)
        {
            foreach (var unit in units)
            {
                // Single NAL unit packets, and FU-A for the NAL units above 1000 bytes (RFC 6184 packetization mode 1).
                var payloads = new List<byte[]>();
                foreach (var nal in unit)
                {
                    if (nal.Length <= 1000) { payloads.Add(nal); continue; }
                    var body = nal.AsSpan(1);
                    for (var offset = 0; offset < body.Length; offset += 998)
                    {
                        var length = Math.Min(998, body.Length - offset);
                        var fragment = new byte[2 + length];
                        fragment[0] = (byte)((nal[0] & 0xE0) | 28);
                        fragment[1] = (byte)((offset == 0 ? 0x80 : 0) | (offset + length == body.Length ? 0x40 : 0) | (nal[0] & 0x1F));
                        body.Slice(offset, length).CopyTo(fragment.AsSpan(2));
                        payloads.Add(fragment);
                    }
                }
                for (var i = 0; i < payloads.Count; i++)
                {
                    var rtp = new byte[12 + payloads[i].Length];
                    rtp[0] = 0x80; rtp[1] = (byte)(97 | (i == payloads.Count - 1 ? 0x80 : 0));
                    System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(rtp.AsSpan(2), sequence++);
                    System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(rtp.AsSpan(4), timestamp);
                    System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(rtp.AsSpan(8), ssrc);
                    payloads[i].CopyTo(rtp, 12);
                    remote.Ice.SendDatagram(remote.Srtp!.ProtectRtp(rtp));
                }
                timestamp += 3000;
                await Task.Delay(5, cancellation);
            }
            await Task.Delay(50, cancellation);
            var seen = Value(await Exchange(client, new { script = probe }));
            if (seen == "128x96:" + want) pixel = seen;
            else Assert.False(seen is not null && seen.StartsWith("ERROR"), seen);
            if (watch.Elapsed > TimeSpan.FromSeconds(60)) break;
        }
        Assert.Equal("128x96:" + want, pixel);
        Assert.Equal("video", Value(await Exchange(client, new { script = "info" })));
    }

    [Fact(Timeout = 60000)]
    public async Task HostBrokerDeniesLoopbackPeersByDefaultSoNothingConnects()
    {
        await using var client = Start(new FolioOptions()); // production destination policy
        using var remote = new RtcPeerConnectionController();
        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        remote.OnDataChannel += channel => channel.OnMessage += payload => received.TrySetResult(payload as string ?? "");
        await Exchange(client, new
        {
            html = "<body></body>",
            capabilities = new FeatureCapabilities { WebRtc = true },
            script = """
                var offer='', error='';
                var pc=new RTCPeerConnection(), channel=pc.createDataChannel('denied');
                channel.onopen=function(){channel.send('folio');};
                pc.createOffer().then(function(d){return pc.setLocalDescription(d).then(function(){offer=d.sdp;});})
                    .catch(function(e){error=String(e);});
                'started'
                """
        }, "started");
        var description = Value(await Exchange(client, new { script = "error?'ERROR:'+error:offer" }));
        Assert.StartsWith("v=0", description);
        remote.SetRemoteDescription("offer", description!);
        var answer = await remote.CreateAnswerAsync(true, 0, 0);
        remote.SetLocalDescription("answer", answer);
        await Exchange(client, new
        {
            script = "pc.setRemoteDescription({type:'answer',sdp:" + JsonSerializer.Serialize(answer) + "}).catch(function(e){error=String(e);});'queued'"
        }, "queued");
        var cancellation = TestContext.Current.CancellationToken;
        var watch = Stopwatch.StartNew();
        while (client.UdpDenied == 0 && watch.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(25, cancellation);
        Assert.True(client.UdpDenied > 0, "The loopback peer's datagrams should have been refused by the host.");
        Assert.False(received.Task.IsCompleted);
        Assert.NotEqual("connected", remote.Dtls.State);
    }

    private static async Task ExchangeRtcDataThroughTheHostBroker(ContentProcessClient client)
    {
        client.UdpOptions = new UdpBrokerOptions { AllowLoopback = true }; // the test peer lives on loopback
        using var remote = new RtcPeerConnectionController();
        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        remote.OnDataChannel += channel => channel.OnMessage += payload =>
        {
            if (payload is not string text) return;
            channel.Send("reply:" + text);
            received.TrySetResult(text);
        };

        await Exchange(client, new
        {
            html = "<body></body>",
            capabilities = new FeatureCapabilities { WebRtc = true },
            script = """
                var offer='', error='', reply='';
                var pc=new RTCPeerConnection(), channel=pc.createDataChannel('published-host');
                channel.onopen=function(){channel.send('folio');};
                channel.onmessage=function(event){reply=event.data;};
                pc.createOffer().then(function(description){
                    return pc.setLocalDescription(description).then(function(){offer=description.sdp;});
                }).catch(function(e){error=String(e);});
                'started'
                """
        }, "started");
        var description = Value(await Exchange(client, new { script = "error?'ERROR:'+error:offer" }));
        Assert.NotNull(description);
        Assert.StartsWith("v=0", description);
        Assert.NotEmpty(RtcSdp.Parse(description).TransportCandidates);
        remote.SetRemoteDescription("offer", description);
        var answer = await remote.CreateAnswerAsync(true, 0, 0);
        remote.SetLocalDescription("answer", answer);
        await Exchange(client, new
        {
            script = "pc.setRemoteDescription({type:'answer',sdp:" + JsonSerializer.Serialize(answer) +
                "}).catch(function(e){error=String(e);});'answer queued'"
        }, "answer queued");

        var cancellation = TestContext.Current.CancellationToken;
        Assert.Equal("folio", await received.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellation));
        var watch = Stopwatch.StartNew();
        string? state;
        do
        {
            state = Value(await Exchange(client, new { script = "error?'ERROR:'+error:reply" }));
            if (!string.IsNullOrEmpty(state)) break;
            await Task.Delay(25, cancellation);
        } while (watch.Elapsed < TimeSpan.FromSeconds(10));
        Assert.Equal("reply:folio", state);
        Assert.Equal("connected", remote.Dtls.State);

        await client.UpdateCapabilitiesAsync(FeatureCapabilities.Default, cancellation);
        var revoked = await Exchange(client, new
        {
            script = """
                (function(){
                    var result='sent';
                    try { channel.send('after-revoke'); } catch(e) { result=e.name; }
                    return [pc.connectionState, channel.readyState, result].join(':');
                })()
                """
        });
        Assert.Equal("closed:closed:InvalidStateError", Value(revoked));
        Assert.Equal(0, client.UdpOpenSockets); // revocation closed the host-owned socket too
    }

    private static async Task ExchangeIframeRtcDataThroughTheHostBroker(ContentProcessClient client)
    {
        client.UdpOptions = new UdpBrokerOptions { AllowLoopback = true };
        using var remote = new RtcPeerConnectionController();
        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        remote.OnDataChannel += channel => channel.OnMessage += payload =>
        {
            if (payload is not string text) return;
            channel.Send("reply:" + text);
            received.TrySetResult(text);
        };

        await Exchange(client, new
        {
            html = "<body><iframe id='f' allow='webrtc'></iframe></body>",
            capabilities = new FeatureCapabilities { Iframes = true, WebRtc = true },
            script = """
                var frame = document.getElementById('f');
                frame.srcdoc = "<script>" +
                    "var offer='', error='', reply='';" +
                    "var pc=new RTCPeerConnection(), channel=pc.createDataChannel('iframe-host');" +
                    "channel.onopen=function(){channel.send('iframe-folio');};" +
                    "channel.onmessage=function(event){reply=event.data; document.body.setAttribute('data-reply', event.data);};" +
                    "pc.createOffer().then(function(description){" +
                        "return pc.setLocalDescription(description).then(function(){offer=description.sdp; document.body.setAttribute('data-offer', offer);});" +
                    "}).catch(function(e){error=String(e); document.body.setAttribute('data-error', error);});" +
                    "onmessage=function(e){" +
                        "if(e.data && e.data.type==='answer'){" +
                            "pc.setRemoteDescription(e.data).catch(function(err){document.body.setAttribute('data-error', String(err));});" +
                        "}" +
                    "};" +
                    "</" + "script>";
                frame.contentDocument;
                'started'
                """
        }, "started");

        var cancellation = TestContext.Current.CancellationToken;
        var watch = Stopwatch.StartNew();
        string? description = null;
        do
        {
            description = Value(await Exchange(client, new
            {
                script = """
                    (function(){
                        var f = document.getElementById('f');
                        if (!f || !f.contentDocument || !f.contentDocument.body) return '';
                        var err = f.contentDocument.body.getAttribute('data-error');
                        if (err) return 'ERROR:' + err;
                        return f.contentDocument.body.getAttribute('data-offer') || '';
                    })()
                    """
            }));
            if (!string.IsNullOrEmpty(description)) break;
            await Task.Delay(25, cancellation);
        } while (watch.Elapsed < TimeSpan.FromSeconds(10));

        Assert.NotNull(description);
        Assert.StartsWith("v=0", description);
        Assert.NotEmpty(RtcSdp.Parse(description).TransportCandidates);
        remote.SetRemoteDescription("offer", description);
        var answer = await remote.CreateAnswerAsync(true, 0, 0);
        remote.SetLocalDescription("answer", answer);

        await Exchange(client, new
        {
            script = "document.getElementById('f').contentWindow.postMessage({type:'answer',sdp:" + JsonSerializer.Serialize(answer) + "}, '*'); 'answer queued'"
        }, "answer queued");

        Assert.Equal("iframe-folio", await received.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellation));
        watch.Restart();
        string? state;
        do
        {
            state = Value(await Exchange(client, new
            {
                script = """
                    (function(){
                        var f = document.getElementById('f');
                        if (!f || !f.contentDocument || !f.contentDocument.body) return '';
                        var err = f.contentDocument.body.getAttribute('data-error');
                        if (err) return 'ERROR:' + err;
                        return f.contentDocument.body.getAttribute('data-reply') || '';
                    })()
                    """
            }));
            if (!string.IsNullOrEmpty(state)) break;
            await Task.Delay(25, cancellation);
        } while (watch.Elapsed < TimeSpan.FromSeconds(10));

        Assert.Equal("reply:iframe-folio", state);
        Assert.Equal("connected", remote.Dtls.State);

        await client.UpdateCapabilitiesAsync(FeatureCapabilities.Default, cancellation);
        var revoked = await Exchange(client, new
        {
            script = """
                (function(){
                    var f = document.getElementById('f');
                    return (f && f.contentDocument) ? 'present' : 'revoked';
                })()
                """
        });
        Assert.Equal("revoked", Value(revoked));
        Assert.Equal(0, client.UdpOpenSockets);
    }

    [Fact]
    public async Task RealChildUsesSnapshotOfHostOriginsAndIgnoresForgedLoadPolicy()
    {
        var origins = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "STUN:allowed.example/" };
        await using var client = Start(new FolioOptions { WebRtcAllowedOrigins = origins });
        origins.Clear();
        origins.Add("stun:blocked.example");
        var first = await Exchange(client, new
        {
            html = "<body></body>", capabilities = Grants, script = IceScript,
            hostPolicy = new { generation = "forged", webRtcAllowedOrigins = new[] { "stun:blocked.example" } }
        });
        Assert.Equal("allowed|SecurityError", Value(first));
        Assert.Equal("allowed|SecurityError", Value(await Exchange(client, new { script = IceScript })));
        Assert.Equal("allowed|SecurityError", Value(await Exchange(client, new { html = "<body></body>", capabilities = Grants, script = IceScript })));
    }

    [Fact]
    public async Task StrayCredentialSeparatorsInStunEntriesAreRefused()
    {
        await using var client = Start(new FolioOptions { WebRtcAllowedOrigins = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "stun:allowed.example", "turn:turn.example" } });
        var response = await Exchange(client, new
        {
            html = "<body></body>", capabilities = Grants,
            script = "function probe(url){try{var p=new RTCPeerConnection({iceServers:[{urls:url}]});p.close();return 'allowed';}catch(e){return e.name;}} probe('stun:allowed.example\\u0001u\\u0001p')+'|'+probe('stun:allowed.example\\u0001x')+'|'+probe('stun:allowed.example')"
        });
        Assert.Equal("SyntaxError|SyntaxError|allowed", Value(response));
    }

    [Fact]
    public async Task EmptyPolicyCannotBeGrantedByLoadFieldsOrPageGlobals()
    {
        await using var client = Start(new FolioOptions());
        var frame = await Exchange(client, new
        {
            html = "<body></body>", capabilities = Grants,
            webRtcAllowedOrigins = new[] { "stun:allowed.example" },
            hostPolicy = new { webRtcAllowedOrigins = new[] { "stun:allowed.example" } },
            script = "globalThis.webRtcAllowedOrigins=['stun:allowed.example'];" + IceScript
        });
        Assert.Equal("SecurityError|SecurityError", Value(frame));
        Assert.Equal("loopback", Value(await Exchange(client, new { script = "var p=new RTCPeerConnection();p.close();'loopback'" })));
    }

    [Fact]
    public async Task RealIframeNavigationIsResolvedAndMediatedByParentIncludingCustomSchemes()
    {
        var navigations = new ConcurrentQueue<Uri>();
        using var delivered = new SemaphoreSlim(0);
        var loader = new Loader();
        var dispatches = 0;
        await using var client = Start(new FolioOptions
        {
            ResourceLoader = loader, IframeNavigationRequested = uri => { navigations.Enqueue(uri); delivered.Release(); }
        }, (deliver, token) =>
        {
            Assert.False(token.IsCancellationRequested);
            dispatches++;
            deliver();
            deliver(); // A dispatcher cannot deliver one notification twice.
            return ValueTask.CompletedTask;
        });
        await Exchange(client, new { html = "<iframe src='child.html'></iframe>", url = "https://host.example/root/", capabilities = Grants, script = "1" });
        Assert.True(await delivered.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken));
        Assert.Equal("https://host.example/root/child.html", Assert.Single(navigations).AbsoluteUri);
        await Exchange(client, new { script = "document.querySelector('iframe').src='folio-resource:child';1" });
        Assert.True(await delivered.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken));
        Assert.Equal("folio-resource:child", navigations.ToArray()[1].AbsoluteUri);
        Assert.Equal(2, dispatches);
        Assert.Equal(2, loader.Uris.Count);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task NavigationCallbackCanSynchronouslyDisposeOrReloadWithoutWaitingOnItself(bool uiDispatch, bool reload)
    {
        using var ui = new NavigationUiThread();
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ContentProcessClient? client = null;
        client = Start(new FolioOptions
        {
            ResourceLoader = new Loader(),
            IframeNavigationRequested = _ =>
            {
                try
                {
                    if (uiDispatch) Assert.Equal(ui.ThreadId, Environment.CurrentManagedThreadId);
                    if (reload)
                    {
                        var frame = Exchange(client!, new { html = "<body>replacement</body>", capabilities = Grants, script = "'replacement'" }, "replacement")
                            .GetAwaiter().GetResult();
                        Assert.Equal("replacement", Value(frame));
                    }
                    else client!.DisposeAsync().AsTask().GetAwaiter().GetResult();
                    completed.TrySetResult();
                }
                catch (Exception error) { completed.TrySetException(error); }
            }
        }, uiDispatch ? ui.Dispatch : null);
        try
        {
            await client.SendAsync(ContentMessageKind.Load, new { html = "<iframe src='https://host.example/child'></iframe>", capabilities = Grants }, TestContext.Current.CancellationToken);
            await completed.Task.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        }
        finally { await client.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken); }
        var worker = (Task)typeof(ContentProcessClient).GetField("_navigationWorker", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(client)!;
        await worker.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeferredUiActionCannotCallBackAfterReloadOrDispose(bool dispose)
    {
        var pending = new TaskCompletionSource<(Action Deliver, CancellationToken Token)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbacks = 0;
        // Deliberately ignore cancellation in the dispatcher. The client's wait
        // must still end, and a UI action retained by the dispatcher must be inert.
        var never = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = Start(new FolioOptions { ResourceLoader = new Loader(), IframeNavigationRequested = _ => callbacks++ },
            (deliver, token) =>
            {
                pending.TrySetResult((deliver, token));
                return new ValueTask(never.Task);
            });
        try
        {
            await client.SendAsync(ContentMessageKind.Load, new { html = "<iframe src='https://host.example/old'></iframe>", capabilities = Grants }, TestContext.Current.CancellationToken);
            var queued = await pending.Task.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
            if (dispose)
                await client.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
            else
                await Exchange(client, new { html = "<body>replacement</body>", capabilities = Grants, script = "'new-document'" }, "new-document");
            Assert.True(queued.Token.IsCancellationRequested);
            queued.Deliver();
            Assert.Equal(0, callbacks);
        }
        finally { never.TrySetResult(); await client.DisposeAsync(); }
    }

    [Fact]
    public async Task IncrementalAndPreCancelledLoadsKeepCurrentNavigationGeneration()
    {
        var callbacks = new ConcurrentQueue<Uri>();
        using var delivered = new SemaphoreSlim(0);
        await using var client = Start(new FolioOptions { ResourceLoader = new Loader(), IframeNavigationRequested = uri => { callbacks.Enqueue(uri); delivered.Release(); } });
        await Exchange(client, new { html = "<iframe src='https://host.example/one'></iframe>", capabilities = Grants, script = "'first'" });
        Assert.True(await delivered.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.SendAsync(ContentMessageKind.Load,
            new { html = "<body>cancelled</body>" }, cancelled.Token).AsTask());
        await Exchange(client, new { script = "document.querySelector('iframe').src='https://host.example/two';'second'" });
        Assert.True(await delivered.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken));
        Assert.Equal(new[] { "https://host.example/one", "https://host.example/two" }, callbacks.Select(uri => uri.AbsoluteUri));
        await client.UpdateCapabilitiesAsync(FeatureCapabilities.Default, TestContext.Current.CancellationToken);
        await Exchange(client, new { script = "document.querySelector('iframe').src='https://host.example/denied';'denied'" });
        Assert.Equal(2, callbacks.Count);
    }

    [Fact]
    public async Task ThrowingNavigationObserverDoesNotBreakResourceBrokerage()
    {
        var callbacks = 0;
        var delivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var client = Start(new FolioOptions
        {
            ResourceLoader = new Loader(), IframeNavigationRequested = _ => { callbacks++; delivered.TrySetResult(); throw new InvalidOperationException("observer failed"); }
        });
        Assert.Equal("alive", Value(await Exchange(client, new
        {
            html = "<iframe src='https://host.example/one'></iframe>", capabilities = Grants, script = "'alive'"
        })));
        await delivered.Task.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        Assert.Equal(1, callbacks);
        Assert.False(client.Supervisor.IsStopped);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingReplacementGrantOrMalformedUpdateCannotAuthorizeChildNotification(bool malformedUpdate)
    {
        var callbacks = 0;
        await using var client = Start(new FolioOptions { IframeNavigationRequested = _ => callbacks++ });
        await Exchange(client, new { html = "<body></body>", capabilities = Grants, script = "1" });
        if (malformedUpdate)
        {
            await client.SendAsync(ContentMessageKind.Capabilities, new { capabilities = new { Iframes = "invalid" } }, TestContext.Current.CancellationToken);
            await Exchange(client, new { script = "1" });
        }
        else await Exchange(client, new { html = "<body>ungranted</body>", script = "1" });
        var authority = (FeatureCapabilities)typeof(ContentProcessClient).GetField("_hostCapabilities", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(client)!;
        Assert.False(authority.Iframes);
        await DispatchNotification(client, JsonSerializer.Serialize(new { generation = Generation(client), uri = "https://host.example/forged" }));
        Assert.Equal(0, callbacks);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"generation\":12,\"uri\":\"https://host.example/\"}")]
    [InlineData("{\"generation\":\"00000000000000000000000000000000\",\"uri\":false}")]
    [InlineData("{\"generation\":\"00000000000000000000000000000000\",\"uri\":\"javascript:alert(1)\"}")]
    [InlineData("{\"generation\":\"00000000000000000000000000000000\",\"uri\":\"https://user:secret@host.example/\"}")]
    [InlineData("{\"generation\":\"00000000000000000000000000000000\",\"uri\":\"https://host.example/%0a\"}")]
    [InlineData("{\"generation\":\"00000000000000000000000000000000\",\"uri\":\"relative\"}")]
    [InlineData("{\"generation\":\"00000000000000000000000000000000\",\"uri\":\"https://host.example/%zz\"}")]
    [InlineData("{\"generation\":\"00000000000000000000000000000000\",\"uri\":\"https://host.example/\",\"extra\":true}")]
    [InlineData("{\"generation\":\"00000000000000000000000000000000\",\"generation\":\"00000000000000000000000000000000\",\"uri\":\"https://host.example/\"}")]
    [InlineData("{\"generation\":\"00000000000000000000000000000000\",\"uri\":\"https://host.example/stale\"}")]
    public async Task InvalidOrStaleNotificationPayloadIsIgnoredWithoutBreakingLiveChild(string json)
    {
        var callbacks = 0;
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var client = Start(new FolioOptions
        {
            IframeNavigationRequested = uri =>
            {
                if (uri.AbsolutePath == "/barrier") barrier.TrySetResult();
                else Interlocked.Increment(ref callbacks);
            }
        });
        await Exchange(client, new { html = "<body></body>", capabilities = Grants, script = "1" });
        // Exercise the exact untrusted-message entry point, without adding a
        // page-accessible protocol-forging backdoor to the production child.
        if (!json.Contains("/stale", StringComparison.Ordinal)) json = json.Replace("00000000000000000000000000000000", Generation(client), StringComparison.Ordinal);
        await DispatchNotification(client, json);
        await DispatchNotification(client, JsonSerializer.Serialize(new { generation = Generation(client), uri = "https://host.example/barrier" }));
        await barrier.Task.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        Assert.Equal(0, callbacks);
        Assert.Equal("still-live", Value(await Exchange(client, new { script = "'still-live'" })));
    }

    [Fact]
    public async Task SlowCallbackBoundsQueueAndDoesNotBlockTransportReloadOrDisposal()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbacks = 0;
        var client = Start(new FolioOptions
        {
            ResourceLoader = new Loader(),
            IframeNavigationRequested = _ =>
            {
                Interlocked.Increment(ref callbacks);
                entered.TrySetResult();
                release.Task.GetAwaiter().GetResult();
            }
        });
        try
        {
            await Exchange(client, new { html = "<iframe src='https://host.example/first'></iframe>", capabilities = Grants, script = "'loaded'" });
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
            var json = JsonSerializer.Serialize(new { generation = Generation(client), uri = "https://host.example/queued" });
            for (var i = 0; i < 1024; i++) await DispatchNotification(client, json);
            var queue = typeof(ContentProcessClient).GetField("_navigationDeliveries", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(client)!;
            var reader = queue.GetType().GetProperty("Reader")!.GetValue(queue)!;
            Assert.Equal(32, (int)reader.GetType().GetProperty("Count")!.GetValue(reader)!);
            Assert.Equal("replacement", Value(await Exchange(client,
                new { html = "<body>replacement</body>", capabilities = Grants, script = "'replacement'" }, "replacement")));
            await client.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
            Assert.False(release.Task.IsCompleted);
            release.TrySetResult();
            var worker = (Task)typeof(ContentProcessClient).GetField("_navigationWorker", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(client)!;
            await worker.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
            Assert.Equal(1, callbacks);
        }
        finally { release.TrySetResult(); await client.DisposeAsync(); }
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{\"webRtcAllowedOrigins\":true}")]
    [InlineData("{\"webRtcAllowedOrigins\":[\"stun:allowed.example\",false]}")]
    [InlineData("{\"webRtcAllowedOrigins\":[\"stun:allowed.example\",\"https://not-an-ice-origin\"]}")]
    [InlineData("{\"webRtcAllowedOrigins\":[],\"webRtcAllowedOrigins\":[\"stun:allowed.example\"]}")]
    public async Task RealRawChildMalformedPolicyFailsClosed(string policy)
    {
        var directory = Path.Combine(Path.GetTempPath(), "folio-policy-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var host = ContentHostTestPaths.Resolve();
        var start = new ProcessStartInfo(Path.GetExtension(host).Equals(".dll", StringComparison.OrdinalIgnoreCase) ? "dotnet" : host)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        if (Path.GetExtension(host).Equals(".dll", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(host);
        start.Environment["FOLIO_CONTENT_FRAME_PATH"] = Path.Combine(directory, "frame.bin");
        using var process = Process.Start(start)!;
        var stderr = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            using var payload = JsonDocument.Parse("{\"html\":\"<body></body>\",\"capabilities\":{\"WebRtc\":true},\"script\":"
                + JsonSerializer.Serialize(IceScript) + ",\"hostPolicy\":" + policy + "}");
            await ContentTransport.WriteAsync(process.StandardInput.BaseStream,
                new(ContentMessageKind.Load, 1, payload.RootElement.Clone()), timeout.Token);
            ContentMessage? response;
            do { response = await ContentTransport.ReadAsync(process.StandardOutput.BaseStream, timeout.Token); }
            while (response is not null && response.Kind is not (ContentMessageKind.Frame or ContentMessageKind.Diagnostic));
            Assert.NotNull(response);
            Assert.Equal(ContentMessageKind.Frame, response.Kind);
            Assert.Equal("SecurityError|SecurityError", Value(response));
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(TestContext.Current.CancellationToken);
            await stderr;
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void PolicyBoundsRejectInvalidEntriesAndExcessiveCountsWithoutTruncatingAuthority()
    {
        Assert.Throws<ArgumentException>(() => ContentHostPolicy.NormalizeOrigins(Enumerable.Repeat("stun:allowed.example", 65)));
        Assert.Throws<ArgumentException>(() => ContentHostPolicy.NormalizeOrigins(new[] { "stun:" + new string('a', 2048) }));
        Assert.Throws<ArgumentException>(() => ContentHostPolicy.NormalizeOrigins(Enumerable.Range(0, 64).Select(i => "stun:" + new string('a', 600) + i)));
        foreach (var invalid in new[] { "", "https://host.example", "turn://user:secret@host.example", "stun:host\n", "stun: host" })
            Assert.Throws<ArgumentException>(() => ContentHostPolicy.NormalizeOrigins(new[] { invalid }));
        Assert.False(ContentHostPolicy.TryNavigationUri("https://host.example/" + new string('a', 8192), out _));
        Assert.True(ContentHostPolicy.TryNavigationUri("file:///C:/artifact/child.html", out _));
        Assert.True(ContentHostPolicy.TryNavigationUri("folio-resource:child", out _));
    }

    private static ContentProcessClient Start(FolioOptions options, Func<Action, CancellationToken, ValueTask>? dispatcher = null) =>
        ContentProcessClient.StartWithOptions(ContentHostTestPaths.Resolve(), options,
            new ContentProcessLimits(HeartbeatTimeout: TimeSpan.FromSeconds(15)), iframeNavigationDispatcher: dispatcher);

    private static string? Value(ContentMessage message) => message.Payload.GetProperty("value").GetString();

    private static string Generation(ContentProcessClient client)
    {
        var document = typeof(ContentProcessClient).GetField("_navigationDocument", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(client)!;
        return (string)document.GetType().GetProperty("Generation", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(document)!;
    }

    private static async ValueTask DispatchNotification(ContentProcessClient client, string json)
    {
        var handler = typeof(ContentProcessClient).GetMethod("ValidateAndDispatchAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        using var payload = JsonDocument.Parse(json);
        await (ValueTask)handler.Invoke(client, new object[]
        {
            new ContentMessage(ContentMessageKind.IframeNavigationRequested, 1, payload.RootElement.Clone()), TestContext.Current.CancellationToken
        })!;
    }

    private static async Task<ContentMessage> Exchange(ContentProcessClient client, object payload, string? expected = null)
    {
        var completion = new TaskCompletionSource<ContentMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        ValueTask Receive(ContentMessage message, CancellationToken _)
        {
            if (message.Kind == ContentMessageKind.Frame && message.Payload.GetProperty("sequence").GetInt64() > 0
                && (expected is null || Value(message) == expected)) completion.TrySetResult(message);
            else if (message.Kind == ContentMessageKind.Diagnostic) completion.TrySetException(new InvalidOperationException(message.Payload.ToString()));
            return ValueTask.CompletedTask;
        }
        client.MessageReceived += Receive;
        try
        {
            await client.SendAsync(ContentMessageKind.Load, payload, TestContext.Current.CancellationToken);
            return await completion.Task.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        }
        finally { client.MessageReceived -= Receive; }
    }

    private sealed class Loader : IResourceLoader
    {
        internal ConcurrentQueue<Uri> Uris { get; } = new();
        public Task<ResourceResponse?> LoadAsync(ResourceRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Uris.Enqueue(new Uri(request.Url));
            return Task.FromResult<ResourceResponse?>(new ResourceResponse(System.Text.Encoding.UTF8.GetBytes("<body>child</body>"), "text/html", null));
        }
    }

    private sealed class NavigationUiThread : IDisposable
    {
        private readonly BlockingCollection<(Action Callback, TaskCompletionSource Completion)> _queue = new();
        private readonly Thread _thread;
        internal int ThreadId => _thread.ManagedThreadId;
        internal NavigationUiThread()
        {
            _thread = new Thread(() =>
            {
                foreach (var (callback, completion) in _queue.GetConsumingEnumerable())
                {
                    try { callback(); completion.TrySetResult(); }
                    catch (Exception error) { completion.TrySetException(error); }
                }
            }) { IsBackground = true, Name = "Folio navigation test UI" };
            _thread.Start();
        }
        internal ValueTask Dispatch(Action callback, CancellationToken token)
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _queue.Add((callback, completion), token);
            return new ValueTask(completion.Task.WaitAsync(token));
        }
        public void Dispose()
        {
            _queue.CompleteAdding();
            if (_thread.Join(TimeSpan.FromSeconds(15))) _queue.Dispose();
        }
    }
}
