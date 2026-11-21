using System.Net;
using System.Text.Json;
using Nid.Core.Contracts;
using Nid.TestSupport;
using static Nid.TestSupport.LoanApi;
using static Nid.TestSupport.PropertyApi;

namespace Nid.RealEstate.IntegrationTests;

/// <summary>#180: a loan finances an SCPI envelope instead of a property.</summary>
public class ScpiLinkTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string Envelopes = "/api/v1/investments/envelopes";

    private static Dictionary<string, object?> Financing(Guid envelope, Dictionary<string, object?> loan)
    {
        loan["envelopeId"] = envelope;
        return loan;
    }

    private static async Task<Guid> ScpiAsync(NidApp app, string name = "SCPI Bureaux", Visibility visibility = Visibility.Shared, PersonId? owner = null)
        => (await app.NewEnvelopeAsync(name, owner ?? app.AdultA, visibility, kind: "scpi")).Value;

    private static async Task<JsonElement[]> ListAsync(NidApp app, string query, PersonId? actor = null)
        => (await JsonAsync(await app.RawAsync(actor ?? app.AdultA, HttpMethod.Get, $"{Loans}{query}"))).GetProperty("items").EnumerateArray().ToArray();

    [Fact]
    public async Task A_loan_finances_an_scpi_at_creation_moves_by_its_patch_and_is_cleared()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var first = await ScpiAsync(app);
        var second = await ScpiAsync(app, "SCPI Commerces");
        var loan = await NewAsync(app, Financing(first, Golden(app)));

        var detail = await DetailAsync(app, loan);
        Assert.Equal((first, "SCPI Bureaux"), (detail.GetProperty("envelopeId").GetGuid(), S(detail, "envelopeName")));
        Assert.Equal(JsonValueKind.Null, detail.GetProperty("propertyId").ValueKind);
        var version = detail.GetProperty("contract").GetProperty("version").GetUInt32();

        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Patch, $"{Loans}/{loan}", new { envelopeId = second })).StatusCode);
        detail = await DetailAsync(app, loan);
        Assert.Equal((second, "SCPI Commerces"), (detail.GetProperty("envelopeId").GetGuid(), S(detail, "envelopeName")));
        Assert.NotEqual(version, detail.GetProperty("contract").GetProperty("version").GetUInt32()); // the link is a column of the loan's row

        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Patch, $"{Loans}/{loan}", new { clearEnvelope = true })).StatusCode);
        detail = await DetailAsync(app, loan);
        Assert.Equal(JsonValueKind.Null, detail.GetProperty("envelopeId").ValueKind);
        Assert.Equal(JsonValueKind.Null, detail.GetProperty("envelopeName").ValueKind);
    }

    [Fact]
    public async Task A_savings_envelope_an_unreadable_scpi_a_property_and_an_unknown_id_all_answer_invalid_envelope()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var livret = (await app.NewEnvelopeAsync("Livret", app.AdultA, Visibility.Shared)).Value;
        var hidden = await ScpiAsync(app, "SCPI de B", Visibility.Private, app.AdultB);
        var home = await NewPropertyAsync(app, Home(app));
        var loan = await NewAsync(app, Golden(app));

        foreach (var target in new[] { livret, hidden, home, Guid.NewGuid() })
        {
            Assert.Equal((HttpStatusCode.UnprocessableEntity, "realestate.invalid_envelope", "envelopeId"),
                await RefusalAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, Loans, Financing(target, Golden(app, name: "Prêt lié")))));
            Assert.Equal((HttpStatusCode.UnprocessableEntity, "realestate.invalid_envelope", "envelopeId"),
                await RefusalAsync(await app.RawAsync(app.AdultA, HttpMethod.Patch, $"{Loans}/{loan}", new { envelopeId = target })));
        }
        Assert.Equal(JsonValueKind.Null, (await DetailAsync(app, loan)).GetProperty("envelopeId").ValueKind);
    }

    [Fact]
    public async Task A_loan_finances_a_property_or_an_scpi_never_both()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var scpi = await ScpiAsync(app);
        var home = await NewPropertyAsync(app, Home(app));
        var both = Financing(scpi, Golden(app));
        both["propertyId"] = home;
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "realestate.invalid_envelope", "envelopeId"),
            await RefusalAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, Loans, both)));

        var loan = await NewAsync(app, Golden(app));
        (await app.RawAsync(app.AdultA, HttpMethod.Patch, $"{Loans}/{loan}", new { propertyId = home })).EnsureSuccessStatusCode();
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "realestate.invalid_envelope", "envelopeId"),
            await RefusalAsync(await app.RawAsync(app.AdultA, HttpMethod.Patch, $"{Loans}/{loan}", new { envelopeId = scpi })));

        // Moved in one patch: the property's link goes as the envelope's comes.
        Assert.Equal(HttpStatusCode.OK,
            (await app.RawAsync(app.AdultA, HttpMethod.Patch, $"{Loans}/{loan}", new { envelopeId = scpi, clearProperty = true })).StatusCode);
        var detail = await DetailAsync(app, loan);
        Assert.Equal((JsonValueKind.Null, scpi), (detail.GetProperty("propertyId").ValueKind, detail.GetProperty("envelopeId").GetGuid()));
    }

    [Fact]
    public async Task The_list_filters_the_loans_financing_an_envelope_and_names_only_a_readable_one()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var mine = await ScpiAsync(app, "SCPI privée", Visibility.Private);
        var other = await ScpiAsync(app, "SCPI Commerces");
        var loan = await NewAsync(app, Financing(mine, Golden(app))); // shared, borrowed by A
        await NewAsync(app, Financing(other, Golden(app, name: "Autre prêt")));
        await NewAsync(app, Golden(app, name: "Prêt sans lien"));

        var row = Assert.Single(await ListAsync(app, $"?envelope={mine}"));
        Assert.Equal((loan, "SCPI privée"), (row.GetProperty("id").GetGuid(), S(row, "envelopeName")));
        Assert.Equal(3, (await ListAsync(app, "")).Length);

        var seenByB = Assert.Single(await ListAsync(app, "", app.AdultB), r => r.GetProperty("id").GetGuid() == loan);
        Assert.Equal(mine, seenByB.GetProperty("envelopeId").GetGuid());
        Assert.Equal(JsonValueKind.Null, seenByB.GetProperty("envelopeName").ValueKind);
    }

    [Fact]
    public async Task A_financed_scpi_keeps_its_kind_and_refuses_its_delete_until_the_loan_lets_go()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var scpi = await ScpiAsync(app);
        var loan = await NewAsync(app, Financing(scpi, Golden(app)));

        var kindChange = await app.RawAsync(app.AdultA, HttpMethod.Patch, $"{Envelopes}/{scpi}", new { kind = "other" });
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "investments.envelope_financed", "kind"), await RefusalAsync(kindChange));

        var preview = await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Envelopes}/{scpi}/deletion"));
        var blocker = Assert.Single(preview.GetProperty("blockers").EnumerateArray());
        Assert.Equal(("scpi_loan", 1), (S(blocker, "key"), blocker.GetProperty("count").GetInt32()));

        (await app.RawAsync(app.AdultA, HttpMethod.Patch, $"{Loans}/{loan}", new { clearEnvelope = true })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Patch, $"{Envelopes}/{scpi}", new { kind = "other" })).StatusCode);
        preview = await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Envelopes}/{scpi}/deletion"));
        Assert.Empty(preview.GetProperty("blockers").EnumerateArray());
    }

    [Fact]
    public async Task The_envelope_list_filters_by_kind_on_the_server()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var scpi = await ScpiAsync(app);
        await app.NewEnvelopeAsync("Livret", app.AdultA, Visibility.Shared);

        var items = (await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Envelopes}?kind=scpi"))).GetProperty("items").EnumerateArray();
        Assert.Equal(scpi, Assert.Single(items).GetProperty("id").GetGuid());
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "investments.unknown_kind", "kind"),
            await RefusalAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Envelopes}?kind=villa")));
    }
}
