using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nid.Host.IntegrationTests.Export;
using Nid.Platform.Contracts;
using Nid.TestSupport;

namespace Nid.Host.IntegrationTests.Import;

/// <summary>Issue #208: the contract terms of a subscription travel in the file, in both directions.</summary>
public class ContractTermsImportTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string TermsMembers = """
        "anniversary_on":"2026-03-15","notice":{"count":2,"unit":"month"},"commitment_ends_on":"2027-03-15","cancellable_from":"2026-09-15"
        """;

    private static object Adults(NidApp app) => new Dictionary<string, Guid> { ["adult-1"] = app.AdultA.Value, ["adult-2"] = app.AdultB.Value };

    private static string Subscription(string members = "")
        => $$"""
            [{"id":"sub-insurance","name":"Assurance habitation","ownership":[{"person":"adult-1","share_bp":10000}],"auto_renew":true{{(members.Length == 0 ? "" : "," + members)}}}]
            """;

    private static Dictionary<string, string> File(string subscriptions) => new()
    {
        ["persons"] = GoldenFile.Persons,
        ["subscriptions"] = subscriptions,
    };

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

    private static async Task<(string? Anniversary, int? NoticeCount, string? NoticeUnit, string? CommitmentEnds, string? CancellableFrom)> StoredTermsAsync(NidApp app)
    {
        await using var subs = PostgresFixture.NewSubscriptionsContext(app.ConnectionString);
        var row = await subs.Subscriptions.AsNoTracking().SingleAsync(s => s.ImportRef == "sub-insurance");
        return (row.AnniversaryOn?.ToString("yyyy-MM-dd", null), row.NoticeCount, row.NoticeUnit,
            row.CommitmentEndsOn?.ToString("yyyy-MM-dd", null), row.CancellableFrom?.ToString("yyyy-MM-dd", null));
    }

    /// <summary>What the subscriptions batch is refused with, at the batch itself: the protocol checks each record as it arrives.</summary>
    private static async Task<IReadOnlyList<IssueResponse>> IssuesOfAsync(NidApp app, string subscriptions)
    {
        var client = new ImportClient(app);
        var session = await client.StartAsync(app.AdultA, Adults(app));
        Assert.True((await client.PutAsync(app.AdultA, session.Id, "persons", 0, GoldenFile.Persons)).Accepted);
        var batch = await client.PutAsync(app.AdultA, session.Id, "subscriptions", 0, subscriptions);
        Assert.False(batch.Accepted);
        return batch.Issues;
    }

    [Fact]
    public async Task Terms_round_trip()
    {
        await using var source = await NidApp.StartAsync(fixture);
        await RoundTripTests.PrepareAsync(source, "Anna");
        await RoundTripTests.CommitAsync(source, source.AdultA, File(Subscription(TermsMembers)));

        var first = await new ExportClient(source).ExportAllAsync(source.AdultA);
        var exported = Assert.Single(first["subscriptions"]);
        Assert.Equal("2026-03-15", exported.GetProperty("anniversary_on").GetString());
        Assert.Equal(2, exported.GetProperty("notice").GetProperty("count").GetInt32());
        Assert.Equal("month", exported.GetProperty("notice").GetProperty("unit").GetString());
        Assert.Equal("2027-03-15", exported.GetProperty("commitment_ends_on").GetString());
        Assert.Equal("2026-09-15", exported.GetProperty("cancellable_from").GetString());

        await using var target = await NidApp.StartAsync(fixture);
        await RoundTripTests.PrepareAsync(target, "Zoé");
        await RoundTripTests.CommitAsync(target, target.AdultA, RoundTripTests.AsFile(first));

        Assert.Equal(("2026-03-15", 2, "month", "2027-03-15", "2026-09-15"), await StoredTermsAsync(target));
        var second = await new ExportClient(target).ExportAllAsync(target.AdultA);
        Assert.Equal(JsonSerializer.Serialize(first["subscriptions"]), JsonSerializer.Serialize(second["subscriptions"]));
    }

    [Fact]
    public async Task File_without_terms_stays_valid()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var committed = await CommitAsync(new ImportClient(app), app, File(Subscription()));

        Assert.Equal(new CountsResponse(1, 0, 0), committed.Report!.Sections["subscriptions"]);
        Assert.Equal((null, null, null, null, null), await StoredTermsAsync(app));
    }

    [Fact]
    public async Task Old_file_keeps_its_hash()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var client = new ImportClient(app);
        await CommitAsync(client, app, GoldenFile.Sections());

        // The same old file, which names none of the new members, still answers as unchanged.
        var again = await CommitAsync(client, app, GoldenFile.Sections());
        Assert.Equal(new CountsResponse(0, 3, 0), again.Report!.Sections["subscriptions"]);

        // And what an export writes for a subscription without terms names none of them either: re-imported
        // as it is, its hash does not move.
        var export = await new ExportClient(app).ExportAllAsync(app.AdultA);
        foreach (var record in export["subscriptions"])
            foreach (var member in new[] { "anniversary_on", "notice", "commitment_ends_on", "cancellable_from" })
                Assert.False(record.TryGetProperty(member, out _), member);
    }

    [Fact]
    public async Task Notice_alone_is_refused_at_its_path()
    {
        await using var app = await NidApp.StartAsync(fixture);

        var issues = await IssuesOfAsync(app, Subscription("\"notice\":{\"count\":2,\"unit\":\"month\"}"));

        var issue = Assert.Single(issues, i => i.Severity == "error");
        Assert.Equal(("subscriptions[0].notice", "subscriptions.notice_without_date"), (issue.Path, issue.Code));
    }

    [Theory]
    [InlineData("anniversary_on", "\"15/03/2026\"", "subscriptions[0].anniversary_on")]
    [InlineData("commitment_ends_on", "\"soon\"", "subscriptions[0].commitment_ends_on")]
    [InlineData("cancellable_from", "\"2026-02-30\"", "subscriptions[0].cancellable_from")]
    public async Task Malformed_date_names_its_member(string member, string value, string path)
    {
        await using var app = await NidApp.StartAsync(fixture);

        var issues = await IssuesOfAsync(app, Subscription($"\"{member}\":{value}"));

        var issue = Assert.Single(issues, i => i.Severity == "error");
        Assert.Equal((path, ImportCodes.InvalidValue), (issue.Path, issue.Code));
    }

    [Fact]
    public async Task Terms_set_in_the_ui_survive_an_unchanged_reimport()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var client = new ImportClient(app);
        await CommitAsync(client, app, File(Subscription()));

        Guid id;
        await using (var subs = PostgresFixture.NewSubscriptionsContext(app.ConnectionString))
            id = (await subs.Subscriptions.AsNoTracking().SingleAsync(s => s.ImportRef == "sub-insurance", cancellationToken: TestContext.Current.CancellationToken)).ItemId;
        var patched = await app.RawAsync(app.AdultA, HttpMethod.Patch, $"/api/v1/subscriptions/{id}",
            new { anniversaryOn = "2026-03-15", notice = new { count = 2, unit = "month" }, commitmentEndsOn = "2027-03-15", cancellableFrom = "2026-09-15" });
        Assert.True(patched.IsSuccessStatusCode, await patched.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var expected = ("2026-03-15", 2, "month", "2027-03-15", "2026-09-15");
        Assert.Equal(expected, await StoredTermsAsync(app));

        var again = await CommitAsync(client, app, File(Subscription()), replace: true);

        Assert.Equal(new CountsResponse(0, 1, 0), again.Report!.Sections["subscriptions"]);
        Assert.Equal(expected, await StoredTermsAsync(app));
    }

    [Fact]
    public async Task Replacing_sets_and_clears_terms()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var client = new ImportClient(app);
        await CommitAsync(client, app, File(Subscription(TermsMembers)));
        Assert.Equal(("2026-03-15", 2, "month", "2027-03-15", "2026-09-15"), await StoredTermsAsync(app));

        var changed = await CommitAsync(client, app,
            File(Subscription("\"anniversary_on\":\"2026-04-01\",\"notice\":{\"count\":30,\"unit\":\"day\"}")), replace: true);
        Assert.Equal(new CountsResponse(0, 0, 0, 1), changed.Report!.Sections["subscriptions"]);
        Assert.Equal(("2026-04-01", 30, "day", null, null), await StoredTermsAsync(app));

        var cleared = await CommitAsync(client, app, File(Subscription()), replace: true);
        Assert.Equal(new CountsResponse(0, 0, 0, 1), cleared.Report!.Sections["subscriptions"]);
        Assert.Equal((null, null, null, null, null), await StoredTermsAsync(app));
    }
}
