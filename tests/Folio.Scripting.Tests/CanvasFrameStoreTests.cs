using Folio.Scripting.Ipc;

namespace Folio.Scripting.Tests;

public sealed class CanvasFrameStoreTests
{
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
