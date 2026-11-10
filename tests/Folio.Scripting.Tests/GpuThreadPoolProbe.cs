using System.IO.Pipes;
using Folio.Scripting.Gpu;

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
            Console.WriteLine("32 pipe replies, timeout, cancellation and pump retirement passed with the sole CLR pool thread occupied.");
            return 0;
        }
        finally { release.Set(); blocker.GetAwaiter().GetResult(); }
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
