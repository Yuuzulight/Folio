using Folio.Scripting.Ipc;

namespace Folio.Scripting.Tests;

public sealed class CanvasFrameStoreTests
{
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
}
