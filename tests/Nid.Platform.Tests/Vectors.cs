namespace Nid.Platform.Tests;

/// <summary>Where the vectors are: copied next to the test assembly, and in the repository for regeneration.</summary>
internal static class Vectors
{
    public static string Copied(params string[] parts)
        => Path.Combine([AppContext.BaseDirectory, "vectors", "rfc8785", .. parts]);

    /// <summary>The source directory, found by walking up to <c>Nid.slnx</c>. Only regeneration writes here.</summary>
    public static string InRepository(params string[] parts)
        => Path.Combine([RepositoryRoot(), "tests", "vectors", "rfc8785", .. parts]);

    /// <summary>The import fixtures the browser shares (<c>web/src/features/import/prepare.test.ts</c>).</summary>
    public static string CopiedImport(params string[] parts)
        => Path.Combine([AppContext.BaseDirectory, "vectors", "import", .. parts]);

    public static string InRepositoryImport(params string[] parts)
        => Path.Combine([RepositoryRoot(), "tests", "vectors", "import", .. parts]);

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Nid.slnx")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new InvalidOperationException("Nid.slnx not found above the test output directory.");
    }
}
