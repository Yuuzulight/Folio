namespace Folio.Scripting.Tests;

internal static class ContentHostTestPaths
{
    // Every process integration test must exercise the selected published host, not silently test a developer DLL.
    internal static string Resolve()
    {
        var configured = Environment.GetEnvironmentVariable("FOLIO_CONTENT_HOST");
        if (!string.IsNullOrWhiteSpace(configured)) return Path.GetFullPath(configured);
        var configuration = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)).Parent!.Name;
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "../../../../../tools/Folio.ContentHost/bin", configuration, "net10.0/Folio.ContentHost.dll"));
    }
}
