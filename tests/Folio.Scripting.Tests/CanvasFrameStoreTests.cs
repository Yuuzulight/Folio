using Folio.Scripting.Ipc;

namespace Folio.Scripting.Tests;

public sealed class CanvasFrameStoreTests
{
    [Fact]
    public async Task ConcurrentFramesNeverReturnMixedPixels()
    {
        var path = Path.Combine(Path.GetTempPath(), $"folio-canvas-{Guid.NewGuid():N}.frame");
        try
        {
            using var writer = new CanvasFrameStore(path, 64, 64, 256);
            using var reader = new CanvasFrameStore(path, 64, 64, 256);
            var publishing = Task.Run(() =>
            {
                for (var index = 1; index <= 200; index++)
                    writer.Publish(Enumerable.Repeat((byte)(index % 251), 64 * 64 * 4).ToArray());
            }, TestContext.Current.CancellationToken);
            do
            {
                try
                {
                    var pixels = reader.ReadLatest(out var sequence);
                    Assert.All(pixels, pixel => Assert.Equal((byte)(sequence % 251), pixel));
                }
                catch (IOException) { } // A busy writer may force a bounded retry by the caller.
            } while (!publishing.IsCompleted);
            await publishing;
            var final = reader.ReadLatest(out var finalSequence);
            Assert.Equal(200, finalSequence);
            Assert.All(final, pixel => Assert.Equal((byte)200, pixel));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ExistingReaderSeesPublishedPixelsWithoutDiskFlush()
    {
        var path = Path.Combine(Path.GetTempPath(), $"folio-canvas-{Guid.NewGuid():N}.frame");
        try
        {
            using var writer = new CanvasFrameStore(path, 2, 1, 8);
            using var reader = new CanvasFrameStore(path, 2, 1, 8);
            for (var value = 1; value <= 100; value++)
            {
                var pixels = Enumerable.Repeat((byte)value, 8).ToArray();
                writer.Publish(pixels);
                Assert.Equal(pixels, reader.ReadLatest(out var sequence));
                Assert.Equal(value, sequence);
            }
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(0, 2, 8)]
    [InlineData(2, 2, 7)]
    [InlineData(int.MaxValue, 2, int.MaxValue)]
    [InlineData(2, int.MaxValue, 8)]
    public void RejectsInvalidDescriptors(int width, int height, int stride)
    {
        Assert.False(CanvasFrameStore.IsValidDescriptor(width, height, stride));
    }

    [Fact]
    public void PublishesPixelsAndSequenceThroughMappedFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"folio-canvas-{Guid.NewGuid():N}.frame");
        try
        {
            using var store = new CanvasFrameStore(path, 2, 2, 8);
            var pixels = Enumerable.Range(0, 16).Select(value => (byte)value).ToArray();
            store.Publish(pixels);
            var read = store.ReadLatest(out var sequence);
            Assert.Equal(1, sequence);
            Assert.Equal(pixels, read);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ReopensAndRetainsThePublishedSequence()
    {
        var path = Path.Combine(Path.GetTempPath(), $"folio-canvas-{Guid.NewGuid():N}.frame");
        try
        {
            using (var first = new CanvasFrameStore(path, 2, 1, 8))
                first.Publish(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
            using var reopened = new CanvasFrameStore(path, 2, 1, 8);
            var pixels = reopened.ReadLatest(out var sequence);
            Assert.Equal(1, reopened.Sequence);
            Assert.Equal(1, sequence);
            Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, pixels);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void RejectsCorruptHeaderInsteadOfOverwritingIt()
    {
        var path = Path.Combine(Path.GetTempPath(), $"folio-canvas-{Guid.NewGuid():N}.frame");
        try
        {
            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
                stream.Write(BitConverter.GetBytes(0x12345678));
            Assert.Throws<InvalidDataException>(() => new CanvasFrameStore(path, 2, 1, 8));
        }
        finally { File.Delete(path); }
    }
}
