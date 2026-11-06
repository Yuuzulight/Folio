using System.Numerics;
using Folio.Painting;
using Folio.Painting.Software;
using Folio.Skia;
using Folio.Typography;

namespace Folio.RenderTests;

public class TrueTypeMachineTests(ITestOutputHelper output)
{
    private static TrueTypeMachine Machine() => new(16, 1000, 8, 8, [40, 60]);

    [Fact]
    public void HintCachesRemainBoundedAcrossManySizesAndGlyphs()
    {
        var face = FontFace.Parse(File.ReadAllBytes(Path.Combine(RepoPaths.Tests, "fonts", "SourceSans3-Regular.ttf")))!;
        for (var size = 8; size <= 64; size++)
            foreach (var c in "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789")
            {
                Assert.True(FontHinting.TryGet(face, face.GlyphFor(c), size, out var path, out var error), error);
                Assert.NotNull(path);
                Assert.InRange(FontHinting.CacheBytes(face), 0, FontHinting.CacheBudget);
            }
    }

    [Theory]
    [InlineData("SourceSans3-Bold.ttf")]
    [InlineData("SourceSans3-It.ttf")]
    [InlineData("SourceCodePro-Regular.ttf")]
    [InlineData("SourceSerif4-Regular.ttf")]
    public void OtherBundledLatinFontsDoNotSilentlyFallBack(string name)
    {
        var face = FontFace.Parse(File.ReadAllBytes(Path.Combine(RepoPaths.Tests, "fonts", name)))!;
        foreach (var size in new[] { 12f, 16f, 17.6f, 32f, 35.2f, 48f })
            foreach (var c in "AVIZabcdefgijéñÁ0123456789")
                Assert.True(FontHinting.TryGet(face, face.GlyphFor(c), size, out _, out var error), $"{name} {c} {size}: {error}");
    }

    [Theory]
    [InlineData(32f)]
    [InlineData(35.2f)]
    [InlineData(48f)]
    public void HintedLatinOutlinesMatchTheRasterOracle(float pixelsPerEm)
    {
        var face = FontFace.Parse(File.ReadAllBytes(Path.Combine(RepoPaths.Tests, "fonts", "SourceSans3-Regular.ttf")))!;
        var prepared = FontHinting.Prepare(face, pixelsPerEm);
        using var actual = new SoftwareRasterSurface(240, 100);
        using var expected = new SkiaRasterBackend().CreateSurface(240, 100)!;
        var paint = new Paint(new Rgba(.2f, .5f, .8f, .45f));
        var text = "AVIZijé";
        for (var i = 0; i < text.Length; i++)
        {
            var glyph = face.GlyphFor(text[i]); var origin = new Vector2(5.25f + 32 * i, 68);
            actual.Canvas.Save();
            actual.Canvas.Transform(Matrix3x2.CreateScale(1, -1) * Matrix3x2.CreateTranslation(origin));
            actual.Canvas.FillPath(GlyphOutlines.GetHinted(face, glyph, prepared), FillRule.NonZero, paint);
            actual.Canvas.Restore();
            expected.Canvas.DrawGlyphs(face, pixelsPerEm, [glyph], [origin], paint);
        }
        var a = actual.ReadPixels(0, 0, 240, 100); var e = expected.ReadPixels(0, 0, 240, 100);
        var sum = 0d; var over40 = 0; var maximum = 0;
        for (var i = 3; i < a.Length; i += 4)
        {
            var difference = Math.Abs(a[i] - e[i]); sum += difference;
            if (difference > 40) over40++; maximum = Math.Max(maximum, difference);
        }
        output.WriteLine($"{pixelsPerEm} ppem: mean alpha {sum / 24000:F4}, max {maximum}, >40 {over40}");
        Assert.True(sum / 24000 < .4 && over40 < 24, $"Hinted outline differs: mean {sum / 24000:F4}, max {maximum}, >40 {over40}.");
    }

    [Theory]
    [InlineData(12f)]
    [InlineData(16f)]
    [InlineData(17.6f)]
    [InlineData(32f)]
    [InlineData(35.2f)]
    [InlineData(48f)]
    public void BundledLatinFontProgramsExecuteWithinTheHostLimits(float pixelsPerEm)
    {
        var face = FontFace.Parse(File.ReadAllBytes(Path.Combine(RepoPaths.Tests, "fonts", "SourceSans3-Regular.ttf")))!;
        var prepared = FontHinting.Prepare(face, pixelsPerEm);
        foreach (var character in "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789!?.,")
        {
            var error = Record.Exception(() => GlyphOutlines.GetHinted(face, face.GlyphFor(character), prepared));
            Assert.True(error is null, $"{character} at {pixelsPerEm} ppem: {error}");
        }
    }

