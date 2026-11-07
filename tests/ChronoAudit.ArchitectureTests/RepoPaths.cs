namespace ChronoAudit.ArchitectureTests;

internal static class RepoPaths
{
    public static string Root { get; } = FindRoot();

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ChronoAudit.sln")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            $"Repository root not found: no parent folder of '{AppContext.BaseDirectory}' contains ChronoAudit.sln.");
    }
}
