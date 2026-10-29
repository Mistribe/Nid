using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Nid.Core.Contracts;
using Nid.Host.IntegrationTests.Import;
using Nid.TestSupport;
using NodaTime;

namespace Nid.Host.IntegrationTests.Demo;

/// <summary>
/// The demo household of deploy/local/demo: alice's file and bob's import as they are, without an error or a warning, so that a
/// local stack can be filled for a visual review whatever the format has become.
/// </summary>
public class DemoFileTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task The_demo_files_import_without_an_issue()
    {
        await using var app = await NidApp.StartAsync(fixture, services => services.AddSingleton<IClock>(new DemoToday()));
        var client = new ImportClient(app);

        await ImportAsync(client, app.AdultA, "alice.nid.json", new Dictionary<string, Guid> { ["adult-1"] = app.AdultA.Value, ["adult-2"] = app.AdultB.Value });
        await ImportAsync(client, app.AdultB, "bob.nid.json", new Dictionary<string, Guid> { ["adult-1"] = app.AdultB.Value });
    }

    private static async Task ImportAsync(ImportClient client, PersonId actor, string file, object adults)
    {
        var validated = await client.RunAsync(actor, adults, Sections(file));
        Assert.True(validated.Report!.Issues.Count == 0, string.Join("; ", validated.Report.Issues.Select(i => $"{i.Severity} {i.Code} at {i.Path}: {i.Message}")));
        var committed = await client.CommitRawAsync(actor, validated.Id);
        Assert.True(committed.IsSuccessStatusCode, await committed.Content.ReadAsStringAsync());
    }

    private static Dictionary<string, string> Sections(string file, [CallerFilePath] string here = "")
    {
        var path = Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "..", "deploy", "local", "demo", file);
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.EnumerateObject()
            .Where(p => p.Value.ValueKind == JsonValueKind.Array)
            .ToDictionary(p => p.Name, p => p.Value.GetRawText());
    }

    /// <summary>The day after the files' last fact (generate.mjs, LAST): no valuation or goal entry is in the future.</summary>
    private sealed class DemoToday : IClock
    {
        public Instant GetCurrentInstant() => new LocalDate(2026, 10, 29).At(new LocalTime(12, 0)).InUtc().ToInstant();
    }
}
