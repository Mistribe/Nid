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
    public async Task The_envelope_takes_the_visibility_asked_and_a_child_s_cannot_be_private()
    {
        await using var app = await BankingApi.StartAsync(fixture);
        await BankingApi.ConnectAsync(app, app.AdultA, "code-a",
            BankingApi.Account("uid-1", "hash-1", null, "Courant"), BankingApi.Account("uid-2", "hash-2", null, "Compte de Léa"),
            BankingApi.Account("uid-3", "hash-3", null, "Joint"));
        var accounts = await AccountsAsync(app);
        var before = (await EnvelopesAsync(app)).Count;

        // #383: neither a child's nor the adults' joint envelope can be private; nothing is made.
        await BankingApi.AssertRefusedAsync(await CreateAsync(app, app.AdultA, accounts["Compte de Léa"],
            new { displayName = "Léa", kind = "checking", dependentId = app.Dependent.Value, visibility = "private" }),
            HttpStatusCode.UnprocessableEntity, "core.private_dependent");
        await BankingApi.AssertRefusedAsync(await CreateAsync(app, app.AdultA, accounts["Joint"],
            new { displayName = "Joint", kind = "checking", joint = true, visibility = "private" }),
            HttpStatusCode.UnprocessableEntity, "core.private_single_owner");
        Assert.Equal(before, (await EnvelopesAsync(app)).Count);

        var response = await CreateAsync(app, app.AdultA, accounts["Courant"], new { displayName = "Courant", kind = "checking", visibility = "private" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var id = (await BankingApi.JsonAsync(response)).GetProperty("id").GetGuid();
        // A private item shows in « Moi » only, and never to B.
        var mine = JsonDocument.Parse(await app.GetRawAsync(app.AdultA, "/api/v1/investments/envelopes?scope=me")).RootElement.GetProperty("items").EnumerateArray();
        Assert.Equal("private", mine.Single(e => e.GetProperty("id").GetGuid() == id).GetProperty("visibility").GetString());
        foreach (var scope in new[] { "me", "household" })
            Assert.DoesNotContain(JsonDocument.Parse(await app.GetRawAsync(app.AdultB, $"/api/v1/investments/envelopes?scope={scope}")).RootElement
                .GetProperty("items").EnumerateArray(), e => e.GetProperty("id").GetGuid() == id);
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

    [Fact]
    public async Task A_dependent_s_account_is_set_by_either_adult()
    {
        await using var app = await BankingApi.StartAsync(fixture);
        await BankingApi.ConnectAsync(app, app.AdultA, "code-a",
            BankingApi.Account("uid-1", "hash-1", null, "Compte de Léa"), BankingApi.Account("uid-2", "hash-2", null, "Carte de Léa", "CARD"),
            BankingApi.Account("uid-3", "hash-3", null, "Courant A"));
        var accounts = await AccountsAsync(app);
        // Unassociated, it is its connector's alone: B does not see it.
        await BankingApi.AssertRefusedAsync(await CreateAsync(app, app.AdultB, accounts["Compte de Léa"], new { displayName = "Léa", kind = "checking" }),
            HttpStatusCode.NotFound, BankingCodes.AccountNotFound);
        // A non-dependent is no child to give it to.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await CreateAsync(app, app.AdultA, accounts["Compte de Léa"],
            new { displayName = "Léa", kind = "checking", dependentId = app.AdultB.Value })).StatusCode);

        var response = await CreateAsync(app, app.AdultA, accounts["Compte de Léa"], new { displayName = "Compte de Léa", kind = "checking", dependentId = app.Dependent.Value });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = (await BankingApi.JsonAsync(response)).GetProperty("id").GetGuid();
        var envelope = (await EnvelopesAsync(app)).Single(e => e.GetProperty("id").GetGuid() == created);
        Assert.Equal("shared", envelope.GetProperty("visibility").GetString());
        Assert.Equal([(app.Dependent.Value, 10_000)], Owners(envelope));
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Patch, $"{BankingApi.Root}/accounts/{accounts["Carte de Léa"]}",
            new { cardOfAccountId = accounts["Compte de Léa"] })).StatusCode);

        // B, who connected nothing, sets every setting of it and of its card, and moves it to another envelope of the child's.
        var seen = (await BankingApi.AccountsAsync(app, app.AdultB)).EnumerateArray().ToDictionary(a => a.GetProperty("name").GetString()!);
        Assert.Equal((true, true), (seen["Compte de Léa"].GetProperty("canWrite").GetBoolean(), seen["Carte de Léa"].GetProperty("canWrite").GetBoolean()));
        Assert.False(seen.ContainsKey("Courant A"));
        var other = await app.NewEnvelopeAsync("Livret de Léa", Nid.Core.Contracts.Visibility.Shared, (app.Dependent, 10_000));
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultB, HttpMethod.Patch, $"{BankingApi.Root}/accounts/{accounts["Compte de Léa"]}",
            new { name = "Léa", autoApplyBalance = true, envelopeId = other.Value })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultB, HttpMethod.Patch, $"{BankingApi.Root}/accounts/{accounts["Carte de Léa"]}",
            new { ignored = true, cardOfAccountId = accounts["Compte de Léa"] })).StatusCode);
        var after = (await BankingApi.AccountsAsync(app, app.AdultA)).EnumerateArray().ToDictionary(a => a.GetProperty("id").GetGuid());
        Assert.Equal((other.Value, true), (after[accounts["Compte de Léa"]].GetProperty("envelopeId").GetGuid(), after[accounts["Compte de Léa"]].GetProperty("autoApplyBalance").GetBoolean()));
        Assert.True(after[accounts["Carte de Léa"]].GetProperty("ignored").GetBoolean());

        // An account on the adults' joint envelope stays its connector's to move: B writes it, but does not own it.
        var mine = await app.NewEnvelopeAsync("Commun", Nid.Core.Contracts.Visibility.Shared, (app.AdultA, 5_000), (app.AdultB, 5_000));
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Patch, $"{BankingApi.Root}/accounts/{accounts["Courant A"]}", new { envelopeId = mine.Value })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await app.RawAsync(app.AdultB, HttpMethod.Patch, $"{BankingApi.Root}/accounts/{accounts["Courant A"]}",
            new { envelopeId = other.Value })).StatusCode);
    }

    private static List<(Guid, int)> Owners(JsonElement envelope)
        => envelope.GetProperty("owners").EnumerateArray().Select(o => (o.GetProperty("personId").GetGuid(), o.GetProperty("shareBp").GetInt32())).ToList();
}
