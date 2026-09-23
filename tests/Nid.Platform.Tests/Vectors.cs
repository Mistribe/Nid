namespace Nid.Platform.Tests;

/// <summary>Where the vectors are: copied next to the test assembly, and in the repository for regeneration.</summary>
internal static class Vectors
{
    public static string Copied(params string[] parts)
        => Path.Combine([AppContext.BaseDirectory, "vectors", "rfc8785", .. parts]);

    /// <summary>The source directory, found by walking up to <c>Nid.slnx</c>. Only regeneration writes here.</summary>
    public static string InRepository(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Nid.slnx")))
            directory = directory.Parent;

        return directory is null
            ? throw new InvalidOperationException("Nid.slnx not found above the test output directory.")
            : Path.Combine([directory.FullName, "tests", "vectors", "rfc8785", .. parts]);
    }
}
