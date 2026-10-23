using Microsoft.Extensions.DependencyInjection;
using Nid.Core.Contracts;
using Nid.Goals.Application;
using Nid.Platform.Contracts;
using Nid.Subscriptions.IntegrationTests.Export;
using Nid.TestSupport;
using NodaTime;
using static Nid.Subscriptions.IntegrationTests.Import.EnvelopesImportTests;

namespace Nid.Subscriptions.IntegrationTests.Import;

/// <summary>Savings goals spec D16: the <c>goals</c> section, asserted through the stores and the export.</summary>
public class GoalsImportTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string OwnerA = """[{"person":"adult-1","share_bp":10000}]""";

    private const string GoalEnvelopes = $$"""
        [{"id":"env-a","name":"Livret","kind":"savings","currency":"EUR","ownership":{{OwnerA}}},
         {"id":"env-b","name":"PEA","kind":"pea","currency":"EUR","ownership":{{OwnerA}},"visibility":"private"}]
        """;

    /// <summary>A savings goal on two envelopes with a target date in the past, a closed emergency fund, and a free goal with entries.</summary>
    private const string Trip = $$"""
        {"id":"goal-trip","name":"Voyage","kind":"savings","target_amount":"5000.00","target_date":"2024-01-01","note":"Japon",
         "ownership":{{OwnerA}},"funding":[{"envelope":"env-a","share_bp":6000},{"envelope":"env-b","share_bp":4000}]}
        """;

    private const string Fund = $$"""
        {"id":"goal-fund","name":"Précaution","kind":"emergency_fund","months":3,"closed":true,"ownership":{{OwnerA}},
         "funding":[{"envelope":"env-a","share_bp":4000}]}
        """;

    private const string Free = $$"""
        {"id":"goal-free","name":"Vélo","kind":"savings","target_amount":800,"ownership":{{OwnerA}},
         "entries":[{"on":"2025-01-01","amount":"100"},{"on":"2025-06-01","amount":250}]}
        """;

    private static Dictionary<string, string> GoalsFile(params string[] goals) => new()
    {
        ["persons"] = GoldenFile.Persons,
        ["envelopes"] = GoalEnvelopes,
        ["goals"] = "[" + string.Join(",", goals) + "]",
    };

    private static async Task<T> StoredAsync<T>(NidApp app, Func<IGoalStore, IItemRegistry, Task<T>> read)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return await read(scope.ServiceProvider.GetRequiredService<IGoalStore>(), scope.ServiceProvider.GetRequiredService<IItemRegistry>());
    }

    private static async Task<ItemId> IdOfAsync(NidApp app, string fileId)
        => (await StoredAsync(app, (store, _) => store.FindImportedAsync([fileId], default)))[fileId].Single().Item;

    private static async Task<uint> VersionAsync(NidApp app, ItemId goal)
        => (await StoredAsync(app, (store, _) => store.FindAsync(goal, default)))!.Version;

    private static IEnumerable<(string Code, string Path)> ErrorsOf(SessionResponse session)
        => session.Report!.Issues.Where(i => i.Severity == "error").Select(i => (i.Code, i.Path));

    [Fact]
    public async Task Goals_links_entries_and_closing_are_created()
    {
        await using var app = await NidApp.StartAsync(fixture);

        var committed = await CommitAsync(new ImportClient(app), app, app.AdultA, GoalsFile(Trip, Fund, Free));

        Assert.Equal(new CountsResponse(3, 0, 0), committed.Report!.Sections["goals"]);
        var trip = await IdOfAsync(app, "goal-trip");
        var (goal, item, funding) = await StoredAsync(app, async (store, items) =>
            ((await store.FindAsync(trip, default))!, (await items.FindAsync(trip, default))!, await store.FundingOfAsync([trip], default)));
        Assert.Equal((5000m, new LocalDate(2024, 1, 1), "Japon"), (goal.TargetAmount, goal.TargetDate, goal.Note));
        Assert.Equal("Voyage", item.DisplayName);
        Assert.Equal([4000, 6000], funding.Select(f => f.ShareBp).Order());

        var fund = await IdOfAsync(app, "goal-fund");
        var (fundGoal, fundItem) = await StoredAsync(app, async (store, items) => ((await store.FindAsync(fund, default))!, (await items.FindAsync(fund, default))!));
        Assert.Equal(3, fundGoal.Months);
        Assert.NotNull(fundItem.ArchivedAt);

        var free = await IdOfAsync(app, "goal-free");
        var entries = await StoredAsync(app, (store, _) => store.EntriesOfAsync([free], default));
        Assert.Equal([(new LocalDate(2025, 1, 1), 100m), (new LocalDate(2025, 6, 1), 250m)], entries.Select(e => (e.On, e.Amount)));
    }

    [Fact]
    public async Task The_same_file_again_is_unchanged_and_replace_updates_in_place()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var client = new ImportClient(app);
        await CommitAsync(client, app, app.AdultA, GoalsFile(Trip, Fund, Free));
        var free = await IdOfAsync(app, "goal-free");

        var again = await CommitAsync(client, app, app.AdultA, GoalsFile(Trip, Fund, Free));

        Assert.Equal(new CountsResponse(0, 3, 0), again.Report!.Sections["goals"]);
        var changed = GoalsFile(Trip, Fund.Replace("\"closed\":true,", ""),
            Free.Replace("\"name\":\"Vélo\"", "\"name\":\"Vélo électrique\"").Replace("{\"on\":\"2025-01-01\",\"amount\":\"100\"},", ""));

        var conflict = await ValidateAsync(client, app, app.AdultA, changed);

        Assert.Equal([(ImportCodes.Conflict, "goals[1]"), (ImportCodes.Conflict, "goals[2]")], ErrorsOf(conflict));
        await client.CancelRawAsync(app.AdultA, conflict.Id);

        var replaced = await CommitAsync(client, app, app.AdultA, changed, replace: true);

        Assert.Equal(new CountsResponse(0, 1, 0, 2), replaced.Report!.Sections["goals"]);
        Assert.Equal(free, await IdOfAsync(app, "goal-free"));
        var (item, entries, fundItem) = await StoredAsync(app, async (store, items) =>
            ((await items.FindAsync(free, default))!, await store.EntriesOfAsync([free], default), (await items.FindAsync(await IdOfAsync(app, "goal-fund"), default))!));
        Assert.Equal("Vélo électrique", item.DisplayName);
        Assert.Equal([new LocalDate(2025, 6, 1)], entries.Select(e => e.On));
        Assert.Null(fundItem.ArchivedAt);
    }

    [Fact]
    public async Task Import_refuses_unknown_envelope()
    {
        await using var app = await NidApp.StartAsync(fixture);

        var session = await ValidateAsync(new ImportClient(app), app, app.AdultA, GoalsFile(Trip.Replace("env-b", "env-nope")));

        Assert.Equal("invalid", session.State);
        Assert.Equal([(ImportCodes.UnresolvedReference, "goals[0].funding[1].envelope")], ErrorsOf(session));
    }

    /// <summary>The API's amount rule (GoalCheck.Amount): too precise or too large for the column, at the file member.</summary>
    [Fact]
    public async Task Import_refuses_an_amount_the_column_cannot_hold()
    {
        await using var app = await NidApp.StartAsync(fixture);

        var client = new ImportClient(app);
        var session = await client.StartAsync(app.AdultA, Adults(app));

        var batch = await client.PutAsync(app.AdultA, session.Id, "goals", 0,
            "[" + Free.Replace("800", "\"12345678901234567\"").Replace("\"100\"", "\"10.123456\"") + "]");

        Assert.False(batch.Accepted);
        Assert.Equal([(Nid.Goals.Contracts.GoalsCodes.InvalidTarget, "goals[0].target_amount"), (Nid.Goals.Contracts.GoalsCodes.InvalidTarget, "goals[0].entries[0].amount")],
            batch.Issues.Select(i => (i.Code, i.Path)));
    }

    /// <summary>D10: a state the interface allows (another adult's goal the author did not see) is restored, not refused; reads flag it.</summary>
    [Fact]
    public async Task Import_restores_an_over_allocated_envelope()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var client = new ImportClient(app);
        var other = Fund.Replace("\"closed\":true,", "").Replace("4000", "5000"); // 6000 + 5000 on env-a, both open

        var committed = await CommitAsync(client, app, app.AdultA, GoalsFile(Trip, other));

        Assert.Equal(new CountsResponse(2, 0, 0), committed.Report!.Sections["goals"]);
        var raw = await app.GetRawAsync(app.AdultA, $"/api/v1/goals/{(await IdOfAsync(app, "goal-trip")).Value}?scope=me");
        Assert.Contains("over_allocated", raw);
        // A share over the envelope's whole is no share at all.
        var invalid = await ValidateAsync(client, app, app.AdultA, GoalsFile(Trip.Replace("6000", "10001")));
        Assert.Equal([(Nid.Goals.Contracts.GoalsCodes.InvalidShare, "goals[0].funding[0].share_bp")], ErrorsOf(invalid));
    }

    [Fact]
    public async Task Export_leaves_out_links_to_other_adults_private_envelopes_and_their_private_goals()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var shared = await app.NewEnvelopeAsync("PEA", app.AdultA, Visibility.Shared);
        var hidden = await app.NewEnvelopeAsync("PEA de B", app.AdultB, Visibility.Private);
        var goal = await app.NewGoalAsync("Maison", app.AdultA, funding: [(shared, 5000)], owners: [(app.AdultA, 5000), (app.AdultB, 5000)]);
        Assert.True((await app.RawAsync(app.AdultB, HttpMethod.Put, $"/api/v1/goals/{goal.Value}", new
        {
            name = "Maison", targetAmount = "10000", note = (string?)null, targetDate = (string?)null, version = await VersionAsync(app, goal),
            funding = new[] { new { envelopeId = shared.Value, shareBp = 5000 }, new { envelopeId = hidden.Value, shareBp = 3000 } },
        })).IsSuccessStatusCode);
        await app.NewGoalAsync("ZZSECRETGOAL", app.AdultB, Visibility.Private, [(hidden, 7000)]);

        var export = await new ExportClient(app).ExportAllAsync(app.AdultA);

        var goals = export["goals"];
        Assert.Equal(["Maison"], goals.Select(g => g.GetProperty("name").GetString()));
        Assert.Single(goals[0].GetProperty("funding").EnumerateArray());
        var text = System.Text.Json.JsonSerializer.Serialize(export);
        Assert.DoesNotContain(hidden.Value.ToString(), text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ZZSECRETGOAL", text, StringComparison.Ordinal);
        Assert.DoesNotContain("PEA de B", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Replace_keeps_links_the_importer_cannot_see()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var client = new ImportClient(app);
        const string Maison = """
            {"id":"goal-maison","name":"Maison","kind":"savings","target_amount":"5000.00",
             "ownership":[{"person":"adult-1","share_bp":5000},{"person":"adult-2","share_bp":5000}],
             "funding":[{"envelope":"env-a","share_bp":3000}]}
            """;
        await CommitAsync(client, app, app.AdultA, GoalsFile(Maison));
        var id = await IdOfAsync(app, "goal-maison");
        var envelope = (await StoredAsync(app, (store, _) => store.FundingOfAsync([id], default))).Single().Envelope;
        var hidden = await app.NewEnvelopeAsync("PEA de B", app.AdultB, Visibility.Private);
        Assert.True((await app.RawAsync(app.AdultB, HttpMethod.Put, $"/api/v1/goals/{id.Value}", new
        {
            name = "Maison", targetAmount = "5000.00", note = (string?)null, targetDate = (string?)null, version = await VersionAsync(app, id),
            funding = new[] { new { envelopeId = envelope.Value, shareBp = 3000 }, new { envelopeId = hidden.Value, shareBp = 2000 } },
        })).IsSuccessStatusCode);

        var replaced = await CommitAsync(client, app, app.AdultA, GoalsFile(Maison.Replace("3000", "4000").Replace("Maison", "Maison neuve")), replace: true);

        Assert.Equal(new CountsResponse(0, 0, 0, 1), replaced.Report!.Sections["goals"]);
        var funding = await StoredAsync(app, (store, _) => store.FundingOfAsync([id], default));
        Assert.Equal(new[] { (envelope, 4000), (hidden, 2000) }.OrderBy(f => f.Item1.Value), funding.Select(f => (f.Envelope, f.ShareBp)).OrderBy(f => f.Envelope.Value));
    }

    [Fact]
    public async Task Export_then_import_restores_goals_links_and_entries()
    {
        await using var source = await NidApp.StartAsync(fixture);
        await RoundTripTests.PrepareAsync(source, "Anna");
        await CommitAsync(new ImportClient(source), source, source.AdultA, GoalsFile(Trip, Fund, Free));

        var first = await new ExportClient(source).ExportAllAsync(source.AdultA);

        await using var target = await NidApp.StartAsync(fixture);
        await RoundTripTests.PrepareAsync(target, "Zoé");
        await RoundTripTests.CommitAsync(target, target.AdultA, RoundTripTests.AsFile(first));
        var second = await new ExportClient(target).ExportAllAsync(target.AdultA);

        Assert.Equal(RoundTripTests.Canonical(first), RoundTripTests.Canonical(second));
        Assert.Equal(["Précaution", "Voyage", "Vélo"], first["goals"].Select(g => g.GetProperty("name").GetString()));
        var trip = first["goals"].Single(g => g.GetProperty("id").GetString() == "goal-trip");
        Assert.Equal(2, trip.GetProperty("funding").GetArrayLength());
        Assert.True(first["goals"].Single(g => g.GetProperty("id").GetString() == "goal-fund").GetProperty("closed").GetBoolean());
        Assert.Equal(2, first["goals"].Single(g => g.GetProperty("id").GetString() == "goal-free").GetProperty("entries").GetArrayLength());
        var restored = await IdOfAsync(target, "goal-trip");
        Assert.Equal(2, (await StoredAsync(target, (store, _) => store.FundingOfAsync([restored], default))).Count);
    }
}
