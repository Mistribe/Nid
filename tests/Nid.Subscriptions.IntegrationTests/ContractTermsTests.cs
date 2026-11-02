using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nid.Core.Contracts;
using Nid.TestSupport;

namespace Nid.Subscriptions.IntegrationTests;

/// <summary>Design 2026-10-30-contract-terms-insurance §2 and §5: the terms are stored, edited by PATCH and read back with the outlook.</summary>
public class ContractTermsTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    // The fixture's clock reads 2025-10-15 (NidApp.GoldenCaseToday).
    private static object Home => new
    {
        anniversaryOn = "2026-03-01",
        notice = new { count = 2, unit = "month" },
        cancellableFrom = "2026-03-01",
    };

    private static string Url(ItemId id) => $"/api/v1/subscriptions/{id.Value}";

    private static async Task<JsonElement> Body(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>();

    [Fact]
    public async Task Patch_sets_terms_and_detail_returns_the_outlook()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await app.NewMonthlyAsync(app.AdultA, 10m);

        (await app.RawAsync(app.AdultA, HttpMethod.Patch, Url(id), Home)).EnsureSuccessStatusCode();

        var detail = await app.GetAsync<JsonElement>(app.AdultA, Url(id));
        var terms = detail.GetProperty("contractTerms");
        Assert.Equal("2026-03-01", terms.GetProperty("anniversaryOn").GetString());
        Assert.Equal((2, "month"), (terms.GetProperty("notice").GetProperty("count").GetInt32(), terms.GetProperty("notice").GetProperty("unit").GetString()));
        Assert.Equal("2026-03-01", terms.GetProperty("cancellableFrom").GetString());
        Assert.Equal(JsonValueKind.Null, terms.GetProperty("commitmentEndsOn").ValueKind);
        var cancellation = detail.GetProperty("cancellation");
        Assert.Equal("deadline", cancellation.GetProperty("status").GetString());
        Assert.Equal("2026-01-01", cancellation.GetProperty("lastDayToCancel").GetString());
        Assert.Equal("2026-03-01", cancellation.GetProperty("effectiveOn").GetString());
    }

    [Fact]
    public async Task A_subscription_without_terms_has_none_and_a_none_outlook()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await app.NewMonthlyAsync(app.AdultA, 10m);

        var detail = await app.GetAsync<JsonElement>(app.AdultA, Url(id));

        Assert.Equal(JsonValueKind.Null, detail.GetProperty("contractTerms").ValueKind);
        Assert.Equal("none", detail.GetProperty("cancellation").GetProperty("status").GetString());
    }

    [Fact]
    public async Task Patch_clears_one_field_keeps_the_others()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await app.NewMonthlyAsync(app.AdultA, 10m);
        (await app.RawAsync(app.AdultA, HttpMethod.Patch, Url(id), Home)).EnsureSuccessStatusCode();

        (await app.RawAsync(app.AdultA, HttpMethod.Patch, Url(id), new { clearCancellableFrom = true })).EnsureSuccessStatusCode();

        var terms = (await app.GetAsync<JsonElement>(app.AdultA, Url(id))).GetProperty("contractTerms");
        Assert.Equal(JsonValueKind.Null, terms.GetProperty("cancellableFrom").ValueKind);
        Assert.Equal("2026-03-01", terms.GetProperty("anniversaryOn").GetString());
        Assert.Equal(2, terms.GetProperty("notice").GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task Notice_alone_is_refused_at_notice()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await app.NewMonthlyAsync(app.AdultA, 10m);

        var response = await app.RawAsync(app.AdultA, HttpMethod.Patch, Url(id), new { notice = new { count = 2, unit = "month" } });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var error = Assert.Single((await Body(response)).GetProperty("errors").EnumerateArray());
        Assert.Equal(("notice", "subscriptions.notice_without_date"), (error.GetProperty("path").GetString(), error.GetProperty("code").GetString()));
        Assert.Equal(JsonValueKind.Null, (await app.GetAsync<JsonElement>(app.AdultA, Url(id))).GetProperty("contractTerms").ValueKind);
    }

    [Fact]
    public async Task Clearing_the_anniversary_under_a_stored_notice_is_refused()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await app.NewMonthlyAsync(app.AdultA, 10m);
        (await app.RawAsync(app.AdultA, HttpMethod.Patch, Url(id), Home)).EnsureSuccessStatusCode();

        var response = await app.RawAsync(app.AdultA, HttpMethod.Patch, Url(id), new { clearAnniversaryOn = true });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var error = Assert.Single((await Body(response)).GetProperty("errors").EnumerateArray());
        Assert.Equal(("notice", "subscriptions.notice_without_date"), (error.GetProperty("path").GetString(), error.GetProperty("code").GetString()));
        var terms = (await app.GetAsync<JsonElement>(app.AdultA, Url(id))).GetProperty("contractTerms");
        Assert.Equal("2026-03-01", terms.GetProperty("anniversaryOn").GetString());
    }

    [Theory]
    [InlineData("anniversaryOn")]
    [InlineData("commitmentEndsOn")]
    [InlineData("cancellableFrom")]
    public async Task Value_and_clear_flag_contradict(string field)
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await app.NewMonthlyAsync(app.AdultA, 10m);
        var clear = "clear" + char.ToUpperInvariant(field[0]) + field[1..];

        var response = await app.RawAsync(app.AdultA, HttpMethod.Patch, Url(id),
                                          new Dictionary<string, object> { [field] = "2026-03-01", [clear] = true });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Notice_and_clear_notice_contradict()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await app.NewMonthlyAsync(app.AdultA, 10m);

        var response = await app.RawAsync(app.AdultA, HttpMethod.Patch, Url(id),
                                          new { anniversaryOn = "2026-03-01", notice = new { count = 1, unit = "month" }, clearNotice = true });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Patch_without_terms_keeps_them()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await app.NewMonthlyAsync(app.AdultA, 10m);
        (await app.RawAsync(app.AdultA, HttpMethod.Patch, Url(id), Home)).EnsureSuccessStatusCode();

        (await app.RawAsync(app.AdultA, HttpMethod.Patch, Url(id), new { note = "kept" })).EnsureSuccessStatusCode();
        (await app.RawAsync(app.AdultA, HttpMethod.Patch, Url(id), new { autoRenew = false })).EnsureSuccessStatusCode();

        var terms = (await app.GetAsync<JsonElement>(app.AdultA, Url(id))).GetProperty("contractTerms");
        Assert.Equal("2026-03-01", terms.GetProperty("anniversaryOn").GetString());
        Assert.Equal(2, terms.GetProperty("notice").GetProperty("count").GetInt32());
        Assert.Equal("2026-03-01", terms.GetProperty("cancellableFrom").GetString());
    }

    [Fact]
    public async Task A_lone_notice_on_an_unknown_subscription_is_not_found()
    {
        await using var app = await NidApp.StartAsync(fixture);

        var response = await app.RawAsync(app.AdultA, HttpMethod.Patch, Url(new ItemId(Guid.NewGuid())),
                                          new { notice = new { count = 2, unit = "month" } });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Terms_change_is_audited()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await app.NewMonthlyAsync(app.AdultA, 10m);
        await using var platformBefore = PostgresFixture.NewPlatformContext(app.ConnectionString);
        var lastSeq = await platformBefore.AuditLog.MaxAsync(r => r.Seq, cancellationToken: TestContext.Current.CancellationToken);

        (await app.RawAsync(app.AdultA, HttpMethod.Patch, Url(id), Home)).EnsureSuccessStatusCode();

        await using var platform = PostgresFixture.NewPlatformContext(app.ConnectionString);
        var update = await platform.AuditLog.SingleAsync(r => r.Seq > lastSeq && r.Category == "write" && r.Action == "update"
                                                              && r.Entity == "subscriptions.subscription", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal([id.Value], update.ItemIds);
        Assert.Contains("anniversary_on", update.Data);
        Assert.Contains("2026-03-01", update.Data);
        Assert.Contains("notice_count", update.Data);
    }
}
