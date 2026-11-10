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
                if (packetLog.Count < 128) packetLog.Add(direction + ": " + Convert.ToHexString(packet.AsSpan(0, Math.Min(packet.Length, 96))));
        }
        peer.Dtls.OnApplicationDataReceived += packet => Packet("in", packet);
        peer.Sctp.OnPacketToSend += packet => Packet("out", packet);
        var received = new List<string>(); var receiveGate = new object();
        var receivedAll = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var local = peer.CreateDataChannel("folio");
        local.OnOpen += () => { for (var i = 0; i < 3; i++) local.Send("folio-" + i); };
        local.OnMessage += message =>
        {
            lock (receiveGate)
            {
                received.Add((string)message);
                if (received.Count >= 3) receivedAll.TrySetResult();
            }
        };
        peer.OnDataChannel += channel => channel.OnMessage += message => channel.Send("echo:" + (string)message);
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
                            received.SequenceEqual(new[] { "echo:folio-0", "echo:folio-1", "echo:folio-2" });
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
          var pc=new RTCPeerConnection({iceServers:[]}),forward=[],reverse=[],reported=false;
          async function report(ok,error){if(reported)return;reported=true;await fetch('/result',{method:'POST',body:JSON.stringify({ok:ok,error:error||'',forward:forward,reverse:reverse,connection:pc.connectionState,ice:pc.iceConnectionState})});pc.close();}
          function done(){if(forward.length===3&&reverse.length===3)report(forward.join(',')==='folio-0,folio-1,folio-2'&&reverse.join(',')==='echo:chrome-0,echo:chrome-1,echo:chrome-2');}
          pc.ondatachannel=function(e){e.channel.onmessage=function(m){forward.push(m.data);e.channel.send('echo:'+m.data);done();};};
          var channel=pc.createDataChannel('chrome');channel.onopen=function(){for(var i=0;i<3;i++)channel.send('chrome-'+i);};
          channel.onmessage=function(m){reverse.push(m.data);done();};
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
