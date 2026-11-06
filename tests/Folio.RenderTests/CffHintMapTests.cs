using System.Numerics;
using Folio.Painting;
using Folio.Typography;

namespace Folio.RenderTests;

public class CffHintMapTests
{
    [Theory]
    [InlineData(.5f, .4f)]
    [InlineData(1f, .275f)]
    [InlineData(1.667f, .275f)]
    [InlineData(2.333f, 0f)]
    [InlineData(10f, 0f)]
    public void OpticalCompensationTapersAwayForHeavyStems(float width, float expected)
    {
        Assert.Equal(expected, CffHintMap.Darkening(width));
    }

    [Fact]
    public void VerticalStemsKeepSubpixelPlacementAndGainOnlyBoundedOpticalWeight()
    {
        var map = new CffHintMap(.016f);
        map.AddStems(false, [176, 77]);
        var low = map.Map(new(176, 0)).X; var high = map.Map(new(253, 0)).X;
        Assert.InRange(Math.Abs(low - 176 * .016f), 0, .1251f);
        Assert.InRange(high - low, 1.25f, 1.75f);
        Assert.Equal(low * 4, MathF.Round(low * 4));
    }
    [Fact]
    public void StemHintsFitEdgesAndInterpolateTheSurroundingOutline()
    {
        var map = new CffHintMap(.01f);
        map.AddStems(true, [10, 60, 80, 100]); // [10,70], [150,250] font units.
        Assert.Equal(new Vector2(2, 0), map.Map(new(200, 10)));
        Assert.Equal(new Vector2(2, 1), map.Map(new(200, 70)));
        Assert.Equal(new Vector2(2, 2), map.Map(new(200, 150)));
        Assert.Equal(new Vector2(2, 3), map.Map(new(200, 250)));
        Assert.Equal(new Vector2(2, 1.5f), map.Map(new(200, 110)));
    }

    [Fact]
    public void HintMasksChooseNonOverlappingSetsAndRejectUndefinedBits()
    {
        var map = new CffHintMap(.01f);
        map.AddStems(true, [10, 60, -30, 60]);
        Assert.Throws<InvalidDataException>(() => map.Map(new(0, 55)));
        map.Mask([0x80]); Assert.Equal(0, map.Map(new(0, 10)).Y);
        map.Mask([0x40]); Assert.Equal(0, map.Map(new(0, 40)).Y);
        Assert.Throws<InvalidDataException>(() => map.Mask([0x41]));
        Assert.Throws<InvalidDataException>(() => map.Mask([]));
    }

    [Fact]
    public void GhostHintsAndAlignmentZonesDoNotBecomeNegativeWidthStems()
    {
        var map = new CffHintMap(.01f, [-10, 10, 700, 10]);
        map.AddStems(true, [21, -21, 700, -20]);
        Assert.Equal(0, map.Map(new(0, 0)).Y);
        Assert.Equal(7, map.Map(new(0, 700)).Y);
        Assert.Throws<InvalidDataException>(() => map.AddStems(true, [0, -1]));
    }

    [Fact]
    public void StemCountsAndCoordinatesStayBounded()
    {
        var map = new CffHintMap(.01f);
        map.AddStems(true, Enumerable.Range(0, 96).SelectMany(_ => new double[] { 100, 50 }).ToArray());
        Assert.Throws<InvalidDataException>(() => map.AddStems(true, [100, 50]));
        Assert.Throws<InvalidDataException>(() => new CffHintMap(float.NaN));
        Assert.Throws<InvalidDataException>(() => new CffHintMap(.01f).AddStems(true, [double.NaN, 1]));
    }

    [Fact]
    public void DenseStemFittingPreservesCountersAndNeverReversesTheOutline()
    {
        var map = new CffHintMap(.012f);
        map.AddStems(true, [149, 190, 26, 66]);
        var firstBottom = map.Map(new(0, 149)).Y; var firstTop = map.Map(new(0, 339)).Y;
        var secondBottom = map.Map(new(0, 365)).Y; var secondTop = map.Map(new(0, 431)).Y;
        Assert.InRange(firstTop - firstBottom, 2.25f - 1f / 64, 2.25f + 1f / 64);
        Assert.InRange(secondTop - secondBottom, .9999f, 1.0001f);
        Assert.InRange(secondBottom - firstTop, .312f - 1f / 64, .312f + 1f / 64);
        var previous = float.NegativeInfinity;
        for (var y = 0; y < 500; y++)
        { var current = map.Map(new(0, y)).Y; Assert.True(current >= previous); previous = current; }
    }

    [Fact]
    public void CounterMasksPreserveWhitespaceWithoutChangingTheActiveHintSet()
    {
        var map = new CffHintMap(.01f);
        map.AddStems(true, [10, 60, 130, 100]);
        map.CounterMask([0xC0]);
        Assert.Throws<InvalidDataException>(() => map.AddStems(true, [1, 10]));
        var firstTop = map.Map(new(0, 70)).Y; var nextBottom = map.Map(new(0, 200)).Y;
        Assert.True(nextBottom - firstTop >= 1.3f - 1f / 64);
        map.Mask([0x80]);
        Assert.Equal(0, map.Map(new(0, 10)).Y);
        Assert.Equal(1, map.Map(new(0, 70)).Y);
        Assert.Throws<InvalidDataException>(() => map.CounterMask([0xC1]));
        for (var i = 1; i < 96; i++) map.CounterMask([0xC0]);
        Assert.Throws<InvalidDataException>(() => map.CounterMask([0xC0]));
    }

    [Theory]
    [InlineData("NotoSansSC-Regular.otf")]
    [InlineData("NotoSansJP-Regular.otf")]
    public void BundledCjkCorpusExecutesWithoutFallingBack(string name)
    {
        var face = FontFace.Parse(File.ReadAllBytes(Path.Combine(RepoPaths.Tests, "fonts", name)))!;
        var text = string.Join("", Directory.GetFiles(Path.Combine(RepoPaths.Tests, "conformance", "S6"), "index.html", SearchOption.AllDirectories).Select(File.ReadAllText));
        var glyphs = text.EnumerateRunes().Where(r => r.Value > 127).Select(r => face.GlyphFor(r.Value)).Where(g => g != 0).Distinct().ToArray();
        foreach (var size in new[] { 12f, 16f, 17.6f, 24f, 35.2f })
            foreach (var glyph in glyphs)
                Assert.True(FontHinting.TryGet(face, glyph, size, out _, out var error), $"{name} glyph {glyph} at {size}: {error}");
    }
}
