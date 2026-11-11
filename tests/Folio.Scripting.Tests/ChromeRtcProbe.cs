using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Folio.Scripting.Rtc;

namespace Folio.Scripting.Tests;

/// <summary>Independent browser/ICE/DTLS/SCTP acceptance probe, separate from unit tests.</summary>
internal static class ChromeRtcProbe
{
    private const int BinaryBytes = 65536;
    private static byte[] Pattern(int multiplier) => Enumerable.Range(0, BinaryBytes).Select(index => (byte)((index * multiplier) % 251)).ToArray();
    private static string[] Expected(string origin, bool echo) => Enumerable.Range(0, 3)
        .Select(index => (echo ? "echo:" : "") + origin + "-" + index)
        .Concat(["binary:65536", "utf8:60000", "empty-string", "binary:0"]).ToArray();
    private static string Describe(object value, string origin, bool echo)
    {
        if (value is byte[] bytes)
            return bytes.Length == 0 ? "binary:0" : bytes.AsSpan().SequenceEqual(Pattern(origin == "folio" ? 1 : 7)) ? "binary:65536" : "invalid-binary:" + bytes.Length;
        var text = (string)value; var prefix = echo ? "echo:" : "";
        if (text == prefix) return "empty-string";
        if (text == prefix + new string(origin == "folio" ? '\u20ac' : '\u6c49', 20000)) return "utf8:60000";
        return text.Length < 64 ? text : "invalid-text:" + text.Length;
    }
    internal static int Run(string executable, bool folioAnswerer = false) => RunAsync(executable, folioAnswerer).GetAwaiter().GetResult();