    [Fact]
    public void WordsAreSignedAndFixedPointMathPreservesOperandOrder()
    {
        var vm = Machine();
        vm.Execute([0xB9, 0xFF, 0x80, 0, 64, 0x62]); // -128 / 64 in 26.6 = -128.
        Assert.Equal(-128, vm.Pop());
        vm.Execute([0xB1, 7, 3, 0x61]);
        Assert.Equal(4, vm.Pop());
    }

    [Fact]
    public void ConditionalSkippingDoesNotInterpretPushDataAsInstructions()
    {
        var vm = Machine();
        vm.Execute([0xB0, 0, 0x58, 0xB1, 0x58, 0x59, 0x1B, 0xB0, 42, 0x59]);
        Assert.Equal(42, vm.Pop());
        Assert.Equal(0, vm.StackCount);
    }

    [Fact]
    public void FunctionsShareTheOperandStackAndPreparedStorage()
    {
        var vm = Machine();
        vm.Execute([0xB0, 2, 0x2C, 0xB0, 1, 0x60, 0x2D], definitions: true);
        vm.Execute([0xB2, 4, 3, 2, 0x2A]); // Apply function 2 three times to 4.
        Assert.Equal(7, vm.Pop());
        vm.Execute([0xB1, 1, 99, 0x42]);
        vm.StartGlyph(new([], []), vm.Graphics.Copy());
        vm.Execute([0xB0, 1, 0x43]);
        Assert.Equal(99, vm.Pop());
    }

    [Theory]
    [InlineData(new byte[] { 0x21 })] // Underflow.
    [InlineData(new byte[] { 0xB8, 0 })] // Truncated word.
    [InlineData(new byte[] { 0xB0, 255, 0x43 })] // Storage bounds.
    [InlineData(new byte[] { 0xB0, 1, 0x58 })] // Unterminated IF, even if its condition is true.
    [InlineData(new byte[] { 0xB0, 1, 0x2B })] // Undefined function.
    [InlineData(new byte[] { 0xB1, 1, 0, 0x62 })] // Division by zero.
    [InlineData(new byte[] { 0xB8, 0xFF, 0xFE, 0x1C })] // Jump into PUSH payload.
    public void MalformedProgramsAreRejected(byte[] code) => Assert.Throws<InvalidDataException>(() => Machine().Execute(code));

    [Fact]
    public void LoopsAndRecursionCannotBypassTheExecutionLimit()
    {
        var vm = Machine();
        var error = Assert.Throws<InvalidDataException>(() => vm.Execute([0xB8, 0xFF, 0xFD, 0x1C]));
        Assert.Contains("budget", error.Message);
        vm = Machine();
        vm.Execute([0xB0, 0, 0x2C, 0xB0, 0, 0x2B, 0x2D], definitions: true);
        error = Assert.Throws<InvalidDataException>(() => vm.Execute([0xB0, 0, 0x2B]));
        Assert.Contains("depth", error.Message);
    }

    [Fact]
    public void DirectGridFitInterpolatesOnlyTheUntouchedContourPoints()
    {
        var vm = Machine();
        var zone = new TrueTypeMachine.Zone([new(0, 20), new(20, 40), new(40, 100), new(60, 120)], [3]);
        vm.StartGlyph(zone, new());
        vm.Execute([0x00, 0xB0, 0, 0x2F, 0xB0, 3, 0x2F, 0x30]);
        Assert.Equal(0, zone.Current[0].Y);
        Assert.Equal(128, zone.Current[3].Y);
        Assert.Equal(26, zone.Current[1].Y);
        Assert.Equal(102, zone.Current[2].Y);
        Assert.Equal(20, zone.Original[0].Y);
        Assert.False(zone.TouchedY[1]);
        Assert.Equal(new Vector2(20, 26), zone.Current[1]);
    }

    [Fact]
    public void DistanceInstructionsPreserveDirectionAndChooseOriginalOrFittedCoordinates()
    {
        var vm = Machine();
        var zone = new TrueTypeMachine.Zone([new(20, 0), new(100, 0)], [1]);
        vm.StartGlyph(zone, new());
        vm.Execute([0xB0, 0, 0x2F, 0xB0, 1, 0x2F]); // Fit x=20 to 0 and x=100 to 128.
        vm.Execute([0xB1, 0, 1, 0x49]); Assert.Equal(-128, vm.Pop());
        vm.Execute([0xB1, 0, 1, 0x4A]); Assert.Equal(-80, vm.Pop());
        vm.Execute([0xB1, 1, 0, 0x49]); Assert.Equal(128, vm.Pop());
    }

