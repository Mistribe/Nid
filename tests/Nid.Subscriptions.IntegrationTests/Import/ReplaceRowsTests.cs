using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.TestSupport;
using NodaTime;

namespace Nid.Subscriptions.IntegrationTests.Import;

/// <summary>1F3b: with <c>replace</c>, billing phases and purchases imported before with other content are replaced in place.</summary>
[Collection("postgres")]
public class ReplaceRowsTests(PostgresFixture fixture)
{
    /// <summary>The price of the golden ph-spotify-1, the only "15.17" of the file, and another.</summary>
    private const string OldAmount = "15.17";
    private const string NewAmount = "16.17";

    private const string OldLabel = "Carte cadeau 3 mois";
    private const string NewLabel = "Carte cadeau 4 mois";

    private static object Adults(NidApp app) => new Dictionary<string, Guid> { ["adult-1"] = app.AdultA.Value, ["adult-2"] = app.AdultB.Value };

    private static async Task<SessionResponse> CommitAsync(ImportClient client, NidApp app, IReadOnlyDictionary<string, string> sections, bool replace = false)
    {
        var session = await client.StartAsync(app.AdultA, Adults(app));
        foreach (var (section, entities) in sections)
            Assert.True((await client.PutAsync(app.AdultA, session.Id, section, 0, entities)).Accepted);
        var validated = await client.ValidateAsync(app.AdultA, session.Id, replace);
        Assert.True(validated.State == "validated", string.Join("; ", validated.Report!.Issues.Select(i => $"{i.Code} at {i.Path}")));
        var committed = await client.CommitRawAsync(app.AdultA, session.Id, replace);
        Assert.True(committed.IsSuccessStatusCode, await committed.Content.ReadAsStringAsync());
        return await client.GetAsync(app.AdultA, session.Id);
    }

    private static async Task<SessionResponse> StageAsync(ImportClient client, NidApp app, PersonId actor, IReadOnlyDictionary<string, string> sections)
    {
        var session = await client.StartAsync(actor, Adults(app));
        foreach (var (section, entities) in sections)
            Assert.True((await client.PutAsync(actor, session.Id, section, 0, entities)).Accepted);
        return session;
    }

    private static string Append(string golden, string entities)
        => golden.TrimEnd().TrimEnd(']') + "," + entities.TrimStart().TrimStart('[');

    /// <summary>The golden sub-spotify renamed (Task 4's technique: anchored on its unique name), so it is replaced.</summary>
    private static string RenamedSubscriptions() => GoldenFile.Subscriptions.Replace("Spotify Premium Duo", "Spotify Family");

    [Fact]
    public async Task With_replace_a_phase_is_updated_in_place_and_its_history_keeps_the_old_price()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var client = new ImportClient(app);
        await CommitAsync(client, app, GoldenFile.Sections());
        Guid phaseId;
        string? hashBefore;
        await using (var before = PostgresFixture.NewSubscriptionsContext(app.ConnectionString))
        {
            var row = await before.Phases.AsNoTracking().SingleAsync(p => p.ImportRef == "ph-spotify-1");
            (phaseId, hashBefore) = (row.Id, row.ImportHash);
        }

        var repriced = GoldenFile.With("billing_phases", GoldenFile.Phases.Replace(OldAmount, NewAmount));
        var committed = await CommitAsync(client, app, repriced, replace: true);

