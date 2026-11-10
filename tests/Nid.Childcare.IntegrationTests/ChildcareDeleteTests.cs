using System.Net;
using System.Text.Json;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.TestSupport;
using static Nid.Childcare.IntegrationTests.ChildcareApi;

namespace Nid.Childcare.IntegrationTests;

/// <summary>#23: a childcare contract created by mistake is deleted after a preview, with every row of it; a validated month refuses it.</summary>
public class ChildcareDeleteTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static async Task<JsonElement> PreviewAsync(NidApp app, PersonId actor, string url)
    {
        var response = await app.RawAsync(actor, HttpMethod.Get, $"{url}/deletion");
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await JsonAsync(response);
    }

    private static int Removed(JsonElement preview, string key)
        => preview.GetProperty("removes").EnumerateArray().Where(r => r.GetProperty("key").GetString() == key).Select(r => r.GetProperty("count").GetInt32()).SingleOrDefault(); // a zero count is not listed

    private static async Task<uint> VersionAsync(NidApp app, string url) => (await PreviewAsync(app, app.AdultA, url)).GetProperty("version").GetUInt32();

    private static Task<HttpResponseMessage> DeleteAsync(NidApp app, string url, uint version) => app.RawAsync(app.AdultA, HttpMethod.Delete, $"{url}?version={version}");

    private static async Task<HttpStatusCode> StatusAsync(NidApp app, string url) => (await app.RawAsync(app.AdultA, HttpMethod.Get, url)).StatusCode;

    private static Guid IdOf(string url) => Guid.Parse(url[(url.LastIndexOf('/') + 1)..]);

    private static async Task AssertRefusedAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal(code, (await JsonAsync(response)).GetProperty("code").GetString());
    }

    private static async Task SendAsync(NidApp app, HttpMethod method, string url, object? body = null)
    {
        var response = await app.RawAsync(app.AdultA, method, url, body);
        Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
    }

    /// <summary>Two week types with an exception week, a gap, and September validated then reopened (a draft with its days).</summary>
    private static async Task<string> FilledAsync(NidApp app)
    {
        var url = await CreateAsync(app, weekTypes: [new { name = "Longue", weeks = 23, schedule = Schedule }, new { name = "Courte", weeks = 22, schedule = Schedule }]);
        await SendAsync(app, HttpMethod.Put, $"{url}/week-overrides/2025-09-08", new { weekType = 0 }); // the rotation gives it 1
        await SendAsync(app, HttpMethod.Put, $"{url}/gaps", new { gaps = new[] { new { monday = "2026-02-16", weeks = 2 } } });
        await ValidateAsync(app, url, "2025-09");
        await ReopenAsync(app, url, "2025-09");
        return url;
    }

    [Fact]
    public async Task Preview_counts_statements_gaps_and_week_overrides()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var url = await FilledAsync(app);

        var preview = await PreviewAsync(app, app.AdultA, url);

        Assert.Equal("Garde de Léa", preview.GetProperty("name").GetString());
        Assert.False(preview.GetProperty("imported").GetBoolean());
        Assert.Equal(1, Removed(preview, "statements"));
        Assert.Equal(1, Removed(preview, "gaps"));
        Assert.Equal(1, Removed(preview, "week_overrides"));
        Assert.Empty(preview.GetProperty("blockers").EnumerateArray());
    }

    [Fact]
    public async Task Validated_month_blocks_until_reopened()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var url = await CreateAsync(app);
        await ValidateAsync(app, url, "2025-09");
        await ValidateAsync(app, url, "2025-10");

        var blocker = (await PreviewAsync(app, app.AdultA, url)).GetProperty("blockers").EnumerateArray().Single(b => b.GetProperty("key").GetString() == "validated_statements");
        await AssertRefusedAsync(await DeleteAsync(app, url, await VersionAsync(app, url)), HttpStatusCode.Conflict, ItemCodes.InUse);

        Assert.Equal(2, blocker.GetProperty("count").GetInt32());
        Assert.Empty(blocker.GetProperty("items").EnumerateArray());
        Assert.Equal(HttpStatusCode.OK, await StatusAsync(app, url));
        await using (var childcare = PostgresFixture.NewChildcareContext(app.ConnectionString))
            Assert.Equal(2, childcare.Statements.Count(s => s.ItemId == IdOf(url)));

        await ReopenAsync(app, url, "2025-10");
        await ReopenAsync(app, url, "2025-09");
        var deleted = await DeleteAsync(app, url, await VersionAsync(app, url));

        Assert.True(deleted.StatusCode == HttpStatusCode.NoContent, await deleted.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(app, url));
    }

    [Fact]
    public async Task Delete_removes_every_row_of_the_contract_and_keeps_the_child()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var url = await FilledAsync(app);
        var id = IdOf(url);

        var deleted = await DeleteAsync(app, url, await VersionAsync(app, url));

        Assert.True(deleted.StatusCode == HttpStatusCode.NoContent, await deleted.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(app, url));
        await using var childcare = PostgresFixture.NewChildcareContext(app.ConnectionString);
        Assert.Empty(childcare.Arrangements.Where(a => a.ItemId == id));
        Assert.Empty(childcare.Terms.Where(t => t.ItemId == id));
        Assert.Empty(childcare.WeekTypes.Where(w => w.ItemId == id));
        Assert.Empty(childcare.Weekdays.Where(w => w.ItemId == id));
        Assert.Empty(childcare.WeekOverrides.Where(o => o.ItemId == id));
        Assert.Empty(childcare.Gaps.Where(g => g.ItemId == id));
        Assert.Empty(childcare.Statements.Where(s => s.ItemId == id));
        Assert.Empty(childcare.StatementDays.Where(d => d.ItemId == id));
        await using var core = PostgresFixture.NewCoreContext(app.ConnectionString);
        Assert.Empty(core.Items.Where(i => i.Id == id));
        Assert.Empty(core.Flows.Where(f => f.ItemId == id));
        Assert.Single(core.Persons.Where(p => p.Id == app.Dependent.Value));
    }

    [Fact]
    public async Task Closed_contract_can_be_deleted()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var url = await CreateAsync(app);
        await SendAsync(app, HttpMethod.Post, $"{url}/close", new { on = "2025-10-31" });

        Assert.Equal(HttpStatusCode.NoContent, (await DeleteAsync(app, url, await VersionAsync(app, url))).StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(app, url));
    }

    [Fact]
    public async Task Stale_version_answers_409()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var url = await CreateAsync(app);
        var stale = await VersionAsync(app, url);
        await SendAsync(app, HttpMethod.Post, $"{url}/close", new { on = "2025-10-31" });

        await AssertRefusedAsync(await DeleteAsync(app, url, stale), HttpStatusCode.Conflict, ItemCodes.Changed);

        Assert.Equal(HttpStatusCode.OK, await StatusAsync(app, url));
    }

    [Fact]
    public async Task Missing_version_answers_422()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var url = await CreateAsync(app);

        var response = await app.RawAsync(app.AdultA, HttpMethod.Delete, url);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("version", (await JsonAsync(response)).GetProperty("errors")[0].GetProperty("path").GetString());
    }

    [Fact]
    public async Task Reader_gets_403_and_stranger_404()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var shared = await CreateAsync(app);
        var secret = await CreateAsync(app, "private");

        Assert.Equal(HttpStatusCode.Forbidden, (await app.RawAsync(app.AdultB, HttpMethod.Get, $"{shared}/deletion")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await app.RawAsync(app.AdultB, HttpMethod.Delete, $"{shared}?version=1")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await app.RawAsync(app.AdultB, HttpMethod.Get, $"{secret}/deletion")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await app.RawAsync(app.AdultB, HttpMethod.Delete, $"{secret}?version=1")).StatusCode);
    }

    [Fact]
    public async Task Write_direct_token_is_refused()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var url = await CreateAsync(app);
        var version = await VersionAsync(app, url);
        var token = await TokenApi.CreateAsync(app, app.AdultA, TokenPresets.Direct);

        await AssertRefusedAsync(await app.BearerAsync(token.Secret, HttpMethod.Delete, $"{url}?version={version}"), HttpStatusCode.Forbidden, BrowserOnlyCodes.Required);

        Assert.Equal(HttpStatusCode.OK, await StatusAsync(app, url));
    }
}
