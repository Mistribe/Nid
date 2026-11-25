using System.Net;
using System.Text.Json;
using Nid.Core.Contracts;
using Nid.TestSupport;
using static Nid.TestSupport.LoanApi;
using static Nid.TestSupport.PropertyApi;

namespace Nid.RealEstate.IntegrationTests;

/// <summary>#188: a loan finances an asset (« Autres biens ») instead of a property or an SCPI.</summary>
public class AssetLinkTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static async Task<Guid> AssetAsync(NidApp app, string name = "Voiture", string visibility = "shared", PersonId? owner = null)
    {
        var who = owner ?? app.AdultA;
        var response = await app.RawAsync(who, HttpMethod.Post, "/api/v1/assets", new
        {
            name, kind = "vehicle", purchasePrice = "10000", purchasedOn = "2025-01-10", visibility,
            owners = new[] { new { personId = who.Value, shareBp = 10_000 } },
        });
        return await CreatedAsync(response);
    }

    private static Dictionary<string, object?> Financing(Guid asset, Dictionary<string, object?> loan)
    {
        loan["assetId"] = asset;
        return loan;
    }

    private static async Task<JsonElement[]> ListAsync(NidApp app, string query, PersonId? actor = null)
        => (await JsonAsync(await app.RawAsync(actor ?? app.AdultA, HttpMethod.Get, $"{Loans}{query}"))).GetProperty("items").EnumerateArray().ToArray();

    [Fact]
    public async Task A_loan_finances_an_asset_at_creation_moves_by_its_patch_and_is_cleared()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var first = await AssetAsync(app);
        var second = await AssetAsync(app, "Moto");
        var loan = await NewAsync(app, Financing(first, Golden(app)));

        var detail = await DetailAsync(app, loan);
        Assert.Equal((first, "Voiture"), (detail.GetProperty("assetId").GetGuid(), S(detail, "assetName")));

        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Patch, $"{Loans}/{loan}", new { assetId = second })).StatusCode);
        detail = await DetailAsync(app, loan);
        Assert.Equal((second, "Moto"), (detail.GetProperty("assetId").GetGuid(), S(detail, "assetName")));

        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Patch, $"{Loans}/{loan}", new { clearAsset = true })).StatusCode);
        detail = await DetailAsync(app, loan);
        Assert.Equal(JsonValueKind.Null, detail.GetProperty("assetId").ValueKind);
        Assert.Equal(JsonValueKind.Null, detail.GetProperty("assetName").ValueKind);
    }

    [Fact]
    public async Task An_unreadable_asset_a_property_and_an_unknown_id_all_answer_invalid_asset()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var hidden = await AssetAsync(app, "Voiture de B", "private", app.AdultB);
        var home = await NewPropertyAsync(app, Home(app));
        var loan = await NewAsync(app, Golden(app));

        foreach (var target in new[] { hidden, home, Guid.NewGuid() })
        {
            Assert.Equal((HttpStatusCode.UnprocessableEntity, "realestate.invalid_asset", "assetId"),
                await RefusalAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, Loans, Financing(target, Golden(app, name: "Prêt lié")))));
            Assert.Equal((HttpStatusCode.UnprocessableEntity, "realestate.invalid_asset", "assetId"),
                await RefusalAsync(await app.RawAsync(app.AdultA, HttpMethod.Patch, $"{Loans}/{loan}", new { assetId = target })));
        }
        Assert.Equal(JsonValueKind.Null, (await DetailAsync(app, loan)).GetProperty("assetId").ValueKind);
    }

    [Fact]
    public async Task A_loan_finances_one_item_never_two()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var asset = await AssetAsync(app);
        var home = await NewPropertyAsync(app, Home(app));
        var both = Financing(asset, Golden(app));
        both["propertyId"] = home;
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "realestate.invalid_asset", "assetId"),
            await RefusalAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, Loans, both)));

        var loan = await NewAsync(app, Golden(app));
        (await app.RawAsync(app.AdultA, HttpMethod.Patch, $"{Loans}/{loan}", new { propertyId = home })).EnsureSuccessStatusCode();
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "realestate.invalid_asset", "assetId"),
            await RefusalAsync(await app.RawAsync(app.AdultA, HttpMethod.Patch, $"{Loans}/{loan}", new { assetId = asset })));

        Assert.Equal(HttpStatusCode.OK,
            (await app.RawAsync(app.AdultA, HttpMethod.Patch, $"{Loans}/{loan}", new { assetId = asset, clearProperty = true })).StatusCode);
        var detail = await DetailAsync(app, loan);
        Assert.Equal((JsonValueKind.Null, asset), (detail.GetProperty("propertyId").ValueKind, detail.GetProperty("assetId").GetGuid()));
    }

    [Fact]
    public async Task The_list_names_only_a_readable_asset_and_filters_the_loans_financing_nothing()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var mine = await AssetAsync(app, "Voiture privée", "private");
        var loan = await NewAsync(app, Financing(mine, Golden(app)));
        var free = await NewAsync(app, Golden(app, name: "Prêt sans lien"));

        var row = Assert.Single(await ListAsync(app, "?unfinanced=true"));
        Assert.Equal(free, row.GetProperty("id").GetGuid());
        Assert.Equal(2, (await ListAsync(app, "")).Length);

        var listed = Assert.Single(await ListAsync(app, ""), r => r.GetProperty("id").GetGuid() == loan);
        Assert.Equal("Voiture privée", S(listed, "assetName"));
        var seenByB = Assert.Single(await ListAsync(app, "", app.AdultB), r => r.GetProperty("id").GetGuid() == loan);
        Assert.Equal(mine, seenByB.GetProperty("assetId").GetGuid());
        Assert.Equal(JsonValueKind.Null, seenByB.GetProperty("assetName").ValueKind);
    }

    [Fact]
    public async Task The_writable_filter_keeps_only_the_loans_the_reader_may_edit()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var free = await NewAsync(app, Golden(app, name: "Prêt sans lien"));

        var seenByB = Assert.Single(await ListAsync(app, "?unfinanced=true", app.AdultB));
        Assert.False(seenByB.GetProperty("canWrite").GetBoolean());
        Assert.Empty(await ListAsync(app, "?unfinanced=true&writable=true", app.AdultB));
        Assert.Equal(free, Assert.Single(await ListAsync(app, "?unfinanced=true&writable=true")).GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task A_financed_asset_refuses_its_delete_until_the_loan_lets_go()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var asset = await AssetAsync(app);
        var loan = await NewAsync(app, Financing(asset, Golden(app)));

        var preview = await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, $"/api/v1/assets/{asset}/deletion"));
        var blocker = Assert.Single(preview.GetProperty("blockers").EnumerateArray());
        Assert.Equal(("asset_loan", 1), (S(blocker, "key"), blocker.GetProperty("count").GetInt32()));

        (await app.RawAsync(app.AdultA, HttpMethod.Patch, $"{Loans}/{loan}", new { clearAsset = true })).EnsureSuccessStatusCode();
        preview = await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, $"/api/v1/assets/{asset}/deletion"));
        Assert.Empty(preview.GetProperty("blockers").EnumerateArray());
    }

    [Fact]
    public async Task Editing_another_field_keeps_a_link_to_an_asset_the_writer_cannot_read()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var hidden = await AssetAsync(app, "Voiture de B", "private", app.AdultB);
        var body = Golden(app, "Prêt partagé", "shared", app.AdultB);
        body["shareLevel"] = "edit";
        body["assetId"] = hidden;
        var loan = await NewAsync(app, body, app.AdultB);

        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Patch, $"{Loans}/{loan}", new { lender = "Autre banque" })).StatusCode);

        var seenByA = await DetailAsync(app, loan, app.AdultA);
        Assert.Equal(hidden, seenByA.GetProperty("assetId").GetGuid());
        Assert.Equal(JsonValueKind.Null, seenByA.GetProperty("assetName").ValueKind);
        Assert.Equal("Autre banque", S(seenByA, "lender"));
    }
}
