using System.Net;
using System.Text.Json;
using Nid.Banking.Contracts;
using Nid.TestSupport;

namespace Nid.Banking.IntegrationTests;

/// <summary>#346: « Créer une enveloppe » in « Régler » makes the envelope and links the account in one transaction.</summary>
public class AccountEnvelopeTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static Task<HttpResponseMessage> CreateAsync(NidApp app, Nid.Core.Contracts.PersonId actor, Guid account, object body)
        => app.RawAsync(actor, HttpMethod.Post, $"{BankingApi.Root}/accounts/{account}/envelope", body);

    private static async Task<Dictionary<string, Guid>> AccountsAsync(NidApp app)
        => (await BankingApi.AccountsAsync(app, app.AdultA)).EnumerateArray().ToDictionary(a => a.GetProperty("name").GetString()!, a => a.GetProperty("id").GetGuid());

    private static async Task<List<JsonElement>> EnvelopesAsync(NidApp app)
        => JsonDocument.Parse(await app.GetRawAsync(app.AdultA, "/api/v1/investments/envelopes")).RootElement.GetProperty("items").EnumerateArray().ToList();

    [Fact]
    public async Task Creates_the_envelope_from_the_account_and_links_it()
    {
        await using var app = await BankingApi.StartAsync(fixture);
        await BankingApi.ConnectAsync(app, app.AdultA, "code-a",
            BankingApi.Account("uid-1", "hash-1", "FR7600000000000000000001234", "Courant", currency: "USD"),
            BankingApi.Account("uid-2", "hash-2", null, "Joint"));
        var accounts = await AccountsAsync(app);

        var response = await CreateAsync(app, app.AdultA, accounts["Courant"], new { displayName = "Banque Alpha Courant", kind = "checking" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var id = (await BankingApi.JsonAsync(response)).GetProperty("id").GetGuid();
        var joint = await CreateAsync(app, app.AdultA, accounts["Joint"], new { displayName = "Banque Alpha Joint", kind = "checking", joint = true });
        Assert.Equal(HttpStatusCode.Created, joint.StatusCode);
        var jointId = (await BankingApi.JsonAsync(joint)).GetProperty("id").GetGuid();

        var linked = (await BankingApi.AccountsAsync(app, app.AdultA)).EnumerateArray().ToDictionary(a => a.GetProperty("name").GetString()!);
        Assert.Equal(id, linked["Courant"].GetProperty("envelopeId").GetGuid());
        Assert.Equal(jointId, linked["Joint"].GetProperty("envelopeId").GetGuid());
        var envelopes = (await EnvelopesAsync(app)).ToDictionary(e => e.GetProperty("id").GetGuid());
        var mine = envelopes[id];
        Assert.Equal(("Banque Alpha Courant", "checking", "USD"),
            (mine.GetProperty("displayName").GetString(), mine.GetProperty("kind").GetString(), mine.GetProperty("currency").GetString()));
        Assert.Equal([(app.AdultA.Value, 10_000)], Owners(mine));
        Assert.Equal([(app.AdultA.Value, 5_000), (app.AdultB.Value, 5_000)], Owners(envelopes[jointId]).OrderBy(o => o.Item1 == app.AdultB.Value));
    }

    [Fact]
    public async Task A_refused_link_leaves_no_envelope_behind()
    {
        await using var app = await BankingApi.StartAsync(fixture);
        await BankingApi.ConnectAsync(app, app.AdultA, "code-a",
            BankingApi.Account("uid-1", "hash-1", null, "Courant"), BankingApi.Account("uid-2", "hash-2", null, "Carte", "CARD"));
        var accounts = await AccountsAsync(app);
        var before = (await EnvelopesAsync(app)).Count;

        // The envelope is made, then the link refused: a card takes no envelope, a PEA no bank account.
        await BankingApi.AssertRefusedAsync(await CreateAsync(app, app.AdultA, accounts["Carte"], new { displayName = "Carte", kind = "checking" }),
            HttpStatusCode.UnprocessableEntity, BankingCodes.EnvelopeKind);
        await BankingApi.AssertRefusedAsync(await CreateAsync(app, app.AdultA, accounts["Courant"], new { displayName = "PEA", kind = "pea" }),
            HttpStatusCode.UnprocessableEntity, BankingCodes.EnvelopeKind);
        // Refused before anything is made: B does not see A's account.
        await BankingApi.AssertRefusedAsync(await CreateAsync(app, app.AdultB, accounts["Courant"], new { displayName = "Le mien", kind = "checking" }),
            HttpStatusCode.NotFound, BankingCodes.AccountNotFound);
        // The envelope's own refusal is on its field.
        var unnamed = await CreateAsync(app, app.AdultA, accounts["Courant"], new { displayName = "", kind = "checking" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, unnamed.StatusCode);

        Assert.Equal(before, (await EnvelopesAsync(app)).Count);
        Assert.All((await BankingApi.AccountsAsync(app, app.AdultA)).EnumerateArray(), a => Assert.Equal(JsonValueKind.Null, a.GetProperty("envelopeId").ValueKind));
    }

    private static List<(Guid, int)> Owners(JsonElement envelope)
        => envelope.GetProperty("owners").EnumerateArray().Select(o => (o.GetProperty("personId").GetGuid(), o.GetProperty("shareBp").GetInt32())).ToList();
}
