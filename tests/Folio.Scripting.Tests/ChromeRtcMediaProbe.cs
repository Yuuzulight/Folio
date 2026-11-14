using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Folio.Scripting.Rtc;

namespace Folio.Scripting.Tests;

/// <summary>Independent browser media acceptance probe (audio Opus tone and decoded video frames for VP8 and H.264).</summary>
internal static class ChromeRtcMediaProbe
{
    private static float[] Sine(int sampleRate, double durationSeconds, double frequency = 440)
    {
        var count = (int)(sampleRate * durationSeconds);
        var samples = new float[count];
        for (var i = 0; i < count; i++)
            samples[i] = (float)(0.5 * Math.Sin(2 * Math.PI * frequency * i / sampleRate));
        return samples;
    }

    private static bool IsNonBlack(MediaCaptureVideoFrame frame)
    {
        var span = frame.Rgba.Span;
        for (var i = 0; i < span.Length; i += 4)
            if (span[i] > 10 || span[i + 1] > 10 || span[i + 2] > 10) return true;
        return false;
    }

    private static bool ChangesOverTime(IReadOnlyList<MediaCaptureVideoFrame> frames)
    {
        if (frames.Count < 2) return false;
        var first = frames[0].Rgba.Span;
        for (var i = 1; i < frames.Count; i++)
            if (!first.SequenceEqual(frames[i].Rgba.Span)) return true;
        return false;
    }

    internal static int Run(string executable, string codec = "all", bool folioAnswerer = false)
    {
        if (codec.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            var r1 = RunSession(executable, "VP8", folioAnswerer);
            if (r1 != 0) return r1;
            return RunSession(executable, "H264", folioAnswerer);
        }
        var target = codec.Equals("h264", StringComparison.OrdinalIgnoreCase) ? "H264" : "VP8";
        return RunSession(executable, target, folioAnswerer);
    }

    private static int RunSession(string executable, string codec, bool folioAnswerer) =>
        RunAsync(executable, codec, folioAnswerer).GetAwaiter().GetResult();

    private static async Task<int> RunAsync(string executable, string codec, bool folioAnswerer)
    {
        if (!File.Exists(executable)) { Console.Error.WriteLine("Chrome executable not found: " + executable); return 2; }
        using var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start(); var port = ((IPEndPoint)reservation.LocalEndpoint).Port; reservation.Stop();
        var origin = $"http://127.0.0.1:{port}/";
        using var listener = new HttpListener(); listener.Prefixes.Add(origin); listener.Start();
        using var peer = new RtcPeerConnectionController();

        var mediaLog = new List<string>(); var logGate = new object();
        void Log(string message)
        {
            lock (logGate)
                if (mediaLog.Count < 512) mediaLog.Add(DateTime.UtcNow.ToString("o") + " " + message);
        }

        var ontrackAudio = false;
        var ontrackVideo = false;
        var audioFramesReceived = 0;
        var audioIsOpus = false;
        var decodedVideoFrames = new List<MediaCaptureVideoFrame>();
        var videoGate = new object();

        peer.Media.OnRemoteTrack += track =>
        {
            Log("OnRemoteTrack: kind=" + track.Kind + " mid=" + track.Mid);
            if (track.Kind == "audio")
            {
                ontrackAudio = true;
                track.AudioFrame += frame =>
                {
                    Interlocked.Increment(ref audioFramesReceived);
                    if (frame.SampleRate == 48000) audioIsOpus = true;
                };
            }
            else if (track.Kind == "video")
            {
                ontrackVideo = true;
                track.VideoFrame += frame =>
                {
                    lock (videoGate)
                    {
                        if (decodedVideoFrames.Count < 60)
                            decodedVideoFrames.Add(frame);
                    }
                };
            }
        };

        // When Folio offers: 1 audio sender (sendrecv) and 1 video receiver (recvonly).
        // Folio offers VP8 and H.264 (Constrained Baseline); the page's setCodecPreferences / SDP filter selects the tested codec.
        var offer = folioAnswerer ? "" : await peer.CreateOfferAsync(hasDataChannels: false, audioSenders: 1, videoSenders: 0, audioReceivers: 0, videoReceivers: 1);
        if (!folioAnswerer) peer.SetLocalDescription("offer", offer);

        var evidenceRoot = Environment.GetEnvironmentVariable("FOLIO_RTC_PROBE_OUTPUT") ?? Path.GetTempPath();
        var profile = Path.Combine(Path.GetFullPath(evidenceRoot), "folio-rtc-media-chrome-" + codec.ToLowerInvariant() + "-" + (folioAnswerer ? "answerer-" : "offerer-") + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile);
        if (!folioAnswerer)
            await File.WriteAllTextAsync(Path.Combine(profile, "folio-offer.json"), JsonSerializer.Serialize(new { type = "offer", sdp = offer }));

        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardError = true, RedirectStandardOutput = true
        };
        var browserProfile = Path.Combine(profile, "profile");
        foreach (var argument in new[]
        {
            "--headless=new",
            "--disable-gpu",
            "--no-first-run",
            "--no-default-browser-check",
            "--use-fake-device-for-media-stream",
            "--use-fake-ui-for-media-stream",
            "--autoplay-policy=no-user-gesture-required",
            "--user-data-dir=" + browserProfile,
            origin
        })
            start.ArgumentList.Add(argument);

