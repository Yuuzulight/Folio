using System.Security.Cryptography;

namespace Folio.RenderTests;

/// <summary>Bundled third-party fonts stay exactly the files tests/fonts/LICENSES.md credits.</summary>
public class FontPinTests
{
    [Fact]
    public void BundledFontsMatchTheirPins()
    {
        var folder = Path.Combine(RepoPaths.Tests, "fonts");
        var pins = File.ReadAllLines(Path.Combine(folder, "SHA256SUMS")).Where(l => l.Length > 0).ToList();

        Assert.NotEmpty(pins);
        foreach (var pin in pins)
        {
            var (hash, file) = (pin[..64], pin[66..]);
            Assert.Equal(hash, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(folder, file)))));
        }
        // Every font file but Folio's own generated one is pinned, so none can be added or swapped unnoticed.
        var pinned = pins.Select(p => p[66..]).ToHashSet();
        foreach (var font in Directory.EnumerateFiles(folder).Select(Path.GetFileName).Where(f => f!.EndsWith(".ttf") || f.EndsWith(".otf")))
            Assert.True(font == "FolioBox.ttf" || pinned.Contains(font!), $"{font} is not pinned in SHA256SUMS");
    }
}
