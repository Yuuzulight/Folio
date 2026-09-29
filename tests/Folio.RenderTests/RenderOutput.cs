namespace Folio.RenderTests;

/// <summary>
/// Where image tests leave <c>.actual.png</c>, <c>.expected.png</c> and <c>.diff.png</c> for anything that
/// failed or changed. Gitignored; CI uploads it.
/// </summary>
public static class RenderOutput
{
    public static string Root { get; } = Path.Combine(RepoPaths.Tests, "render-output");

    private static readonly string[] Suffixes = [".actual.png", ".expected.png", ".diff.png"];

    /// <param name="basePath">Output path without suffix, e.g. <c>render-output/goldens/area/name</c>.</param>
    public static void Write(string basePath, PixelBuffer actual, PixelBuffer? expected = null, PixelBuffer? diff = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(basePath)!);
        actual.SavePng(basePath + ".actual.png");
        expected?.SavePng(basePath + ".expected.png");
        diff?.SavePng(basePath + ".diff.png");
    }

    public static void Clear(string basePath)
    {
        if (!Directory.Exists(Path.GetDirectoryName(basePath)))
            return;
        foreach (var suffix in Suffixes)
            File.Delete(basePath + suffix);
    }
}
