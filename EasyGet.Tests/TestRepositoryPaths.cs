namespace EasyGet.Tests;

internal static class TestRepositoryPaths
{
    public static string Root { get; } = FindRepositoryRoot();

    public static string GetRootPath(string relativePath)
    {
        var candidate = Path.Combine(Root, relativePath);
        if (File.Exists(candidate) || Directory.Exists(candidate))
            return candidate;

        throw new FileNotFoundException($"Could not find {relativePath} under repository root.");
    }

    public static string GetViewPath(string fileName)
        => GetRootPath(Path.Combine("Views", fileName));

    public static string GetThemePath(string fileName)
        => GetRootPath(Path.Combine("Themes", fileName));

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "EasyGet.csproj")))
                return directory.FullName;

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not find repository root from test output directory.");
    }
}
