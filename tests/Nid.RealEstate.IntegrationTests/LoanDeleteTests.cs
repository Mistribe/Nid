using System.Net;
using System.Text.Json;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.RealEstate.Contracts;
using Nid.TestSupport;
using static Nid.TestSupport.LoanApi;
using static Nid.TestSupport.PropertyApi;

namespace Nid.RealEstate.IntegrationTests;

/// <summary>#23: a loan created by mistake is deleted after a preview, with its contract, fees, repayments and bank table; nothing blocks.</summary>
public class LoanDeleteTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static async Task<JsonElement> PreviewAsync(NidApp app, PersonId actor, Guid loan)
    {
        var response = await app.RawAsync(actor, HttpMethod.Get, $"{Loans}/{loan}/deletion");
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await JsonAsync(response);
    }

    private static int Removed(JsonElement preview, string key)
        => preview.GetProperty("removes").EnumerateArray().Single(r => r.GetProperty("key").GetString() == key).GetProperty("count").GetInt32();

    private static async Task<uint> DeletionVersionAsync(NidApp app, Guid loan) => (await PreviewAsync(app, app.AdultA, loan)).GetProperty("version").GetUInt32();

    private static Task<HttpResponseMessage> DeleteAsync(NidApp app, Guid loan, uint version)
        => app.RawAsync(app.AdultA, HttpMethod.Delete, $"{Loans}/{loan}?version={version}");

    private static async Task<HttpStatusCode> StatusAsync(NidApp app, string url) => (await app.RawAsync(app.AdultA, HttpMethod.Get, url)).StatusCode;

    private static async Task AssertRefusedAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal(code, (await JsonAsync(response)).GetProperty("code").GetString());
    }

    /// <summary>The golden loan on a property, with an insurance, a fee, an early repayment and one bank row.</summary>
    private static async Task<(Guid Loan, Guid Property)> FilledAsync(NidApp app)
    {
        var property = await NewPropertyAsync(app, Home(app));
        var body = Golden(app);
        body["propertyId"] = property;
        body["insurances"] = new[] { new { mode = "fixed_monthly", monthlyAmount = "20", startsOn = "2020-11-15" } };
        var loan = await LoanApi.NewAsync(app, body);
        await CreatedAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Loans}/{loan}/fees", new { kind = "file", amount = "1000", onDate = "2020-10-15" }));
        await CreatedAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Loans}/{loan}/repayments",
            new { onDate = "2023-03-15", amount = "10000", effect = "reduce_term" }));
        await StoreTableAsync(app, loan, ScheduleSource.Computed, Sixtieth);
        return (loan, property);
    }

    [Fact]
    public async Task Preview_counts_phases_fees_repayments_and_bank_rows()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var (loan, _) = await FilledAsync(app);

        var preview = await PreviewAsync(app, app.AdultA, loan);

        Assert.Equal("Prêt maison", preview.GetProperty("name").GetString());
        Assert.Equal(1, Removed(preview, "loan_phases"));
        Assert.Equal(1, Removed(preview, "loan_fees"));
        Assert.Equal(1, Removed(preview, "loan_repayments"));
        Assert.Equal(1, Removed(preview, "bank_schedule_rows"));
        Assert.Empty(preview.GetProperty("blockers").EnumerateArray());
    }

    [Fact]
    public async Task Delete_removes_the_loan_and_everything_it_holds_and_keeps_its_property()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var (loan, property) = await FilledAsync(app);

        var deleted = await DeleteAsync(app, loan, await DeletionVersionAsync(app, loan));

        Assert.True(deleted.StatusCode == HttpStatusCode.NoContent, await deleted.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(app, $"{Loans}/{loan}"));
        Assert.Equal(HttpStatusCode.OK, await StatusAsync(app, $"{Properties}/{property}"));
        await using var realestate = PostgresFixture.NewRealEstateContext(app.ConnectionString);
        Assert.Empty(realestate.Loans.Where(l => l.ItemId == loan));
        Assert.Empty(realestate.Phases.Where(p => p.LoanItemId == loan));
        Assert.Empty(realestate.Insurances.Where(i => i.LoanItemId == loan));
        Assert.Empty(realestate.Fees.Where(f => f.LoanItemId == loan));
        Assert.Empty(realestate.Events.Where(e => e.LoanItemId == loan));
        Assert.Empty(realestate.BankSchedule.Where(r => r.LoanItemId == loan));
        Assert.Single(realestate.Properties.Where(p => p.ItemId == property));
        await using var core = PostgresFixture.NewCoreContext(app.ConnectionString);
        Assert.Empty(core.Flows.Where(f => f.ItemId == loan));
        Assert.Empty(core.Valuations.Where(v => v.ItemId == loan));
    }

    [Fact]
    public async Task Stale_version_answers_409()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var loan = await LoanApi.NewAsync(app, Short(app));
        var stale = await DeletionVersionAsync(app, loan);
        var renamed = await app.RawAsync(app.AdultA, HttpMethod.Patch, $"{Loans}/{loan}", new { name = "Prêt de Léa" });
        Assert.True(renamed.IsSuccessStatusCode, await renamed.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        await AssertRefusedAsync(await DeleteAsync(app, loan, stale), HttpStatusCode.Conflict, ItemCodes.Changed);

        Assert.Equal(HttpStatusCode.OK, await StatusAsync(app, $"{Loans}/{loan}"));
    }

    [Fact]
    public async Task Missing_version_answers_422()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var loan = await LoanApi.NewAsync(app, Short(app));

        var response = await app.RawAsync(app.AdultA, HttpMethod.Delete, $"{Loans}/{loan}");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("version", (await JsonAsync(response)).GetProperty("errors")[0].GetProperty("path").GetString());
    }

    [Fact]
    public async Task Reader_gets_403_and_stranger_404()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var shared = (await app.NewLoanAsync("Prêt commun", Visibility.Shared, (app.AdultA, 10_000))).Value;
        var secret = (await app.NewLoanAsync("Prêt perso", Visibility.Private, (app.AdultA, 10_000))).Value;

        Assert.Equal(HttpStatusCode.Forbidden, (await app.RawAsync(app.AdultB, HttpMethod.Get, $"{Loans}/{shared}/deletion")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await app.RawAsync(app.AdultB, HttpMethod.Delete, $"{Loans}/{shared}?version=1")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await app.RawAsync(app.AdultB, HttpMethod.Get, $"{Loans}/{secret}/deletion")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await app.RawAsync(app.AdultB, HttpMethod.Delete, $"{Loans}/{secret}?version=1")).StatusCode);
    }

    [Fact]
    public async Task Write_direct_token_is_refused()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var loan = await LoanApi.NewAsync(app, Short(app));
        var version = await DeletionVersionAsync(app, loan);
        var token = await TokenApi.CreateAsync(app, app.AdultA, TokenPresets.Direct);

        await AssertRefusedAsync(await app.BearerAsync(token.Secret, HttpMethod.Delete, $"{Loans}/{loan}?version={version}"),
            HttpStatusCode.Forbidden, BrowserOnlyCodes.Required);

        Assert.Equal(HttpStatusCode.OK, await StatusAsync(app, $"{Loans}/{loan}"));
    }
}
