namespace Folio.RenderTests;

/// <summary>Where failing image tests leave their images; CI uploads this folder when a job fails.</summary>
public static class RenderOutput
{
    public static string Root { get; } = Path.Combine(AppContext.BaseDirectory, "render-output");

    /// <summary>Writes <c>name.expected.png</c>, <c>name.actual.png</c> and <c>name.diff.png</c>.</summary>
    public static void WriteFailure(string name, PixelBuffer expected, PixelBuffer actual, ComparisonResult result)
    {
        var basePath = Path.Combine(Root, name);
        Directory.CreateDirectory(Path.GetDirectoryName(basePath)!);
        expected.SavePng(basePath + ".expected.png");
        actual.SavePng(basePath + ".actual.png");
        result.Diff.SavePng(basePath + ".diff.png");
    }
}
