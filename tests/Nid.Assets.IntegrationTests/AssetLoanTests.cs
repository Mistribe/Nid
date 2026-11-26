using System.Globalization;
using System.Net;
using System.Text.Json;
using Nid.Core.Contracts;
using Nid.TestSupport;
using static Nid.Assets.IntegrationTests.AssetApi;
using static Nid.TestSupport.LoanApi;

namespace Nid.Assets.IntegrationTests;

/// <summary>#188: the loans financing an asset and its net value (the net worth already counts every loan once).</summary>
public class AssetLoanTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    // The golden loan owes 162 253.12 on the host's today (2025-10-15, loans.md §4).
    private const string Owed = "162253.12";

    private static async Task<string> NetWorthAsync(NidApp app)
        => JsonDocument.Parse(await app.GetRawAsync(app.AdultA, "/api/v1/dashboard?scope=household")).RootElement
            .GetProperty("netWorth").GetProperty("total").GetProperty("amount").GetString()!;

    private static Task<HttpResponseMessage> LinkAsync(NidApp app, Guid loan, ItemId? asset, PersonId? actor = null)
        => app.RawAsync(actor ?? app.AdultA, HttpMethod.Patch, $"{Loans}/{loan}", asset is { } a ? new { assetId = a.Value } : new { clearAsset = true });

    [Fact]
    public async Task Net_value_is_the_value_less_the_balance_of_the_readable_linked_loans()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var asset = await CreateAsync(app, Body(app, "Voiture", "vehicle", purchasePrice: "250000"));
        var loan = await NewAsync(app, Golden(app, "Crédit auto"));

        var before = await GetAsync(app, asset);
        Assert.Empty(before.GetProperty("loans").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, before.GetProperty("netValue").ValueKind);
        var worth = await NetWorthAsync(app);

        (await LinkAsync(app, loan, asset)).EnsureSuccessStatusCode();
        var view = await GetAsync(app, asset);

        var linked = Assert.Single(view.GetProperty("loans").EnumerateArray());
        Assert.Equal(("Crédit auto", Owed, "EUR"), (linked.GetProperty("name").GetString(), linked.GetProperty("balance").GetProperty("amount").GetString(),
            linked.GetProperty("balance").GetProperty("currency").GetString()));
        Assert.Equal("250000.00", view.GetProperty("value").GetProperty("amount").GetString());
        Assert.Equal("87746.88", view.GetProperty("netValue").GetProperty("amount").GetString());
        Assert.Equal(worth, await NetWorthAsync(app)); // display only: the loan was already a holding of its own
    }

    [Fact]
    public async Task Net_value_starts_from_the_estimate_when_a_depreciation_rule_applies()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var body = Body(app, "Citadine", "vehicle", purchasePrice: "250000");
        body["depreciationRate"] = 20m;
        var asset = await CreateAsync(app, body);
        (await LinkAsync(app, await NewAsync(app, Golden(app)), asset)).EnsureSuccessStatusCode();

        var view = await GetAsync(app, asset);

        var value = decimal.Parse(view.GetProperty("value").GetProperty("amount").GetString()!, CultureInfo.InvariantCulture);
        Assert.True(view.GetProperty("valueEstimated").GetBoolean());
        Assert.True(value < 250000m);
        Assert.Equal((value - 162253.12m).ToString("0.00", CultureInfo.InvariantCulture), view.GetProperty("netValue").GetProperty("amount").GetString());
    }

    [Fact]
    public async Task A_loan_the_reader_cannot_see_is_neither_named_nor_subtracted()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var asset = await CreateAsync(app, Body(app, "Voiture", "vehicle", purchasePrice: "250000"));
        var secret = await NewAsync(app, Golden(app, "Crédit privé de A", "private"));
        var shared = await NewAsync(app, Short(app, "Crédit commun"));
        (await LinkAsync(app, secret, asset)).EnsureSuccessStatusCode();
        (await LinkAsync(app, shared, asset)).EnsureSuccessStatusCode();

        var seenByB = await GetAsync(app, asset, app.AdultB);
        var names = seenByB.GetProperty("loans").EnumerateArray().Select(l => l.GetProperty("name").GetString()).ToList();
        Assert.Equal(["Crédit commun"], names);
        Assert.DoesNotContain("Crédit privé", seenByB.GetRawText());
        Assert.NotEqual((await GetAsync(app, asset)).GetProperty("netValue").GetProperty("amount").GetString(),
            seenByB.GetProperty("netValue").GetProperty("amount").GetString());
    }

    [Fact]
    public async Task A_linked_loan_blocks_the_assets_delete_and_unlinking_frees_it()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var asset = await CreateAsync(app, Body(app, "Voiture", "vehicle"));
        var loan = await NewAsync(app, Golden(app));
        (await LinkAsync(app, loan, asset)).EnsureSuccessStatusCode();

        var preview = await AssetApi.JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Route}/{asset.Value}/deletion"));
        Assert.Equal("asset_loan", Assert.Single(preview.GetProperty("blockers").EnumerateArray()).GetProperty("key").GetString());
        var refused = await app.RawAsync(app.AdultA, HttpMethod.Delete, $"{Route}/{asset.Value}?version={preview.GetProperty("version").GetUInt32()}");
        Assert.NotEqual(HttpStatusCode.NoContent, refused.StatusCode);

        (await LinkAsync(app, loan, null)).EnsureSuccessStatusCode();
        preview = await AssetApi.JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Route}/{asset.Value}/deletion"));
        Assert.Empty(preview.GetProperty("blockers").EnumerateArray());
    }

    [Fact]
    public async Task A_loan_without_an_exchange_rate_stays_listed_unbalanced_flagged_and_not_deducted()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var asset = await CreateAsync(app, Body(app, "Voiture", "vehicle", purchasePrice: "250000"));
        var body = Golden(app, "Crédit en francs");
        body["currency"] = "CHF";
        var loan = await NewAsync(app, body);
        (await LinkAsync(app, loan, asset)).EnsureSuccessStatusCode();

        var view = await GetAsync(app, asset);

        var linked = Assert.Single(view.GetProperty("loans").EnumerateArray());
        Assert.Equal(("Crédit en francs", JsonValueKind.Null), (linked.GetProperty("name").GetString(), linked.GetProperty("balance").ValueKind));
        Assert.True(view.GetProperty("fxMissing").GetBoolean());
        Assert.Equal("250000.00", view.GetProperty("netValue").GetProperty("amount").GetString());
    }
}