        using var chrome = Process.Start(start) ?? throw new InvalidOperationException("Cannot launch Chrome.");
        var chromeLog = new StringBuilder(); var chromeLogGate = new object();
        void LogChrome(object _, DataReceivedEventArgs e)
        { if (e.Data is { } line) lock (chromeLogGate) if (chromeLog.Length < 32768) chromeLog.AppendLine(line); }
        chrome.ErrorDataReceived += LogChrome; chrome.OutputDataReceived += LogChrome;
        chrome.BeginErrorReadLine(); chrome.BeginOutputReadLine();

        // Background tone generator: sends 48 kHz Opus sine tone (440 Hz) in 40 ms frames
        using var toneCts = new CancellationTokenSource();
        var toneTask = Task.Run(async () =>
        {
            var toneBuffer = Sine(48000, 0.04);
            while (!toneCts.IsCancellationRequested)
            {
                try
                {
                    if (peer.Dtls.State == "connected")
                    {
                        var localTrack = peer.LocalAudioTrack(0);
                        if (localTrack is not null && !localTrack.Released)
                        {
                            peer.Media.SendAudio(localTrack, toneBuffer, 48000, 1);
                            peer.Media.Tick();
                        }
                    }
                    await Task.Delay(40, toneCts.Token);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception) { /* pump failure does not crash the probe */ }
            }
        });

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var passed = false;
        try
        {
            while (!deadline.IsCancellationRequested)
            {
                var context = await listener.GetContextAsync().WaitAsync(deadline.Token);
                var path = context.Request.Url!.AbsolutePath;
                Log("HTTP request: " + path);
                if (path == "/")
                {
                    var pageContent = Page
                        .Replace("__TARGET_CODEC__", codec)
                        .Replace("__FOLIO_ANSWERS__", folioAnswerer ? "true" : "false");
                    await Reply(context, pageContent, "text/html");
                }
                else if (path == "/offer")
                {
                    await Reply(context, JsonSerializer.Serialize(new { type = "offer", sdp = offer }), "application/json");
                }
                else if (path == "/answer")
                {
                    using var reader = new StreamReader(context.Request.InputStream);
                    var body = await reader.ReadToEndAsync(deadline.Token);
                    await File.WriteAllTextAsync(Path.Combine(profile, "chrome-answer.json"), body, deadline.Token);
                    using var description = JsonDocument.Parse(body);
                    peer.SetRemoteDescription("answer", description.RootElement.GetProperty("sdp").GetString()!);
                    await Reply(context, "ok", "text/plain");
                }
                else if (path == "/negotiate")
                {
                    using var reader = new StreamReader(context.Request.InputStream);
                    var body = await reader.ReadToEndAsync(deadline.Token);
                    await File.WriteAllTextAsync(Path.Combine(profile, "chrome-offer.json"), body, deadline.Token);
                    using var description = JsonDocument.Parse(body);
                    peer.SetRemoteDescription("offer", description.RootElement.GetProperty("sdp").GetString()!);
                    var answer = await peer.CreateAnswerAsync(hasDataChannels: false, audioSenders: 1, videoSenders: 0);
                    peer.SetLocalDescription("answer", answer);
                    var json = JsonSerializer.Serialize(new { type = "answer", sdp = answer });
                    await File.WriteAllTextAsync(Path.Combine(profile, "folio-answer.json"), json, deadline.Token);
                    await Reply(context, json, "application/json");
                }
                else if (path == "/result")
                {
                    using var reader = new StreamReader(context.Request.InputStream);
                    var body = await reader.ReadToEndAsync(deadline.Token);
                    await File.WriteAllTextAsync(Path.Combine(profile, "browser-result.json"), body, deadline.Token);
                    using var resultDoc = JsonDocument.Parse(body);
                    var root = resultDoc.RootElement;
                    var chromeOk = root.GetProperty("ok").GetBoolean();
                    if (root.TryGetProperty("stats", out var statsEl))
                        await File.WriteAllTextAsync(Path.Combine(profile, "stats.json"), statsEl.GetRawText(), deadline.Token);

                    // Wait a moment if needed for video frames to arrive
                    var videoWaitStart = Stopwatch.StartNew();
                    while (decodedVideoFrames.Count < 3 && videoWaitStart.Elapsed < TimeSpan.FromSeconds(5))
                        await Task.Delay(100, deadline.Token);

                    List<MediaCaptureVideoFrame> frames;
                    lock (videoGate) frames = decodedVideoFrames.ToList();

                    var videoNonEmpty = frames.Count > 0 && frames.All(f => f.Width > 0 && f.Height > 0 && f.Rgba.Length == f.Width * f.Height * 4);
                    var videoNonBlack = frames.Count > 0 && frames.All(IsNonBlack);
                    var videoChanged = ChangesOverTime(frames);

                    Log($"Outcome: chromeOk={chromeOk}, ontrackAudio={ontrackAudio}, audioFrames={audioFramesReceived}, isOpus={audioIsOpus}, " +
                        $"ontrackVideo={ontrackVideo}, videoCount={frames.Count}, non-empty={videoNonEmpty}, non-black={videoNonBlack}, changed={videoChanged}");

                    passed = chromeOk &&
                             ontrackAudio && audioFramesReceived >= 5 && audioIsOpus &&
                             ontrackVideo && videoNonEmpty && videoNonBlack && videoChanged;

                    Console.WriteLine($"Chrome report ({codec}): " + body);
                    Console.WriteLine($"Folio media: audio={audioFramesReceived} frames (Opus={audioIsOpus}), video={frames.Count} frames (non-empty={videoNonEmpty}, non-black={videoNonBlack}, changed={videoChanged}).");
                    await Reply(context, "ok", "text/plain");
                    break;
                }
                else
                {
                    context.Response.StatusCode = 404; context.Response.Close();
                }
            }
        }
        catch (OperationCanceledException) { Console.Error.WriteLine($"Browser RTC media deadline expired for {codec}."); }
        catch (Exception error) { Console.Error.WriteLine(error); }
        finally
        {
            toneCts.Cancel();
            try { await toneTask; } catch { }
            if (!chrome.HasExited) chrome.Kill(entireProcessTree: true);
            await chrome.WaitForExitAsync();
            string log;
            lock (chromeLogGate) log = chromeLog.ToString();
            await File.WriteAllTextAsync(Path.Combine(profile, "chrome.log"), log);
            string[] mediaLines;
            lock (logGate) mediaLines = mediaLog.ToArray();
            await File.WriteAllLinesAsync(Path.Combine(profile, "media.log"), mediaLines);

            var resolvedProfile = Path.GetFullPath(browserProfile);
            if (!resolvedProfile.StartsWith(Path.GetFullPath(profile) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Invalid owned browser profile path.");
            try { Directory.Delete(resolvedProfile, recursive: true); }
            catch (IOException) { Console.Error.WriteLine("Owned browser profile remains locked; evidence path contains it."); }
        }

        Console.WriteLine($"Folio ({codec}): ICE={peer.IceConnectionState}, DTLS={peer.Dtls.State}. Media pass={passed}.");
        Console.WriteLine("Probe evidence: " + profile);
        Console.WriteLine("Chrome version: " + FileVersionInfo.GetVersionInfo(executable).ProductVersion +
                          "; Folio role: " + (folioAnswerer ? "answerer" : "offerer") + "; Codec: " + codec);
        return passed ? 0 : 1;
    }

    private static async Task Reply(HttpListenerContext context, string text, string contentType)
    {
        var bytes = Encoding.UTF8.GetBytes(text); context.Response.ContentType = contentType;
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes); context.Response.Close();
    }

