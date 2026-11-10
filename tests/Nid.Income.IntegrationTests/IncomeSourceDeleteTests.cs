using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nid.Core.Contracts;
using Nid.Documents.Infrastructure.Persistence;
using Nid.Platform.Contracts;
using Nid.TestSupport;
using NodaTime;
using static Nid.TestSupport.DocumentApi;

namespace Nid.Income.IntegrationTests;

/// <summary>#23: an income source created by mistake is deleted after a preview, with its phases, one-off incomes and document anchors.</summary>
public class IncomeSourceDeleteTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string Sources = "/api/v1/income/sources";

    private static object Phase(string label, string startsOn, string? endsOn = null)
        => new { label, startsOn, endsOn, netAmount = new { amount = "2000", currency = "EUR" }, every = new { count = 1, unit = "month" } };

    private static object OneOff() => new
    {
        occurredOn = "2025-06-10", amount = new { amount = "500", currency = "EUR" }, note = "Prime",
        taxableAmount = new { amount = "450", currency = "EUR" },
    };

    private static async Task<Guid> CreatedAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await JsonAsync(response)).GetProperty("id").GetGuid();
    }

    private static async Task<Guid> NewSourceAsync(NidApp app, params object[] phases)
        => await CreatedAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, Sources, new Dictionary<string, object?>
        {
            ["displayName"] = "Salaire", ["kind"] = "salary", ["currency"] = "EUR", ["visibility"] = "shared",
            ["owners"] = new[] { new { personId = app.AdultA.Value, shareBp = 10_000 } },
            ["phases"] = phases,
        }));

    private static async Task<JsonElement> PreviewAsync(NidApp app, PersonId actor, Guid source)
    {
        var response = await app.RawAsync(actor, HttpMethod.Get, $"{Sources}/{source}/deletion");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await JsonAsync(response);
    }

    private static int Removed(JsonElement preview, string key)
        => preview.GetProperty("removes").EnumerateArray().Single(r => r.GetProperty("key").GetString() == key).GetProperty("count").GetInt32();

    private static async Task<uint> VersionAsync(NidApp app, Guid source) => (await PreviewAsync(app, app.AdultA, source)).GetProperty("version").GetUInt32();

    [Fact]
    public async Task Preview_counts_phases_and_one_offs()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var source = await NewSourceAsync(app, Phase("Salaire", "2025-01-01", "2025-06-30"), Phase("Augmentation", "2025-07-01"));
        await CreatedAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Sources}/{source}/one-offs", OneOff()));

        var preview = await PreviewAsync(app, app.AdultA, source);

        Assert.Equal("Salaire", preview.GetProperty("name").GetString());
        Assert.Equal(2, Removed(preview, "phases"));
        Assert.Equal(1, Removed(preview, "flows"));
    }

    [Fact]
    public async Task Delete_removes_the_source_its_phases_one_offs_and_document_links()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var source = await NewSourceAsync(app, Phase("Salaire", "2025-01-01"));
        var phase = (await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Sources}/{source}"))).GetProperty("phases")[0].GetProperty("id").GetGuid();
        var oneOff = await CreatedAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Sources}/{source}/one-offs", OneOff()));
        var document = await UploadedAsync(app, app.AdultA, Pdf, $"fileName=a.pdf&targetKind=income.phase&targetId={phase}&role=payslip");
        await using (var documents = PostgresFixture.NewDocumentsContext(app.ConnectionString))
        {
            documents.ExpectedDismissals.Add(new ExpectedDismissalRow
            {
                TargetKind = "income.one_off", TargetId = oneOff, Role = "payslip", Period = 0,
                DismissedByPersonId = app.AdultA.Value, DismissedAt = Instant.FromUtc(2025, 10, 15, 0, 0),
            });
            await documents.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        var version = await VersionAsync(app, source);

        var deleted = await app.RawAsync(app.AdultA, HttpMethod.Delete, $"{Sources}/{source}?version={version}");

        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Sources}/{source}")).StatusCode);
        await using var income = PostgresFixture.NewIncomeContext(app.ConnectionString);
        Assert.Empty(income.Phases.Where(p => p.SourceItemId == source));
        Assert.Empty(income.OneOffTaxables.Where(t => t.SourceId == source));
        await using var core = PostgresFixture.NewCoreContext(app.ConnectionString);
        Assert.Empty(core.Flows.Where(f => f.ItemId == source));
        await using var after = PostgresFixture.NewDocumentsContext(app.ConnectionString);
        Assert.Empty(after.Links.Where(l => l.DocumentId == document));
        Assert.Empty(after.ExpectedDismissals.Where(d => d.TargetId == oneOff));
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(app, app.AdultA, document)).StatusCode);
    }

    [Fact]
    public async Task Ended_source_can_be_deleted()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var source = await NewSourceAsync(app, Phase("Salaire", "2025-01-01"));
        (await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Sources}/{source}/close", new { endedOn = "2025-09-30" })).EnsureSuccessStatusCode();

        var deleted = await app.RawAsync(app.AdultA, HttpMethod.Delete, $"{Sources}/{source}?version={await VersionAsync(app, source)}");

        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
    }

    [Fact]
    public async Task Stale_version_answers_409()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var source = await NewSourceAsync(app, Phase("Salaire", "2025-01-01"));
        var version = await VersionAsync(app, source);
        (await app.RawAsync(app.AdultA, HttpMethod.Patch, $"{Sources}/{source}", new { displayName = "Paie" })).EnsureSuccessStatusCode();

        var response = await app.RawAsync(app.AdultA, HttpMethod.Delete, $"{Sources}/{source}?version={version}");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(ItemCodes.Changed, (await JsonAsync(response)).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Sources}/{source}")).StatusCode);
    }

    [Fact]
    public async Task Missing_version_answers_422()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var source = await NewSourceAsync(app, Phase("Salaire", "2025-01-01"));

        var response = await app.RawAsync(app.AdultA, HttpMethod.Delete, $"{Sources}/{source}");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("version", (await JsonAsync(response)).GetProperty("errors")[0].GetProperty("path").GetString());
    }

    [Fact]
    public async Task Reader_gets_403_and_stranger_404()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var shared = await NewSourceAsync(app, Phase("Salaire", "2025-01-01"));
        var secret = (await app.NewIncomeSourceAsync("Cadeau", app.AdultA, "private")).Value;

        Assert.Equal(HttpStatusCode.Forbidden, (await app.RawAsync(app.AdultB, HttpMethod.Get, $"{Sources}/{shared}/deletion")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await app.RawAsync(app.AdultB, HttpMethod.Delete, $"{Sources}/{shared}?version=1")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await app.RawAsync(app.AdultB, HttpMethod.Get, $"{Sources}/{secret}/deletion")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await app.RawAsync(app.AdultB, HttpMethod.Delete, $"{Sources}/{secret}?version=1")).StatusCode);
    }

    [Fact]
    public async Task Write_direct_token_is_refused()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var source = await NewSourceAsync(app, Phase("Salaire", "2025-01-01"));
        var version = await VersionAsync(app, source);
        var token = await TokenApi.CreateAsync(app, app.AdultA, TokenPresets.Direct);

        var response = await app.BearerAsync(token.Secret, HttpMethod.Delete, $"{Sources}/{source}?version={version}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(BrowserOnlyCodes.Required, (await JsonAsync(response)).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Sources}/{source}")).StatusCode);
    }
}