    private static async Task<int> RunAsync(string executable, bool folioAnswerer)
    {
        if (!File.Exists(executable)) { Console.Error.WriteLine("Chrome executable not found."); return 2; }
        using var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start(); var port = ((IPEndPoint)reservation.LocalEndpoint).Port; reservation.Stop();
        var origin = $"http://127.0.0.1:{port}/";
        using var listener = new HttpListener(); listener.Prefixes.Add(origin); listener.Start();
        using var peer = new RtcPeerConnectionController();
        var packetLog = new List<string>(); var packetGate = new object();
        void Packet(string direction, byte[] packet)
        {
            lock (packetGate)
                if (packetLog.Count < 512) packetLog.Add(direction + ": " + Convert.ToHexString(packet.AsSpan(0, Math.Min(packet.Length, 96))));
        }
        peer.Dtls.OnApplicationDataReceived += packet => Packet("in", packet);
        peer.Sctp.OnPacketToSend += packet => Packet("out", packet);
        var received = new List<string>(); var receiveGate = new object();
        var reverseReceived = new List<string>();
        var oversizedRefused = false;
        var receivedAll = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var local = peer.CreateDataChannel("folio");
        local.OnOpen += () =>
        {
            try { local.Send(new byte[local.MaximumMessageSize + 1]); }
            catch (ArgumentException) { oversizedRefused = true; }
            for (var i = 0; i < 3; i++) local.Send("folio-" + i);
            local.Send(Pattern(1)); local.Send(new string('\u20ac', 20000)); local.Send(""); local.Send(Array.Empty<byte>());
        };
        local.OnMessage += message =>
        {
            lock (receiveGate)
            {
                received.Add(Describe(message, "folio", echo: true));
                if (received.Count >= 7) receivedAll.TrySetResult();
            }
        };
        peer.OnDataChannel += channel => channel.OnMessage += message =>
        {
            lock (receiveGate) reverseReceived.Add(Describe(message, "chrome", echo: false));
            if (message is byte[] bytes) channel.Send(bytes);
            else channel.Send("echo:" + (string)message);
        };
        var offer = folioAnswerer ? "" : await peer.CreateOfferAsync(true, 0, 0);
        if (!folioAnswerer) peer.SetLocalDescription("offer", offer);
        var evidenceRoot = Environment.GetEnvironmentVariable("FOLIO_RTC_PROBE_OUTPUT") ?? Path.GetTempPath();
        var profile = Path.Combine(Path.GetFullPath(evidenceRoot), "folio-rtc-chrome-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile);
        await File.WriteAllTextAsync(Path.Combine(profile, "folio-offer.json"), JsonSerializer.Serialize(new { type = "offer", sdp = offer }));
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardError = true, RedirectStandardOutput = true
        };
        var browserProfile = Path.Combine(profile, "profile");
        foreach (var argument in new[] { "--headless=new", "--disable-gpu", "--no-first-run", "--no-default-browser-check", "--user-data-dir=" + browserProfile, origin })
            start.ArgumentList.Add(argument);
        using var chrome = Process.Start(start) ?? throw new InvalidOperationException("Cannot launch Chrome.");
        var chromeLog = new StringBuilder(); var logGate = new object();
        void Log(object _, DataReceivedEventArgs e)
        { if (e.Data is { } line) lock (logGate) if (chromeLog.Length < 16384) chromeLog.AppendLine(line); }
        chrome.ErrorDataReceived += Log; chrome.OutputDataReceived += Log;
        chrome.BeginErrorReadLine(); chrome.BeginOutputReadLine();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var passed = false;
        try
        {
            while (!deadline.IsCancellationRequested)
            {
                var context = await listener.GetContextAsync().WaitAsync(deadline.Token);
                var path = context.Request.Url!.AbsolutePath;
                if (path == "/") await Reply(context, Page.Replace("__FOLIO_ANSWERS__", folioAnswerer ? "true" : "false"), "text/html");
                else if (path == "/offer") await Reply(context, JsonSerializer.Serialize(new { type = "offer", sdp = offer }), "application/json");
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
                    var answer = await peer.CreateAnswerAsync(true, 0, 0); peer.SetLocalDescription("answer", answer);
                    var json = JsonSerializer.Serialize(new { type = "answer", sdp = answer });
                    await File.WriteAllTextAsync(Path.Combine(profile, "folio-answer.json"), json, deadline.Token);
                    await Reply(context, json, "application/json");
                }
                else if (path == "/result")
                {
                    using var reader = new StreamReader(context.Request.InputStream);
                    var body = await reader.ReadToEndAsync(deadline.Token);
                    await File.WriteAllTextAsync(Path.Combine(profile, "browser-result.json"), body, deadline.Token);
                    using var result = JsonDocument.Parse(body);
                    if (result.RootElement.GetProperty("ok").GetBoolean())
                        await receivedAll.Task.WaitAsync(deadline.Token);
                    lock (receiveGate)
                        passed = result.RootElement.GetProperty("ok").GetBoolean() &&
                            oversizedRefused && received.SequenceEqual(Expected("folio", echo: true)) &&
                            reverseReceived.SequenceEqual(Expected("chrome", echo: false));
                    Console.WriteLine("Chrome report: " + body);
                    await Reply(context, "ok", "text/plain"); break;
                }
                else { context.Response.StatusCode = 404; context.Response.Close(); }
            }
        }
        catch (OperationCanceledException) { Console.Error.WriteLine("Browser RTC deadline expired."); }
        catch (Exception error) { Console.Error.WriteLine(error); }
        finally
        {
            if (!chrome.HasExited) chrome.Kill(entireProcessTree: true);
            await chrome.WaitForExitAsync();
            string log;
            lock (logGate) log = chromeLog.ToString();
            await File.WriteAllTextAsync(Path.Combine(profile, "chrome.log"), log);
            string[] packets;
            lock (packetGate) packets = packetLog.ToArray();
            await File.WriteAllLinesAsync(Path.Combine(profile, "sctp.log"), packets);
            var resolvedProfile = Path.GetFullPath(browserProfile);
            if (!resolvedProfile.StartsWith(Path.GetFullPath(profile) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Invalid owned browser profile path.");
            try { Directory.Delete(resolvedProfile, recursive: true); }
            catch (IOException) { Console.Error.WriteLine("Owned browser profile remains locked; evidence path contains it."); }
        }
        Console.WriteLine($"Folio: ICE={peer.IceConnectionState}, DTLS={peer.Dtls.State}, SCTP={peer.Sctp.State}, channel={local.ReadyState}.");
        lock (receiveGate) Console.WriteLine("Folio received: " + string.Join(",", received));
        Console.WriteLine("Probe evidence: " + profile);
        Console.WriteLine("Chrome version: " + FileVersionInfo.GetVersionInfo(executable).ProductVersion + "; Folio role: " + (folioAnswerer ? "answerer" : "offerer"));
        return passed ? 0 : 1;
    }

    private static async Task Reply(HttpListenerContext context, string text, string contentType)
    {
        var bytes = Encoding.UTF8.GetBytes(text); context.Response.ContentType = contentType;
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes); context.Response.Close();
    }

