using Folio.Scripting.Rtc;

namespace Folio.Scripting.Tests;

internal static class RtcThreadPoolProbe
{
    internal static int Run()
    {
        ThreadPool.GetMinThreads(out _, out var minIo); ThreadPool.GetMaxThreads(out _, out var maxIo);
        if (!ThreadPool.SetMinThreads(1, minIo) || !ThreadPool.SetMaxThreads(1, maxIo)) return 2;
        using var occupied = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var blocker = Task.Run(() => { occupied.Set(); release.Wait(); });
        if (!occupied.Wait(TimeSpan.FromSeconds(3))) return 3;
        try
        {
            using var a = new RtcPeerConnectionController(); using var b = new RtcPeerConnectionController();
            using var delivered = new ManualResetEventSlim(); using var opened = new ManualResetEventSlim();
            var payload = Enumerable.Range(0, 262144).Select(index => (byte)(index % 251)).ToArray();
            var correct = false;
            b.OnDataChannel += channel => channel.OnMessage += message =>
            { correct = message is byte[] bytes && bytes.AsSpan().SequenceEqual(payload); delivered.Set(); };
            var local = a.CreateDataChannel("starved-pool"); local.OnOpen += () => opened.Set();
            var offer = a.CreateOfferAsync(true, 0, 0).GetAwaiter().GetResult();
            a.SetLocalDescription("offer", offer); b.SetRemoteDescription("offer", offer);
            var answer = b.CreateAnswerAsync(true, 0, 0).GetAwaiter().GetResult();
            b.SetLocalDescription("answer", answer); a.SetRemoteDescription("answer", answer);
            if (!opened.Wait(TimeSpan.FromSeconds(5)))
            { Console.Error.WriteLine("Negotiation: " + a.Sctp.GetDiagnostics() + "; " + b.Sctp.GetDiagnostics()); return 4; }
            local.Send(payload);
            if (!delivered.Wait(TimeSpan.FromSeconds(5)) || !correct)
            { Console.Error.WriteLine("Delivery: " + a.Sctp.GetDiagnostics() + "; " + b.Sctp.GetDiagnostics()); return 5; }
            a.Dispose(); b.Dispose();
            if (!SpinWait.SpinUntil(() => !a.Ice.IsReceivePumpAlive && !b.Ice.IsReceivePumpAlive, TimeSpan.FromSeconds(2)))
            { Console.Error.WriteLine("Owned socket pumps did not retire after disposal."); return 6; }
            Console.WriteLine("Negotiated and delivered 256 KiB with the sole CLR pool thread occupied.");
            return 0;
        }
        finally { release.Set(); blocker.GetAwaiter().GetResult(); }
    }
}
