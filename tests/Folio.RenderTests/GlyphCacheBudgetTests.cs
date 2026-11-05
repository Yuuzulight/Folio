using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using Folio.Painting;
using Folio.Painting.Software;
using Folio.Typography;

namespace Folio.RenderTests;

public class GlyphCacheBudgetTests
{
    private static byte[] FontBytes() => File.ReadAllBytes(Path.Combine(RepoPaths.Tests, "fonts", "SourceSans3-Regular.ttf"));

    private static long Bytes(FontFace face)
    {
        var usage = SoftwareCanvas.GlyphCacheUsage(face);
        return usage.Outlines + usage.Flattened + usage.Coverage + usage.Hinting + BitmapGlyphs.CacheBytes(face) + ColorGlyphs.CacheBytes(face);
    }

    private static byte[] Render(FontFace face)
    {
        using var surface = new SoftwareRasterSurface(80, 64);
        surface.Canvas.DrawGlyphs(face, 32, [face.GlyphFor('A'), face.GlyphFor('V')],
            [new Vector2(8.25f, 40), new Vector2(34.5f, 40)], new Paint(new Rgba(.2f, .5f, .8f, .6f)));
        return surface.PremultipliedPixels.ToArray();
    }

    private static void Populate(FontFace face)
    {
        for (ushort glyph = 0; glyph < 350; glyph++)
        {
            SoftwareCanvas.Flattened(face, glyph, .5f);
            FontHinting.TryGet(face, glyph, 18, out _, out _);
        }
    }

    [Fact]
    public void AllLiveFacesShareEightMiBAndEvictionDoesNotChangeRendering()
    {
        var source = FontBytes();
        var faces = new List<FontFace>();
        lock (GlyphCacheBudget.Sync)
        {
            var first = FontFace.Parse(source)!; faces.Add(first);
            Populate(first); var expected = Render(first); var firstBytes = Bytes(first);
            Assert.True(firstBytes > 0);
            for (var index = 0; index < 12; index++)
            {
                var face = FontFace.Parse(source)!; faces.Add(face);
                Populate(face); Render(face);
                // Check actual per-face contents as well as the shared accountant: accounting alone must not hide
                // arrays retained in a cache that was supposedly evicted.
                Assert.InRange(faces.Sum(Bytes), 0, GlyphCacheBudget.Limit);
                Assert.True(GlyphCacheBudget.Usage.Bytes >= faces.Sum(Bytes));
                Assert.InRange(GlyphCacheBudget.Usage.Bytes, 0, GlyphCacheBudget.Limit);
                Assert.InRange(GlyphCacheBudget.Usage.RegionCount, 0, GlyphCacheBudget.MaxRegions);
            }
            Assert.True(Bytes(first) < firstBytes, "The cold first font should have been evicted under cross-font pressure.");
            Assert.Equal(expected, Render(first));
            Assert.InRange(GlyphCacheBudget.Usage.Bytes, 0, GlyphCacheBudget.Limit);
            GC.KeepAlive(faces);
        }
    }

    [Fact]
    public void EmojiBitmapsAndColourLayersShareTheOutlineBudget()
    {
        var source = FontBytes();
        var baseFace = FontFace.Parse(source)!;
        var glyph = baseFace.GlyphFor('A');
        // Large enough to force eviction across live faces, using an actual decoded emoji strike.
        var emojiBytes = File.ReadAllBytes(Path.Combine(RepoPaths.Tests, "fonts", "NotoColorEmoji.ttf"));
        var faces = new List<FontFace>();
        lock (GlyphCacheBudget.Sync)
        {
            var colour = FontFace.Parse(SoftwareRasterTests.AddTables(source,
                ("CPAL", SoftwareRasterTests.SyntheticCpal()), ("COLR", SoftwareRasterTests.SyntheticColrV1(glyph))))!;
            faces.Add(colour);
            var expected = ColorGlyphs.Get(colour, glyph);
            Assert.NotNull(expected);
            for (var i = 0; i < 5; i++)
            {
                var face = FontFace.Parse(emojiBytes)!; faces.Add(face);
                for (ushort g = 1; g < Math.Min(500, (int)face.GlyphCount); g++) BitmapGlyphs.Get(face, g);
                Assert.InRange(faces.Sum(Bytes), 0, GlyphCacheBudget.Limit);
                Assert.True(GlyphCacheBudget.Usage.Bytes >= faces.Sum(Bytes));
                Assert.InRange(GlyphCacheBudget.Usage.Bytes, 0, GlyphCacheBudget.Limit);
            }
            Assert.Equal(0, ColorGlyphs.CacheBytes(colour));
            Assert.Equal(expected, ColorGlyphs.Get(colour, glyph));
            GC.KeepAlive(faces);
        }
    }

    [Fact]
    public void DeepAcyclicColourPaintGraphIsRejectedWithoutOverflow()
    {
        var source = FontBytes(); var original = FontFace.Parse(source)!; var glyph = original.GlyphFor('A');
        var colr = SoftwareRasterTests.SyntheticColrV1(glyph);
        Array.Resize(ref colr, 44 + 12 * 100 + 12);
        for (var i = 0; i < 100; i++)
        {
            var offset = 44 + i * 12;
            colr[offset] = 14; colr[offset + 3] = 12; // PaintTranslate -> next node.
        }
        var terminal = 44 + 100 * 12;
        SoftwareRasterTests.SyntheticColrV1(glyph).AsSpan(44, 12).CopyTo(colr.AsSpan(terminal));
        var face = FontFace.Parse(SoftwareRasterTests.AddTables(source,
            ("CPAL", SoftwareRasterTests.SyntheticCpal()), ("COLR", colr)))!;
        Assert.Null(ColorGlyphs.Get(face, glyph));
    }

