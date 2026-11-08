using System.Diagnostics;
using System.Runtime.Versioning;
using Folio.Scripting.Gpu.Remote;

namespace Folio.Scripting.Tests;

[SupportedOSPlatform("windows")]
public sealed class GpuSharedHandlesTests
{
    public static bool IsWindows => OperatingSystem.IsWindows();

    [Fact(Skip = "Only supported on Windows.", SkipUnless = nameof(IsWindows))]
    public void DuplicatedSectionSharesBytesAndRejectsOversizedViews()
    {
        using var self = Process.GetCurrentProcess();
        using var section = GpuSharedHandles.CreateSection(64 * 1024);
        using var duplicate = GpuSharedHandles.AdoptSection(GpuSharedHandles.DuplicateSection(section, self.SafeHandle));
        using var writer = GpuSharedHandles.Map(section, 64 * 1024);
        using var reader = GpuSharedHandles.Map(duplicate, 64 * 1024);

        writer.Write(65_000, [1, 2, 3, 4]);
        var copy = new byte[4];
        reader.Read(65_000, copy);
        Assert.Equal([1, 2, 3, 4], copy);

        Assert.Throws<ArgumentOutOfRangeException>(() => reader.Read(65_534, new byte[4]));
        Assert.Throws<ArgumentOutOfRangeException>(() => writer.Write(-1, [1]));
        Assert.Throws<IOException>(() => GpuSharedHandles.Map(section, 128 * 1024));
        Assert.Throws<ArgumentOutOfRangeException>(() => GpuSharedHandles.CreateSection(GpuSharedHandles.MaxSectionBytes + 1));
    }

    [Fact(Skip = "Only supported on Windows.", SkipUnless = nameof(IsWindows))]
    public void UploadRingWritesThroughASharedSection()
    {
        using var self = Process.GetCurrentProcess();
        using var section = GpuSharedHandles.CreateSection(4096);
        using var duplicate = GpuSharedHandles.AdoptSection(GpuSharedHandles.DuplicateSection(section, self.SafeHandle));
        using var producer = GpuSharedHandles.Map(section, 4096);
        using var consumer = GpuSharedHandles.Map(duplicate, 4096);
        var ring = new GpuUploadRing(producer);

        Assert.True(ring.TryWrite([9, 8, 7], out var offset));
        var window = ring.PublishBatch(1);
        var header = new GpuCommandBatch.Header(1, 0, 0, window.Start, window.End, false);
        Assert.True(GpuCommandBatch.TryResolveUpload(header, consumer.Length, offset, 3, out var physical));
        var copy = new byte[3];
        consumer.Read(physical, copy);
        Assert.Equal([9, 8, 7], copy);
    }

    [Fact(Skip = "Only supported on Windows.", SkipUnless = nameof(IsWindows))]
    public void DuplicatedPipeEndCarriesBytes()
    {
        using var self = Process.GetCurrentProcess();
        var (read, write) = GpuSharedHandles.CreatePipe();
        using var input = new FileStream(read, FileAccess.Read);
        using (var adopted = new FileStream(GpuSharedHandles.AdoptPipe(GpuSharedHandles.DuplicatePipe(write, self.SafeHandle)), FileAccess.Write))
        {
            write.Dispose(); // only the duplicate keeps the write end open
            adopted.Write([5, 6, 7]);
        }
        var copy = new byte[3];
        input.ReadExactly(copy);
        Assert.Equal([5, 6, 7], copy);
        Assert.Equal(0, input.Read(new byte[1])); // EOF once every write end is closed
    }
}
