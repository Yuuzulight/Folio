using System.IO.Pipes;
using Folio.Scripting.Gpu;
using Folio.Scripting.Gpu.Remote;

namespace Folio.Scripting.Tests;

internal static class GpuThreadPoolProbe
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
            using var requests = new AnonymousPipeServerStream(PipeDirection.Out);
            using var replies = new AnonymousPipeServerStream(PipeDirection.In);
            using var read = new AnonymousPipeClientStream(PipeDirection.In, requests.ClientSafePipeHandle);
            using var write = new AnonymousPipeClientStream(PipeDirection.Out, replies.ClientSafePipeHandle);
            Exception? serverError = null;
            var server = new Thread(() =>
            {
                try
                {
                    for (var i = 0; i < 32; i++)
                    {
                        var packet = new byte[GpuWorkerProtocol.RequestBytes]; read.ReadExactly(packet);
                        var response = new byte[GpuWorkerProtocol.HeaderBytes];
                        GpuWorkerProtocol.Put(response, 0, GpuWorkerProtocol.Magic);
                        GpuWorkerProtocol.Put(response, 1, GpuWorkerProtocol.Get(packet, 1) | 0x40000000);
                        GpuWorkerProtocol.Put(response, 2, GpuWorkerProtocol.Get(packet, 2));
                        write.Write(response); write.Flush();
                    }
                }
                catch (Exception error) { serverError = error; }
            }) { IsBackground = true };
            server.Start();
            using (var broker = new GpuWorkerBroker(requests, replies, () => { read.Dispose(); write.Dispose(); }, TimeSpan.FromSeconds(2)))
            {
                for (var i = 0; i < 32; i++) broker.Exchange(GpuWorkerProtocol.Request(GpuWorkerCommand.Open));
                broker.Dispose();
                if (!SpinWait.SpinUntil(() => !broker.IsIoThreadAlive, TimeSpan.FromSeconds(2))) return 4;
            }
            if (!server.Join(TimeSpan.FromSeconds(2)) || serverError is not null) throw new IOException("Pipe peer failed.", serverError);
            using var stalled = new StalledStream();
            using var timed = new GpuWorkerBroker(new MemoryStream(), stalled, () => stalled.Release.Set(), TimeSpan.FromMilliseconds(100));
            try { timed.Exchange(GpuWorkerProtocol.Request(GpuWorkerCommand.Open)); return 5; }
            catch (TimeoutException) { }
            if (!timed.IsLost || !SpinWait.SpinUntil(() => !timed.IsIoThreadAlive, TimeSpan.FromSeconds(2))) return 6;

            using var cancellation = new CancellationTokenSource();
            using var canceledStream = new StalledStream();
            using var canceled = new GpuWorkerBroker(new MemoryStream(), canceledStream, () => canceledStream.Release.Set(), TimeSpan.FromSeconds(5));
            var canceler = new Thread(() => { canceledStream.Entered.Wait(); cancellation.Cancel(); }) { IsBackground = true };
            canceler.Start();
            try { canceled.Exchange(GpuWorkerProtocol.Request(GpuWorkerCommand.Open), cancellation.Token); return 7; }
            catch (OperationCanceledException) { }
            if (!canceler.Join(TimeSpan.FromSeconds(2)) || !canceled.IsLost ||
                !SpinWait.SpinUntil(() => !canceled.IsIoThreadAlive, TimeSpan.FromSeconds(2))) return 8;
            if (!WebGlPipeProgress()) return 9;
            Console.WriteLine("32 pipe replies, timeout, cancellation and pump retirement passed with the sole CLR pool thread occupied.");
            return 0;
        }
        finally { release.Set(); blocker.GetAwaiter().GetResult(); }
    }

    private static bool WebGlPipeProgress()
    {
        using var commands = new AnonymousPipeServerStream(PipeDirection.Out);
        using var replies = new AnonymousPipeServerStream(PipeDirection.In);
        using var read = new AnonymousPipeClientStream(PipeDirection.In, commands.ClientSafePipeHandle);
        using var write = new AnonymousPipeClientStream(PipeDirection.Out, replies.ClientSafePipeHandle);
        Exception? serverError = null;
        var server = new Thread(() =>
        {
            try
            {
                for (var i = 0; i < 32; i++)
                {
                    var batch = GpuChannelFraming.Read(read, GpuCommandBatch.MaxBatchBytes)!;
                    GpuChannelFraming.Write(write, batch);
                }
            }
            catch (Exception error) { serverError = error; }
        }) { IsBackground = true };
        server.Start();
        using var transport = new GpuWebGlPipeTransport(commands, replies, TimeSpan.FromSeconds(2));
        for (var i = 0; i < 32; i++)
        {
            var batch = new byte[] { 1, 2, (byte)i };
            if (!transport.Exchange(batch).AsSpan().SequenceEqual(batch)) return false;
        }
        transport.Dispose();
        if (!server.Join(TimeSpan.FromSeconds(2)) || serverError is not null || transport.IsPumpAlive) return false;
        if (OperatingSystem.IsWindows())
        {
            // The peer keeps both pipes open and does not consume commands or
            // produce a reply. Cancellation must retire reads AND blocked writes.
            foreach (var blockedWrite in new[] { false, true })
            {
                using var stalledCommands = new AnonymousPipeServerStream(PipeDirection.Out);
                using var stalledReplies = new AnonymousPipeServerStream(PipeDirection.In);
                using var peerRead = new AnonymousPipeClientStream(PipeDirection.In, stalledCommands.ClientSafePipeHandle);
                using var peerWrite = new AnonymousPipeClientStream(PipeDirection.Out, stalledReplies.ClientSafePipeHandle);
                using var stalledTransport = new GpuWebGlPipeTransport(stalledCommands, stalledReplies, TimeSpan.FromMilliseconds(100));
                try { stalledTransport.Exchange(new byte[blockedWrite ? GpuCommandBatch.MaxBatchBytes : 0]); return false; }
                catch (TimeoutException) { }
                if (!SpinWait.SpinUntil(() => !stalledTransport.IsPumpAlive, TimeSpan.FromSeconds(2))) return false;
                try { stalledTransport.Exchange([]); return false; }
                catch (IOException) { }
            }
        }
        Console.WriteLine("WebGL framed pipe progress and stalled read/write retirement passed without a free shared pool worker.");
        return true;
    }

    private sealed class StalledStream : Stream
    {
        internal readonly ManualResetEventSlim Entered = new(), Release = new();
        public override int Read(byte[] buffer, int offset, int count)
        { Entered.Set(); Release.Wait(); throw new EndOfStreamException(); }
        public override int Read(Span<byte> buffer)
        { Entered.Set(); Release.Wait(); throw new EndOfStreamException(); }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
