namespace Folio.RenderTests;

/// <summary>
/// Renders a test page and its reference page (<c>tests/reftests/&lt;module&gt;/&lt;name&gt;.html</c> and
/// <c>&lt;name&gt;-ref.html</c>) and requires identical pixels.
/// </summary>
public static class ReftestRunner
{
    public static string Root { get; } = Path.Combine(RepoPaths.Tests, "reftests");

    /// <param name="name">Path under <c>tests/reftests</c> without extension, e.g. <c>css-backgrounds/background-color-001</c>.</param>
    public static ComparisonResult Run(string name)
    {
        var outputBase = Path.Combine(RenderOutput.Root, "reftests", name);
        RenderOutput.Clear(outputBase);
        var actual = TestRenderer.Render(Path.Combine(Root, name + ".html"));
        var expected = TestRenderer.Render(Path.Combine(Root, name + "-ref.html"));
        var result = ImageComparer.Compare(expected, actual, Tolerance.Exact);
        if (!result.Passed)
            RenderOutput.Write(outputBase, actual, expected, result.Diff);
        return result;
    }
}
