namespace Nid.Ai.Tests;

/// <summary>Where the vectors are: copied next to the test assembly, and in the repository for regeneration.</summary>
internal static class Vectors
{
    public static string Copied(params string[] parts)
        => Path.Combine([AppContext.BaseDirectory, "vectors", .. parts]);

    /// <summary>The repository root, found by walking up to <c>Nid.slnx</c>. Only regeneration writes under it.</summary>
    public static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Nid.slnx")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new InvalidOperationException("Nid.slnx not found above the test output directory.");
    }
}
