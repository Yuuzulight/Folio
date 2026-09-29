using Folio.Paint;

namespace Folio.Tests;

public class LayeringTests
{
    // The engine stays free of native libraries and UI frameworks (docs/architecture.md, Boundaries).
    private static readonly string[] ForbiddenPrefixes =
    [
        "SkiaSharp",
        "HarfBuzzSharp",
        "System.Windows.Forms",
        "Folio.Skia",
        "Folio.WinForms",
    ];

    [Fact]
    public void EngineReferencesNoNativeOrUiAssembly()
    {
        var referenced = typeof(ICanvas).Assembly.GetReferencedAssemblies().Select(a => a.Name!);

        var violations = referenced
            .Where(name => ForbiddenPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        Assert.Empty(violations);
    }

    // The compiler drops references the code does not use, so also catch a package added to the
    // engine project: this test project references only the engine, so its output shows the closure.
    [Fact]
    public void EngineDependencyClosureHasNoNativeOrUiAssembly()
    {
        var violations = Directory.EnumerateFiles(AppContext.BaseDirectory, "*.dll")
            .Select(Path.GetFileName)
            .Where(name => ForbiddenPrefixes.Any(prefix => name!.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        Assert.Empty(violations);
    }
}