    [Fact]
    public void DeepSbixDuplicateChainIsRejectedWithoutOverflow()
    {
        var source = FontBytes(); var original = FontFace.Parse(source)!;
        var count = original.GlyphCount;
        var header = 4 + 4 * (count + 1);
        var sbix = new byte[12 + header + 100 * 10];
        BinaryPrimitives.WriteUInt32BigEndian(sbix.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt32BigEndian(sbix.AsSpan(8), 12);
        BinaryPrimitives.WriteUInt16BigEndian(sbix.AsSpan(12), 64);
        for (var i = 0; i <= count; i++)
            BinaryPrimitives.WriteUInt32BigEndian(sbix.AsSpan(16 + 4 * i), (uint)(header + Math.Min(i, 100) * 10));
        for (var i = 0; i < 100; i++)
        {
            var offset = 12 + header + i * 10;
            "dupe"u8.CopyTo(sbix.AsSpan(offset + 4));
            BinaryPrimitives.WriteUInt16BigEndian(sbix.AsSpan(offset + 8), (ushort)(i + 1));
        }
        var face = FontFace.Parse(SoftwareRasterTests.AddTables(source, ("sbix", sbix)))!;
        Assert.Null(BitmapGlyphs.Get(face, 0));
    }

    [Fact]
    public void OversizedSbixImageIsRejectedBeforePixelAllocation()
    {
        var source = FontBytes(); var original = FontFace.Parse(source)!; var glyph = original.GlyphFor('A');
        var png = File.ReadAllBytes(Path.Combine(RepoPaths.Tests, "images", "gray-1.png"));
        // The PNG decoder ignores CRCs; these dimensions exercise the glyph-specific allocation ceiling.
        BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(16), 4096);
        BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(20), 4096);
        var face = FontFace.Parse(SoftwareRasterTests.AddTables(source,
            ("sbix", SoftwareRasterTests.SyntheticSbix(original.GlyphCount, glyph, png))))!;
        Assert.Null(BitmapGlyphs.Get(face, glyph));
    }

    [Theory]
    [InlineData(8, true)]
    [InlineData(9, false)]
    public void RepeatedGradientBranchesShareOneStopAllocationBudget(int branches, bool accepted)
    {
        var source = FontBytes(); var original = FontFace.Parse(source)!; var glyph = original.GlyphFor('A');
        var colr = new byte[129 + 512 * 6];
        BinaryPrimitives.WriteUInt16BigEndian(colr, 1);
        BinaryPrimitives.WriteUInt32BigEndian(colr.AsSpan(14), 34);
        BinaryPrimitives.WriteUInt32BigEndian(colr.AsSpan(18), 50);
        BinaryPrimitives.WriteUInt32BigEndian(colr.AsSpan(34), 1);
        BinaryPrimitives.WriteUInt16BigEndian(colr.AsSpan(38), glyph);
        BinaryPrimitives.WriteUInt32BigEndian(colr.AsSpan(40), 10);
        colr[44] = 1; colr[45] = (byte)branches;
        BinaryPrimitives.WriteUInt32BigEndian(colr.AsSpan(50), 9);
        for (var i = 0; i < 9; i++) BinaryPrimitives.WriteUInt32BigEndian(colr.AsSpan(54 + 4 * i), 40);
        colr[90] = 10; colr[93] = 6;
        BinaryPrimitives.WriteUInt16BigEndian(colr.AsSpan(94), glyph);
        colr[96] = 4; colr[99] = 30;
        BinaryPrimitives.WriteInt16BigEndian(colr.AsSpan(104), 500);
        BinaryPrimitives.WriteUInt16BigEndian(colr.AsSpan(127), 512);
        for (var i = 0; i < 512; i++) BinaryPrimitives.WriteInt16BigEndian(colr.AsSpan(133 + 6 * i), 0x4000);
        var face = FontFace.Parse(SoftwareRasterTests.AddTables(source,
            ("CPAL", SoftwareRasterTests.SyntheticCpal()), ("COLR", colr)))!;
        var layers = ColorGlyphs.Get(face, glyph);
        if (accepted) Assert.Equal(branches, Assert.IsType<ColorGlyphs.Layer[]>(layers).Length);
        else Assert.Null(layers);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference<FontFace> RetiredFont()
    {
        var face = FontFace.Parse(FontBytes())!;
        Render(face); Populate(face);
        return new WeakReference<FontFace>(face);
    }

    [Fact]
    public void SharedAccountingDoesNotKeepARetiredFontAlive()
    {
        var retired = RetiredFont();
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Assert.False(retired.TryGetTarget(out _));
        Assert.InRange(GlyphCacheBudget.Usage.Bytes, 0, GlyphCacheBudget.Limit);
    }

    [Fact]
    public async Task NestedLookupsAndCrossFontEvictionDoNotDeadlockParallelRenderers()
    {
        var source = FontBytes();
        var cancellation = TestContext.Current.CancellationToken;
        var tasks = Enumerable.Range(0, 12).Select(workerIndex => Task.Run(() =>
        {
            var face = FontFace.Parse(source)!;
            for (ushort glyph = 0; glyph < 100; glyph++)
            {
                cancellation.ThrowIfCancellationRequested();
                SoftwareCanvas.Flattened(face, glyph, .5f);
                FontHinting.TryGet(face, glyph, 16, out _, out _);
                Render(face);
                Assert.InRange(GlyphCacheBudget.Usage.Bytes, 0, GlyphCacheBudget.Limit);
            }
        }, cancellation)).ToArray();
        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(30), cancellation);
    }
}
