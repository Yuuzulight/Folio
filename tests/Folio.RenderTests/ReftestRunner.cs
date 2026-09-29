namespace Folio.RenderTests;

/// <summary>
/// Renders a test page and its reference page (<c>tests/reftests/&lt;module&gt;/&lt;name&gt;.html</c> and
/// <c>&lt;name&gt;-ref.html</c>) and requires identical pixels.
/// </summary>
public static class ReftestRunner
{
    public static string Root { get; } = Path.Combine(AppContext.BaseDirectory, "reftests");

    /// <param name="name">Path under <c>tests/reftests</c> without extension, e.g. <c>css-backgrounds/background-color-001</c>.</param>
    public static ComparisonResult Run(string name)
    {
        var actual = Render(Path.Combine(Root, name + ".html"));
        var expected = Render(Path.Combine(Root, name + "-ref.html"));
        var result = ImageComparer.Compare(expected, actual, Tolerance.Exact);
        if (!result.Passed)
            RenderOutput.WriteFailure(name, expected, actual, result);
        return result;
    }

    // Becomes a call to Folio.Skia's headless renderer once it exists.
    private static PixelBuffer Render(string htmlPath) =>
        throw new NotImplementedException($"The headless renderer does not exist yet: {htmlPath}");
}
