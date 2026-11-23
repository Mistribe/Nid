using System.Runtime.CompilerServices;

namespace Nid.TestSupport;

/// <summary>A text file next to the tests, compared line by line; <c>NID_UPDATE_SNAPSHOTS=1</c> rewrites it.</summary>
public static class Snapshot
{
    public static void Match(string file, IReadOnlyList<string> lines, [CallerFilePath] string here = "")
    {
        var path = Path.Combine(Path.GetDirectoryName(here)!, "Snapshots", file);
        var actual = string.Join('\n', lines) + "\n";
        if (Environment.GetEnvironmentVariable("NID_UPDATE_SNAPSHOTS") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, actual);
        }
        Assert.Equal(File.ReadAllText(path).ReplaceLineEndings("\n"), actual);
    }
}