        Assert.Equal(new CountsResponse(0, 1, 0, 1), committed.Report!.Sections["billing_phases"]);
        await using var after = PostgresFixture.NewSubscriptionsContext(app.ConnectionString);
        Assert.Equal(2, await after.Phases.CountAsync());
        var phase = await after.Phases.AsNoTracking().SingleAsync(p => p.Id == phaseId); // same row
        Assert.Equal(decimal.Parse(NewAmount, CultureInfo.InvariantCulture), phase.PriceAmount);
        Assert.Equal("ph-spotify-1", phase.ImportRef);
        Assert.NotEqual(hashBefore, phase.ImportHash);
        await using var platform = PostgresFixture.NewPlatformContext(app.ConnectionString);
        var update = await platform.AuditLog.SingleAsync(r => r.Category == "write" && r.Action == "update" && r.Entity == "subscriptions.billing_phase");
        Assert.Contains(OldAmount, update.Data);
        Assert.Equal("import", update.Via);
    }

    [Fact]
    public async Task With_replace_a_purchase_is_updated_in_place_grants_included()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var client = new ImportClient(app);
        await CommitAsync(client, app, GoldenFile.Sections());
        Guid purchaseId;
        Guid spotify;
        await using (var before = PostgresFixture.NewSubscriptionsContext(app.ConnectionString))
        {
            purchaseId = (await before.Purchases.AsNoTracking().SingleAsync(p => p.ImportRef == "pu-gift")).Id;
            spotify = (await before.Subscriptions.AsNoTracking().SingleAsync(s => s.ImportRef == "sub-spotify")).ItemId;
        }
        await using var platformBefore = PostgresFixture.NewPlatformContext(app.ConnectionString);
        var lastSeq = await platformBefore.AuditLog.MaxAsync(r => r.Seq);

        // pu-gift's label and its only grant's duration: "count":3 is unique to that grant.
        var changed = GoldenFile.With("purchases", GoldenFile.Purchases
            .Replace(OldLabel, NewLabel)
            .Replace("\"duration\":{\"count\":3,\"unit\":\"month\"}", "\"duration\":{\"count\":4,\"unit\":\"month\"}"));
        var committed = await CommitAsync(client, app, changed, replace: true);

        Assert.Equal(new CountsResponse(0, 2, 0, 1), committed.Report!.Sections["purchases"]);
        await using var after = PostgresFixture.NewSubscriptionsContext(app.ConnectionString);
        Assert.Equal(3, await after.Purchases.CountAsync());
        var purchase = await after.Purchases.AsNoTracking().Include(p => p.Grants).SingleAsync(p => p.Id == purchaseId); // same row
        Assert.Equal(NewLabel, purchase.Label);
        var grant = Assert.Single(purchase.Grants);
        Assert.Equal((0, spotify, 4, "month", "gift"), (grant.Index, grant.SubscriptionItemId, grant.DurationCount, grant.DurationUnit, grant.Source));

        await using var platform = PostgresFixture.NewPlatformContext(app.ConnectionString);
        var writes = await platform.AuditLog.Where(r => r.Category == "write" && r.Seq > lastSeq).ToListAsync();
        var update = Assert.Single(writes, r => r.Action == "update" && r.Entity == "subscriptions.purchase");
        Assert.Contains(OldLabel, update.Data);
        Assert.Equal([spotify], update.ItemIds);
        Assert.Single(writes, r => r.Action == "delete" && r.Entity == "subscriptions.coverage_grant");
        Assert.Single(writes, r => r.Action == "insert" && r.Entity == "subscriptions.coverage_grant");
        Assert.All(writes, r => Assert.Equal("import", r.Via));

        // Spec §3.3/§6: the old value stays in the item's history.
        var history = await ItemHistoryAsync(app, spotify);
        Assert.Contains(history, e => e.Action == "update" && e.Entity == "subscriptions.purchase" && e.Body.Contains(OldLabel));
    }

    /// <summary>A purchase whose grant moves from one subscription to another is in the history of both.</summary>
    [Fact]
    public async Task A_replaced_purchase_whose_grants_move_is_in_the_history_of_the_old_and_the_new_subscription()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var client = new ImportClient(app);
        await CommitAsync(client, app, GoldenFile.Sections());
        Guid spotify;
        Guid wow;
        await using (var before = PostgresFixture.NewSubscriptionsContext(app.ConnectionString))
        {
            spotify = (await before.Subscriptions.AsNoTracking().SingleAsync(s => s.ImportRef == "sub-spotify")).ItemId;
            wow = (await before.Subscriptions.AsNoTracking().SingleAsync(s => s.ImportRef == "sub-wow")).ItemId;
        }
        await using var platformBefore = PostgresFixture.NewPlatformContext(app.ConnectionString);
        var lastSeq = await platformBefore.AuditLog.MaxAsync(r => r.Seq);

        // pu-gift's only grant ("count":3 is unique to it) moves from sub-spotify to sub-wow, and its label changes.
        var moved = GoldenFile.With("purchases", GoldenFile.Purchases
            .Replace(OldLabel, NewLabel)
            .Replace("{\"subscription_id\":\"sub-spotify\",\"duration\":{\"count\":3,", "{\"subscription_id\":\"sub-wow\",\"duration\":{\"count\":3,"));
        var committed = await CommitAsync(client, app, moved, replace: true);

        Assert.Equal(1, committed.Report!.Sections["purchases"].Replaced);
        await using var platform = PostgresFixture.NewPlatformContext(app.ConnectionString);
        var update = await platform.AuditLog.SingleAsync(r => r.Seq > lastSeq && r.Action == "update" && r.Entity == "subscriptions.purchase");
        Assert.Equal(new[] { spotify, wow }.Order(), update.ItemIds.Order());
        foreach (var item in new[] { spotify, wow })
        {
            var history = await ItemHistoryAsync(app, item);
            Assert.Contains(history, e => e.Action == "update" && e.Entity == "subscriptions.purchase" && e.Body.Contains(OldLabel));
        }
    }

    private static async Task<IReadOnlyList<(string Action, string Entity, string Body)>> ItemHistoryAsync(NidApp app, Guid item)
    {
        var response = await app.RawAsync(app.AdultA, HttpMethod.Get, $"/api/v1/platform/audit/items/{item}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var page = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return page.RootElement.GetProperty("entries").EnumerateArray()
            .Select(e => (e.GetProperty("action").GetString()!, e.GetProperty("entity").GetString()!, e.GetRawText()))
            .ToList();
    }

    /// <summary>
    /// A replaced purchase may grant to a subscription this same file creates: that one has no item yet, so it is
    /// not write-checked (the new-purchase rule), only the items that exist are.
    /// </summary>
    [Fact]
    public async Task A_replaced_purchase_can_grant_to_a_subscription_the_same_file_creates()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var client = new ImportClient(app);
        await CommitAsync(client, app, GoldenFile.Sections());
        Guid purchaseId;
        await using (var before = PostgresFixture.NewSubscriptionsContext(app.ConnectionString))
            purchaseId = (await before.Purchases.AsNoTracking().SingleAsync(p => p.ImportRef == "pu-gift")).Id;

        var file = GoldenFile.With("subscriptions", Append(GoldenFile.Subscriptions,
            """[{"id":"sub-deezer","name":"Deezer","ownership":[{"person":"adult-1","share_bp":10000}],"auto_renew":true}]"""));
        // pu-gift's only grant ("count":3 is unique to it) followed by a second one on the new subscription.
        file["purchases"] = GoldenFile.Purchases.Replace(
            "\"duration\":{\"count\":3,\"unit\":\"month\"},\"source\":\"gift\"}",
            "\"duration\":{\"count\":3,\"unit\":\"month\"},\"source\":\"gift\"},{\"subscription_id\":\"sub-deezer\",\"duration\":{\"count\":1,\"unit\":\"month\"},\"source\":\"gift\"}");
        var committed = await CommitAsync(client, app, file, replace: true);

        Assert.Equal(new CountsResponse(1, 3, 0, 0), committed.Report!.Sections["subscriptions"]);
        Assert.Equal(new CountsResponse(0, 2, 0, 1), committed.Report.Sections["purchases"]);
        await using var after = PostgresFixture.NewSubscriptionsContext(app.ConnectionString);
        var deezer = (await after.Subscriptions.AsNoTracking().SingleAsync(s => s.ImportRef == "sub-deezer")).ItemId;
        var spotify = (await after.Subscriptions.AsNoTracking().SingleAsync(s => s.ImportRef == "sub-spotify")).ItemId;
        var purchase = await after.Purchases.AsNoTracking().Include(p => p.Grants).SingleAsync(p => p.Id == purchaseId);
        Assert.Equal([(0, spotify), (1, deezer)], purchase.Grants.OrderBy(g => g.Index).Select(g => (g.Index, g.SubscriptionItemId)).ToList());
    }

    private const string Subscriptions = """
        [{"id":"sub-shared","name":"Shared","ownership":[{"person":"adult-1","share_bp":5000},{"person":"adult-2","share_bp":5000}],"auto_renew":true},
         {"id":"sub-a","name":"A's","ownership":[{"person":"adult-1","share_bp":10000}],"auto_renew":true}]
        """;

    private static string Gift(params string[] subscriptions)
        => """[{"id":"pu-x","date":"2025-05-01","label":"X","kind":"gift","price":{"amount":"0","currency":"EUR"},"payer":{"type":"external"},"grants":["""
           + string.Join(",", subscriptions.Select(s => $$"""{"subscription_id":"{{s}}","duration":{"count":1,"unit":"month"},"source":"gift"}"""))
           + "]}]";

    /// <summary>
    /// From SubscriptionsImportTests' two-adult tests: "sub-shared" is owned by both adults, so B writes it; "sub-a"
    /// is owned by A alone and visible to the household, so B reads it and cannot write it. A imports a purchase;
    /// B re-imports it with its grants moved onto sub-a, or away from it. Either way the replacement touches sub-a
    /// (as it will be, or as it is now), which B cannot write. The purchase keeps a grant on sub-shared, so B's
    /// file id resolves to A's row (a row of another adult resolves only under the subscriptions the file names).
    /// </summary>
    [Theory]
    [InlineData(new[] { "sub-shared" }, new[] { "sub-shared", "sub-a" })]
    [InlineData(new[] { "sub-shared", "sub-a" }, new[] { "sub-shared" })]
    public async Task Moving_a_purchases_grants_needs_write_access_to_the_items_it_used_to_touch_and_the_new_ones(string[] before, string[] moved)
    {
        await using var app = await NidApp.StartAsync(fixture);
        var client = new ImportClient(app);
        await CommitAsync(client, app, new Dictionary<string, string>
        {
            ["persons"] = GoldenFile.Persons, ["subscriptions"] = Subscriptions, ["purchases"] = Gift(before),
        });
        await using var platformBefore = PostgresFixture.NewPlatformContext(app.ConnectionString);
        var writesBefore = await platformBefore.AuditLog.CountAsync(r => r.Category == "write");

        var session = await StageAsync(client, app, app.AdultB, new Dictionary<string, string>
        {
            ["persons"] = GoldenFile.Persons, ["subscriptions"] = Subscriptions, ["purchases"] = Gift(moved),
        });
        var validated = await client.ValidateAsync(app.AdultB, session.Id, replace: true);

        Assert.Equal("invalid", validated.State);
        Assert.Equal(new CountsResponse(0, 2, 0), validated.Report!.Sections["subscriptions"]);
        Assert.Equal(new CountsResponse(0, 0, 0), validated.Report.Sections["purchases"]);
        var issue = Assert.Single(validated.Report.Issues, i => i.Severity == "error");
        Assert.Equal((ImportCodes.NotPermitted, "purchases[0].grants"), (issue.Code, issue.Path));
        Assert.Equal(HttpStatusCode.Conflict, (await client.CommitRawAsync(app.AdultB, session.Id, replace: true)).StatusCode);

        await using var subs = PostgresFixture.NewSubscriptionsContext(app.ConnectionString);
        var purchase = await subs.Purchases.AsNoTracking().Include(p => p.Grants).SingleAsync();
        Assert.Equal(before.Length, purchase.Grants.Count);
        await using var platform = PostgresFixture.NewPlatformContext(app.ConnectionString);
        Assert.Equal(writesBefore, await platform.AuditLog.CountAsync(r => r.Category == "write"));
    }

    [Fact]
    public async Task A_replaced_subscription_can_gain_a_new_phase_checked_against_its_other_phases()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var client = new ImportClient(app);
        await CommitAsync(client, app, GoldenFile.Sections());

        // Overlaps the stored open ph-spotify-2 (from 2025-06-28).
        const string overlapping = """
            [{"id":"ph-spotify-3","subscription_id":"sub-spotify","starts_on":"2025-07-28","ends_on":null,
              "price":{"amount":"19.99","currency":"EUR"},"every":{"count":1,"unit":"month"},"payer":{"type":"household"}}]
            """;
        var refusedFile = GoldenFile.With("subscriptions", RenamedSubscriptions());
        refusedFile["billing_phases"] = Append(GoldenFile.Phases, overlapping);
        var refused = await StageAsync(client, app, app.AdultA, refusedFile);
        var invalid = await client.ValidateAsync(app.AdultA, refused.Id, replace: true);

        Assert.Equal("invalid", invalid.State);
        Assert.Equal(1, invalid.Report!.Sections["subscriptions"].Replaced);
        var issue = Assert.Single(invalid.Report.Issues, i => i.Severity == "error");
        Assert.Equal(("subscriptions.phase_overlap", "billing_phases[2]"), (issue.Code, issue.Path));
        Assert.True((await client.CancelRawAsync(app.AdultA, refused.Id)).IsSuccessStatusCode); // one open session per importer

        // Five whole months ending where ph-spotify-1 starts.
        const string before = """
            [{"id":"ph-spotify-3","subscription_id":"sub-spotify","starts_on":"2024-01-28","ends_on":"2024-06-28",
              "price":{"amount":"12.99","currency":"EUR"},"every":{"count":1,"unit":"month"},"payer":{"type":"household"}}]
            """;
        var acceptedFile = GoldenFile.With("subscriptions", RenamedSubscriptions());
        acceptedFile["billing_phases"] = Append(GoldenFile.Phases, before);
        var committed = await CommitAsync(client, app, acceptedFile, replace: true);

        Assert.Equal(new CountsResponse(0, 2, 0, 1), committed.Report!.Sections["subscriptions"]);
        Assert.Equal(new CountsResponse(1, 2, 0, 0), committed.Report.Sections["billing_phases"]);
        await using var subs = PostgresFixture.NewSubscriptionsContext(app.ConnectionString);
        var spotify = (await subs.Subscriptions.AsNoTracking().SingleAsync(s => s.ImportRef == "sub-spotify")).ItemId;
        Assert.Equal(3, await subs.Phases.CountAsync(p => p.ItemId == spotify));
    }

    /// <summary>A phase is replaced under its subscription (spec §3.3 lists no subscription among what is replaced): it does not move.</summary>
    [Fact]
    public async Task A_replaced_phase_does_not_move_to_another_subscription()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var client = new ImportClient(app);
        await CommitAsync(client, app, GoldenFile.Sections());

        // ph-spotify-1 is the only phase that ends on 2025-06-28: anchored on its first field and that date.
        var moved = GoldenFile.With("billing_phases", GoldenFile.Phases.Replace(
            "\"id\":\"ph-spotify-1\",\"subscription_id\":\"sub-spotify\"", "\"id\":\"ph-spotify-1\",\"subscription_id\":\"sub-wow\""));
        var session = await StageAsync(client, app, app.AdultA, moved);
        var validated = await client.ValidateAsync(app.AdultA, session.Id, replace: true);

        Assert.Equal("invalid", validated.State);
        var issue = Assert.Single(validated.Report!.Issues, i => i.Severity == "error");
        Assert.Equal((ImportCodes.InvalidValue, "billing_phases[0].subscription_id"), (issue.Code, issue.Path));
        Assert.Equal((0, 0), (validated.Report.Sections["billing_phases"].Replaced, validated.Report.Sections["billing_phases"].Conflict));
        await using var subs = PostgresFixture.NewSubscriptionsContext(app.ConnectionString);
        var spotify = (await subs.Subscriptions.AsNoTracking().SingleAsync(s => s.ImportRef == "sub-spotify")).ItemId;
        Assert.Equal(spotify, (await subs.Phases.AsNoTracking().SingleAsync(p => p.ImportRef == "ph-spotify-1")).ItemId);
    }

    [Fact]
    public async Task A_replacement_that_breaks_the_timeline_refuses_the_commit_and_writes_nothing()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var client = new ImportClient(app);
        await CommitAsync(client, app, GoldenFile.Sections());
        await using var platformBefore = PostgresFixture.NewPlatformContext(app.ConnectionString);
        var writesBefore = await platformBefore.AuditLog.CountAsync(r => r.Category == "write");

        // ph-spotify-1 now runs a month into the stored ph-spotify-2: a phase alone passes the receipt's check, only
        // the dry run against the subscription's other phases sees the overlap (the engine's one cross-row rule).
        var overlapping = GoldenFile.With("billing_phases",
            GoldenFile.Phases.Replace("\"ends_on\":\"2025-06-28\"", "\"ends_on\":\"2025-07-28\""));
        var session = await StageAsync(client, app, app.AdultA, overlapping);
        var validated = await client.ValidateAsync(app.AdultA, session.Id, replace: true);

        Assert.Equal("invalid", validated.State);
        var issue = Assert.Single(validated.Report!.Issues, i => i.Severity == "error");
        Assert.Equal(("subscriptions.phase_overlap", "billing_phases[0]"), (issue.Code, issue.Path));
        Assert.Equal(0, validated.Report.Sections["billing_phases"].Conflict);
        Assert.Equal(HttpStatusCode.Conflict, (await client.CommitRawAsync(app.AdultA, session.Id, replace: true)).StatusCode);

        await using var subs = PostgresFixture.NewSubscriptionsContext(app.ConnectionString);
        var phase = await subs.Phases.AsNoTracking().SingleAsync(p => p.ImportRef == "ph-spotify-1");
        Assert.Equal(new LocalDate(2025, 6, 28), phase.EndsOn);
        await using var platform = PostgresFixture.NewPlatformContext(app.ConnectionString);
        Assert.Equal(writesBefore, await platform.AuditLog.CountAsync(r => r.Category == "write"));
    }
}