    private const string Page = """
        <!doctype html><meta charset="utf-8"><title>Folio RTC independent-peer probe</title>
        <script>
        (async function(){
          var pc=new RTCPeerConnection({iceServers:[]}),forward=[],reverse=[],reported=false,oversizedRefused=false;
          async function report(ok,error){if(reported)return;reported=true;await fetch('/result',{method:'POST',body:JSON.stringify({ok:ok,error:error||'',forward:forward,reverse:reverse,connection:pc.connectionState,ice:pc.iceConnectionState})});pc.close();}
          function expected(origin,echo){return [0,1,2].map(i=>(echo?'echo:':'')+origin+'-'+i).concat(['binary:65536','utf8:60000','empty-string','binary:0']);}
          function describe(value,origin,echo){
            if(value instanceof ArrayBuffer){var bytes=new Uint8Array(value);if(bytes.length===0)return 'binary:0';if(bytes.length!==65536)return 'invalid-binary:'+bytes.length;for(var i=0;i<bytes.length;i++)if(bytes[i]!==i*(origin==='folio'?1:7)%251)return 'invalid-byte:'+i;return 'binary:65536';}
            var prefix=echo?'echo:':'';if(value===prefix)return 'empty-string';if(value===prefix+(origin==='folio'?'\u20ac':'\u6c49').repeat(20000))return 'utf8:60000';return value.length<64?value:'invalid-text:'+value.length;
          }
          function done(){if(forward.length===7&&reverse.length===7)report(oversizedRefused&&forward.join(',')===expected('folio',false).join(',')&&reverse.join(',')===expected('chrome',true).join(','));}
          pc.ondatachannel=function(e){e.channel.binaryType='arraybuffer';e.channel.onmessage=function(m){forward.push(describe(m.data,'folio',false));e.channel.send(typeof m.data==='string'?'echo:'+m.data:m.data);done();};};
          var channel=pc.createDataChannel('chrome');channel.binaryType='arraybuffer';channel.onopen=function(){
            try{channel.send(new Uint8Array(262145));}catch(error){oversizedRefused=error.name==='TypeError';}
            for(var i=0;i<3;i++)channel.send('chrome-'+i);
            var bytes=new Uint8Array(65536);for(var i=0;i<bytes.length;i++)bytes[i]=i*7%251;channel.send(bytes);
            channel.send('\u6c49'.repeat(20000));channel.send('');channel.send(new Uint8Array(0));
          };
          channel.onmessage=function(m){reverse.push(describe(m.data,'chrome',true));done();};
          setTimeout(function(){report(false,'transport/message deadline');},20000);
          try{
            async function gather(){if(pc.iceGatheringState!=='complete')await new Promise(function(resolve){pc.addEventListener('icegatheringstatechange',function(){if(pc.iceGatheringState==='complete')resolve();});});}
            if(__FOLIO_ANSWERS__){
              await pc.setLocalDescription(await pc.createOffer());await gather();
              var answer=await(await fetch('/negotiate',{method:'POST',body:JSON.stringify(pc.localDescription)})).json();
              await pc.setRemoteDescription(answer);
            }else{
              var offer=await(await fetch('/offer')).json();await pc.setRemoteDescription(offer);
              await pc.setLocalDescription(await pc.createAnswer());await gather();
              await fetch('/answer',{method:'POST',body:JSON.stringify(pc.localDescription)});
            }
          }catch(error){report(false,String(error));}
        })();
        </script>
        """;
}
