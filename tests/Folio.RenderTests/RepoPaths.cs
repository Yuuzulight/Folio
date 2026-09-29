namespace Folio.RenderTests;

/// <summary>Image tests read their inputs from the source tree so the approve command can update them in place.</summary>
public static class RepoPaths
{
    public static string Root { get; } = FindRoot();

    public static string Tests => Path.Combine(Root, "tests");

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Folio.slnx")))
                return dir.FullName;
        }
        throw new DirectoryNotFoundException($"No Folio.slnx above {AppContext.BaseDirectory}.");
    }
}
