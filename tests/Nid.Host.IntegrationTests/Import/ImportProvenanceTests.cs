using Microsoft.EntityFrameworkCore;
using Nid.Core.Contracts;
using Nid.Core.Infrastructure.Persistence;
using Nid.Platform.Contracts;
using Nid.TestSupport;

namespace Nid.Host.IntegrationTests.Import;

/// <summary>
/// Import provenance privacy (spec 2026-09-25): an import answer never depends on a row the importer cannot see.
/// An id another adult used privately is answered as an unused one and imported as new; a visible row is still
/// recognised, the importer's own first.
/// </summary>
public class ImportProvenanceTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static object Adults(NidApp app) => new Dictionary<string, Guid> { ["adult-1"] = app.AdultA.Value, ["adult-2"] = app.AdultB.Value };

    /// <summary>One subscription with a phase and a purchase, owned by <paramref name="owner"/>.</summary>
    private static Dictionary<string, string> File(string owner, string visibility, string name, string price, string label) => new()
    {
        ["persons"] = GoldenFile.Persons,
        ["subscriptions"] = $$$"""[{"id":"sub-p","name":"{{{name}}}","visibility":"{{{visibility}}}","ownership":[{"person":"{{{owner}}}","share_bp":10000}],"auto_renew":true}]""",
        ["billing_phases"] = $$$"""[{"id":"ph-p","subscription_id":"sub-p","starts_on":"2025-01-01","ends_on":null,"price":{"amount":"{{{price}}}","currency":"EUR"},"every":{"count":1,"unit":"month"},"payer":{"type":"household"}}]""",
        ["purchases"] = $$$"""[{"id":"pu-p","date":"2025-02-01","label":"{{{label}}}","kind":"gift","price":{"amount":"0","currency":"EUR"},"payer":{"type":"external"},"grants":[{"subscription_id":"sub-p","duration":{"count":1,"unit":"month"},"source":"gift"}]}]""",
    };

    private static Dictionary<string, string> FileOfA(string visibility = "private") => File("adult-1", visibility, "A's", "5", "A");
    private static Dictionary<string, string> FileOfB(string visibility = "private") => File("adult-2", visibility, "B's", "7", "B");

    private static async Task<SessionResponse> CommitAsync(ImportClient client, NidApp app, PersonId actor, Dictionary<string, string> sections)
    {
        var validated = await client.RunAsync(actor, Adults(app), sections);
        Assert.True(validated.State == "validated", string.Join("; ", validated.Report!.Issues.Select(i => $"{i.Code} at {i.Path}: {i.Message}")));
        var committed = await client.CommitRawAsync(actor, validated.Id);
        Assert.True(committed.IsSuccessStatusCode, await committed.Content.ReadAsStringAsync());
        return await client.GetAsync(actor, validated.Id);
    }

    /// <summary>Everything a report says, as one comparable string: state, counts per section, every issue.</summary>
    private static string Shape(ReportResponse report)
        => string.Join("\n", new[] { report.State }
            .Concat(report.Sections.OrderBy(s => s.Key, StringComparer.Ordinal).Select(s => $"{s.Key}: {s.Value}"))
            .Concat(report.Issues.Select(i => $"{i.Severity} {i.Code} {i.Path} {i.Message}")));

    private static void AssertAllUnchanged(ReportResponse report)
    {
        Assert.Equal(new CountsResponse(0, 1, 0), report.Sections["subscriptions"]);
        Assert.Equal(new CountsResponse(0, 1, 0), report.Sections["billing_phases"]);
        Assert.Equal(new CountsResponse(0, 1, 0), report.Sections["purchases"]);
    }

    [Fact]
    public async Task An_id_the_other_adult_used_privately_is_answered_as_unused_and_imported_as_new()
    {
        await using var fresh = await NidApp.StartAsync(fixture);
        var baseline = await new ImportClient(fresh).RunAsync(fresh.AdultB, Adults(fresh), FileOfB());

        await using var app = await NidApp.StartAsync(fixture);
        var client = new ImportClient(app);
        await CommitAsync(client, app, app.AdultA, FileOfA());

        var validated = await client.RunAsync(app.AdultB, Adults(app), FileOfB());

        Assert.Equal("validated", baseline.State);
        Assert.Equal(Shape(baseline.Report!), Shape(validated.Report!));
        var committed = await client.CommitRawAsync(app.AdultB, validated.Id);
        Assert.True(committed.IsSuccessStatusCode, await committed.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        await using var subs = PostgresFixture.NewSubscriptionsContext(app.ConnectionString);
        Assert.Equal(2, await subs.Subscriptions.CountAsync(s => s.ImportRef == "sub-p", cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(2, await subs.Phases.CountAsync(p => p.ImportRef == "ph-p", cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(2, await subs.Purchases.CountAsync(p => p.ImportRef == "pu-p", cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(1, await subs.Subscriptions.CountAsync(s => s.ImportRef == "sub-p" && s.ImportedBy == app.AdultB.Value, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_shared_row_is_still_recognised_by_the_other_adult()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var client = new ImportClient(app);
        await CommitAsync(client, app, app.AdultA, FileOfA("shared"));

        var same = await client.RunAsync(app.AdultB, Adults(app), FileOfA("shared"));
        Assert.Equal("validated", same.State);
        AssertAllUnchanged(same.Report!);
        Assert.True((await client.CancelRawAsync(app.AdultB, same.Id)).IsSuccessStatusCode);

        var changed = FileOfA("shared");
        changed["billing_phases"] = changed["billing_phases"].Replace("\"amount\":\"5\"", "\"amount\":\"6\"");
        var conflict = await client.RunAsync(app.AdultB, Adults(app), changed);
        Assert.Equal("invalid", conflict.State);
        var issue = Assert.Single(conflict.Report!.Issues, i => i.Severity == "error");
        Assert.Equal((ImportCodes.Conflict, "billing_phases[0]"), (issue.Code, issue.Path));
    }

    [Fact]
    public async Task Each_adult_resolves_to_their_own_rows_even_when_both_are_visible()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var client = new ImportClient(app);
        await CommitAsync(client, app, app.AdultA, FileOfA());
        await CommitAsync(client, app, app.AdultB, FileOfB("shared"));

        AssertAllUnchanged((await CommitAsync(client, app, app.AdultA, FileOfA())).Report!);

        await using (var core = PostgresFixture.NewCoreContext(app.ConnectionString))
        {
            var item = await core.Items.SingleAsync(i => i.DisplayName == "A's", cancellationToken: TestContext.Current.CancellationToken);
            item.Visibility = "shared";
            await core.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        AssertAllUnchanged((await CommitAsync(client, app, app.AdultA, FileOfA())).Report!);
        AssertAllUnchanged((await CommitAsync(client, app, app.AdultB, FileOfB("shared"))).Report!);
    }

    [Fact]
    public async Task The_importers_own_row_they_can_no_longer_see_is_refused()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var client = new ImportClient(app);
        await CommitAsync(client, app, app.AdultA, FileOfA());
        await using (var core = PostgresFixture.NewCoreContext(app.ConnectionString))
        {
            var item = await core.Items.Include(i => i.Owners).SingleAsync(i => i.DisplayName == "A's", cancellationToken: TestContext.Current.CancellationToken);
            item.Owners.Clear();
            item.Owners.Add(new OwnershipRow { ItemId = item.Id, PersonId = app.AdultB.Value, ShareBp = 10000 });
            await core.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var validated = await client.RunAsync(app.AdultA, Adults(app), FileOfA());

        Assert.Equal("invalid", validated.State);
        var issue = Assert.Single(validated.Report!.Issues, i => i.Severity == "error");
        Assert.Equal((ImportCodes.NotPermitted, "subscriptions[0]", "'sub-p' was imported before as an item you cannot see."),
            (issue.Code, issue.Path, issue.Message));
    }

    [Fact]
    public async Task A_row_imported_before_the_importer_was_recorded_is_recognised_when_visible()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var client = new ImportClient(app);
        await CommitAsync(client, app, app.AdultA, FileOfA());
        await using (var subs = PostgresFixture.NewSubscriptionsContext(app.ConnectionString))
        {
            await subs.Database.ExecuteSqlRawAsync("update subscriptions.subscription set imported_by = null", cancellationToken: TestContext.Current.CancellationToken);
            await subs.Database.ExecuteSqlRawAsync("update subscriptions.billing_phase set imported_by = null", cancellationToken: TestContext.Current.CancellationToken);
            await subs.Database.ExecuteSqlRawAsync("update subscriptions.purchase set imported_by = null", cancellationToken: TestContext.Current.CancellationToken);
        }

        AssertAllUnchanged((await CommitAsync(client, app, app.AdultA, FileOfA())).Report!);
    }

    /// <summary>
    /// B's subscription and A's share a file id, and A's is now visible to B. A phase or purchase of B's file
    /// belongs to the subscription B's file id resolved to — B's own — never to A's row of the same id under
    /// A's subscription, which would report it unchanged and leave B's subscription without it.
    /// </summary>
    [Fact]
    public async Task A_phase_or_purchase_resolves_under_the_subscription_its_file_resolved_to()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var client = new ImportClient(app);
        await CommitAsync(client, app, app.AdultA, FileOfA());
        var ofB = FileOfB();
        await CommitAsync(client, app, app.AdultB, new Dictionary<string, string> { ["persons"] = ofB["persons"], ["subscriptions"] = ofB["subscriptions"] });
        await using (var core = PostgresFixture.NewCoreContext(app.ConnectionString))
        {
            var item = await core.Items.SingleAsync(i => i.DisplayName == "A's", cancellationToken: TestContext.Current.CancellationToken);
            item.Visibility = "shared";
            await core.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var ofA = FileOfA();
        var committed = await CommitAsync(client, app, app.AdultB, new Dictionary<string, string>
        {
            ["persons"] = ofB["persons"], ["subscriptions"] = ofB["subscriptions"],
            ["billing_phases"] = ofA["billing_phases"], ["purchases"] = ofA["purchases"],
        });

        Assert.Equal(new CountsResponse(0, 1, 0), committed.Report!.Sections["subscriptions"]);
        Assert.Equal(new CountsResponse(1, 0, 0), committed.Report.Sections["billing_phases"]);
        Assert.Equal(new CountsResponse(1, 0, 0), committed.Report.Sections["purchases"]);
        await using var subs = PostgresFixture.NewSubscriptionsContext(app.ConnectionString);
        var ofBItem = await subs.Subscriptions.Where(s => s.ImportRef == "sub-p" && s.ImportedBy == app.AdultB.Value).Select(s => s.ItemId).SingleAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(await subs.Phases.AnyAsync(p => p.ItemId == ofBItem && p.ImportRef == "ph-p", cancellationToken: TestContext.Current.CancellationToken));
        Assert.True(await subs.Grants.AnyAsync(g => g.SubscriptionItemId == ofBItem, cancellationToken: TestContext.Current.CancellationToken));
    }
}