    private const string Page = """
        <!doctype html><meta charset="utf-8"><title>Folio RTC independent-peer media probe</title>
        <body>
        <script>
        window.addEventListener('error', function(e) {
          fetch('/result', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ ok: false, error: 'window.error: ' + (e.error ? e.error.stack || e.error.message : e.message) }) }).catch(function(){});
        });
        window.addEventListener('unhandledrejection', function(e) {
          fetch('/result', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ ok: false, error: 'unhandledrejection: ' + (e.reason ? e.reason.stack || e.reason.message || String(e.reason) : 'unknown') }) }).catch(function(){});
        });
        (async function(){
          var targetCodec = '__TARGET_CODEC__';
          var folioAnswers = __FOLIO_ANSWERS__;
          var reported = false;
          var pc = new RTCPeerConnection({ iceServers: [] });
          var audioEl = document.createElement('audio');
          audioEl.autoplay = true;
          document.body.appendChild(audioEl);

          async function report(ok, error, stats){
            if (reported) return;
            reported = true;
            try {
              await fetch('/result', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({
                  ok: ok,
                  error: error || '',
                  connection: pc.connectionState,
                  ice: pc.iceConnectionState,
                  stats: stats || {}
                })
              });
            } catch (_) {}
            pc.close();
          }

          function filterSdpCodecs(sdp, keepCodec){
            var lines = sdp.split('\r\n');
            var otherCodec = keepCodec.toUpperCase() === 'VP8' ? 'H264' : 'VP8';
            var removePts = new Set();
            for (var i = 0; i < lines.length; i++){
              var m = lines[i].match(/^a=rtpmap:(\d+)\s+([^\/]+)/i);
              if (m && m[2].toUpperCase().indexOf(otherCodec) !== -1){
                removePts.add(m[1]);
              }
            }
            for (var i = 0; i < lines.length; i++){
              var m2 = lines[i].match(/^a=fmtp:(\d+)\s+apt=(\d+)/i);
              if (m2 && removePts.has(m2[2])){
                removePts.add(m2[1]);
              }
            }
            var result = [];
            for (var i = 0; i < lines.length; i++){
              var line = lines[i];
              if (line.indexOf('m=video ') === 0){
                var parts = line.split(' ');
                var header = parts.slice(0, 3);
                var pts = parts.slice(3).filter(function(pt){ return !removePts.has(pt); });
                result.push(header.concat(pts).join(' '));
              } else {
                var skip = false;
                for (var pt of removePts){
                  if (line.indexOf('a=rtpmap:' + pt + ' ') === 0 ||
                      line.indexOf('a=rtcp-fb:' + pt + ' ') === 0 ||
                      line.indexOf('a=fmtp:' + pt + ' ') === 0){
                    skip = true;
                    break;
                  }
                }
                if (!skip) result.push(line);
              }
            }
            return result.join('\r\n');
          }

          pc.ontrack = function(event){
            if (event.track.kind === 'audio'){
              audioEl.srcObject = event.streams[0] || new MediaStream([event.track]);
              audioEl.play().catch(function(){});
            }
          };

          async function gather(){
            if (pc.iceGatheringState === 'complete') return;
            await new Promise(function(resolve){
              function check(){
                if (pc.iceGatheringState === 'complete'){
                  pc.removeEventListener('icegatheringstatechange', check);
                  resolve();
                }
              }
              pc.addEventListener('icegatheringstatechange', check);
              if (pc.iceGatheringState === 'complete') {
                pc.removeEventListener('icegatheringstatechange', check);
                resolve();
              }
              setTimeout(resolve, 4000);
            });
          }

          setTimeout(function(){ report(false, 'media probe deadline expired'); }, 25000);

          try {
            var stream = await navigator.mediaDevices.getUserMedia({ audio: true, video: true });
            var tracks = stream.getTracks();
            for (var i = 0; i < tracks.length; i++){
              pc.addTrack(tracks[i], stream);
            }

            if (RTCRtpReceiver.getCapabilities){
              var transceivers = pc.getTransceivers();
              for (var i = 0; i < transceivers.length; i++){
                var tr = transceivers[i];
                if (tr.sender && tr.sender.track && tr.sender.track.kind === 'video' && tr.setCodecPreferences){
                  var caps = RTCRtpReceiver.getCapabilities('video');
                  if (caps && caps.codecs){
                    var match = caps.codecs.filter(function(c){ return c.mimeType.toLowerCase() === 'video/' + targetCodec.toLowerCase(); });
                    var rest = caps.codecs.filter(function(c){ return c.mimeType.toLowerCase() !== 'video/' + targetCodec.toLowerCase(); });
                    tr.setCodecPreferences(match.concat(rest));
                  }
                }
              }
            }

            if (folioAnswers){
              var rawOffer = await pc.createOffer();
              var filteredOffer = { type: 'offer', sdp: filterSdpCodecs(rawOffer.sdp, targetCodec) };
              await pc.setLocalDescription(filteredOffer);
              await gather();
              var answer = await (await fetch('/negotiate', { method: 'POST', body: JSON.stringify(pc.localDescription) })).json();
              await pc.setRemoteDescription(answer);
            } else {
              var offer = await (await fetch('/offer')).json();
              await pc.setRemoteDescription(offer);
              var rawAnswer = await pc.createAnswer();
              var filteredAnswer = { type: 'answer', sdp: filterSdpCodecs(rawAnswer.sdp, targetCodec) };
              await pc.setLocalDescription(filteredAnswer);
              await gather();
              await fetch('/answer', { method: 'POST', body: JSON.stringify(pc.localDescription) });
            }

            var statsPassed = false;
            var lastStatsDump = null;
            var startTime = Date.now();
            var packets = 0, samples = 0, level = 0;
            while (Date.now() - startTime < 20000){
              await new Promise(function(r){ setTimeout(r, 200); });
              var stats = await pc.getStats();
              var dump = {};
              stats.forEach(function(report){
                dump[report.id] = report;
                if (report.type === 'inbound-rtp' && report.kind === 'audio'){
                  packets = Math.max(packets, report.packetsReceived || 0);
                  samples = Math.max(samples, report.totalSamplesReceived || 0);
                  if (typeof report.audioLevel === 'number') level = Math.max(level, report.audioLevel);
                }
                if (report.type === 'track' && report.kind === 'audio' && typeof report.audioLevel === 'number'){
                  level = Math.max(level, report.audioLevel);
                }
              });
              lastStatsDump = dump;
              if (packets >= 10 && samples > 0 && level > 0){
                statsPassed = true;
                break;
              }
            }
            if (statsPassed){
              report(true, '', lastStatsDump);
            } else {
              report(false, 'Audio stats criteria not met: packets=' + packets + ', samples=' + samples + ', level=' + level, lastStatsDump);
            }
          } catch (err){
            report(false, String(err));
          }
        })();
        </script>
        """;
}