    [Fact]
    public void PhantomPointsAreNotIncludedInContourInterpolation()
    {
        var vm = Machine();
        var zone = new TrueTypeMachine.Zone([new(0, 20), new(20, 40), new(40, 100), new(0, 0), new(200, 0)], [2]);
        vm.StartGlyph(zone, new());
        vm.Execute([0x00, 0xB0, 0, 0x2F, 0x30]);
        Assert.Equal(new Vector2(0, 0), zone.Current[3]);
        Assert.Equal(new Vector2(200, 0), zone.Current[4]);
    }

    [Fact]
    public void DeltaExceptionsOnlyMovePointsAtTheRequestedPixelSize()
    {
        var vm = Machine(); var zone = new TrueTypeMachine.Zone([new(0, 0)], [0]);
        vm.StartGlyph(zone, new());
        vm.Execute([0xB2, 0x78, 0, 1, 0x5D]); // 9 + 7 ppem, +1/8 pixel.
        Assert.Equal(8, zone.Current[0].X);
        vm.Execute([0xB2, 0x68, 0, 1, 0x5D]);
        Assert.Equal(8, zone.Current[0].X);
    }

    [Fact]
    public void ASharedCompositeBudgetCannotBeResetByStartingAnotherGlyph()
    {
        var vm = Machine(); vm.StartGlyph(new([], []), new(), allowance: 2);
        Assert.Throws<InvalidDataException>(() => vm.Execute([0xB0, 1, 0xB0, 2]));
        Assert.Throws<InvalidDataException>(() => vm.StartGlyph(new([], []), new(), -1));
    }

    [Fact]
    public void StackCapacityIsEnforcedBeforeWritingPastItsEnd()
    {
        var code = Enumerable.Repeat(new byte[] { 0xB0, 1 }, TrueTypeMachine.StackLimit + 1).SelectMany(b => b).ToArray();
        var error = Assert.Throws<InvalidDataException>(() => Machine().Execute(code));
        Assert.Contains("Stack overflow", error.Message);
    }

    [Fact]
    public void CurveFlagOperationsUseTheSelectedZone()
    {
        var vm = Machine(); var glyph = new TrueTypeMachine.Zone([new(0, 0)], [0]);
        vm.StartGlyph(glyph, new());
        vm.Execute([0xB0, 0, 0x13, 0xB0, 3, 0x80]); // zp0=twilight, flip point 3.
        Assert.True(vm.Twilight.OnCurve[3]); Assert.False(glyph.OnCurve[0]);
        vm.Execute([0xB1, 2, 4, 0x81]);
        Assert.True(vm.Twilight.OnCurve[2]); Assert.True(vm.Twilight.OnCurve[4]);
        vm.Execute([0xB1, 2, 4, 0x82]);
        Assert.False(vm.Twilight.OnCurve[3]); Assert.False(glyph.OnCurve[0]);
    }

    [Fact]
    public void FontAndCvtProgramsCanDefineForwardCompatibleInstructions()
    {
        var vm = Machine();
        vm.Execute([0xB0, 0x91, 0x89, 0xB0, 42, 0x2D], definitions: true);
        vm.StartGlyph(new([], []), vm.Graphics.Copy());
        vm.Execute([0x91]); Assert.Equal(42, vm.Pop());
        Assert.Throws<InvalidDataException>(() => vm.Execute([0xB0, 0x92, 0x89, 0x2D]));
        Assert.Throws<InvalidDataException>(() => Machine().Execute([0xB0, 0x91, 0x89, 0xB0, 0, 0x2C, 0x2D, 0x2D], definitions: true));
    }

    [Fact]
    public void LineIntersectionMovesBothCoordinatesIndependentOfFreedomVector()
    {
        var vm = Machine();
        var glyph = new TrueTypeMachine.Zone([new(0, 0), new(128, 128), new(0, 128), new(128, 0), new(0, 0)], [4]);
        vm.StartGlyph(glyph, new());
        vm.Execute([0xB4, 4, 3, 2, 1, 0, 0x0F]);
        Assert.Equal(new Vector2(64, 64), glyph.Current[4]);
        Assert.True(glyph.TouchedX[4]); Assert.True(glyph.TouchedY[4]);
    }

    [Fact]
    public void PreparedStateIsIsolatedFromEveryGlyph()
    {
        var prepared = Machine(); prepared.Execute([0xB1, 1, 99, 0x42]);
        var first = prepared.Fork(); first.StartGlyph(new([], []), prepared.Graphics);
        first.Execute([0xB1, 1, 42, 0x42, 0xB1, 0, 0, 0x44]);
        var second = prepared.Fork(); second.StartGlyph(new([], []), prepared.Graphics);
        Assert.Equal(99, second.Storage[1]); Assert.NotEqual(0, second.Cvt[0]);
        Assert.Equal(99, prepared.Storage[1]); Assert.NotEqual(0, prepared.Cvt[0]);
    }
}
