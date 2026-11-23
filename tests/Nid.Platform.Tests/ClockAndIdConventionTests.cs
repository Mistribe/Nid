using System.Text.RegularExpressions;

namespace Nid.Platform.Tests;

/// <summary>
/// #520, <c>docs/architecture.md</c> §6: ids are UUIDv7 (<c>Guid.CreateVersion7()</c>) and the time comes from NodaTime's
/// <c>IClock</c>, so a test can set it. Reads <c>src/**/*.cs</c>: a <c>Guid.NewGuid()</c> or a read of the system clock fails here
/// unless its file is allowed below, with the reason. CI runs this class on every pull request.
/// </summary>
public partial class ClockAndIdConventionTests
{
    /// <summary>Paths under <c>src/</c>, with <c>/</c>; a folder ends with <c>/</c>.</summary>
    private static readonly Dictionary<string, string> Allowed = new()
    {
        // Random names, never ids: a temporary file, a dummy password, a tool call or a JSON-RPC request id.
        ["Platform/Nid.Platform.Infrastructure/Assets/FileAssetStore.cs"] = "temporary file name",
        ["Documents/Nid.Documents.Infrastructure/Storage/FileDocumentStorage.cs"] = "temporary file name",
        ["Hosting/Nid.Host/Auth/LocalPasswordHasher.cs"] = "a dummy password hashed against timing attacks",
        // ASP.NET's AuthenticationProperties.ExpiresUtc is a DateTimeOffset, and the cookie handler reads the system clock itself.
        ["Hosting/Nid.Host/Auth/AuthenticationSetup.cs"] = "cookie expiry",
        ["Hosting/Nid.Host/Auth/LocalSignIn.cs"] = "cookie expiry",
        ["Hosting/Nid.Host/Demo/DemoEndpoints.cs"] = "cookie expiry",
        // Left to the sessions working on the assistant and the model workers (#520's notes).
        ["Ai/"] = "the AI module",
        ["Hosting/Nid.Host/Assistant/"] = "the assistant",
        ["Hosting/Nid.Host/Jobs/ModelWorkLoop.cs"] = "the model workers' loop",
    };

    [GeneratedRegex(@"\bGuid\.NewGuid\(|\bDateTime(Offset)?\.(Now|UtcNow|Today)\b")]
    private static partial Regex Forbidden();

    [Fact]
    public void Ids_and_the_time_come_from_UUIDv7_and_the_clock()
    {
        var src = Path.Combine(Vectors.RepositoryRoot(), "src");
        var found = (from path in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
                     let relative = Path.GetRelativePath(src, path).Replace('\\', '/')
                     where !relative.Contains("/obj/", StringComparison.Ordinal) && !relative.Contains("/bin/", StringComparison.Ordinal)
                     where !Allowed.Keys.Any(a => a.EndsWith('/') ? relative.StartsWith(a, StringComparison.Ordinal) : relative == a)
                     from line in File.ReadLines(path).Select((text, i) => (text, number: i + 1))
                     where Forbidden().IsMatch(line.text)
                     select $"{relative}:{line.number}: {line.text.Trim()}").ToArray();
        Assert.True(found.Length == 0,
            "Use Guid.CreateVersion7() for an id and IClock for the time (architecture.md §6), or allow the file in "
            + $"{nameof(ClockAndIdConventionTests)} with its reason:{Environment.NewLine}  {string.Join($"{Environment.NewLine}  ", found)}");
    }

    [Fact]
    public void Every_allowed_path_still_exists()
    {
        var src = Path.Combine(Vectors.RepositoryRoot(), "src");
        Assert.All(Allowed.Keys, a => Assert.True(a.EndsWith('/') ? Directory.Exists(Path.Combine(src, a)) : File.Exists(Path.Combine(src, a)), a));
    }

    [Theory]
    [InlineData("var id = Guid.NewGuid();", true)]
    [InlineData("var at = DateTimeOffset.UtcNow;", true)]
    [InlineData("var at = DateTime.Now;", true)]
    [InlineData("var today = DateTime.Today;", true)]
    [InlineData("var id = Guid.CreateVersion7();", false)]
    [InlineData("var at = clock.GetCurrentInstant();", false)]
    [InlineData("DateTimeOffset.Parse(text)", false)]
    public void The_pattern_finds_what_it_bans(string line, bool banned) => Assert.Equal(banned, Forbidden().IsMatch(line));
}
