using System.Net;
using System.Text.Json;
using Nid.Banking.Contracts;
using Nid.Core.Contracts;
using Nid.TestSupport;

namespace Nid.Banking.IntegrationTests;

/// <summary>Bank sync D7, D22 and spec §2.3: associating a bank account with an envelope, and who reads it.</summary>
public class AccountTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static async Task<ItemId> EnvelopeAsync(NidApp app, PersonId owner, string kind, Visibility visibility = Visibility.Private)
    {
        var response = await app.RawAsync(owner, HttpMethod.Post, "/api/v1/investments/envelopes", new
        {
            displayName = kind, kind, currency = "EUR", visibility = visibility == Visibility.Private ? "private" : "shared",
            owners = new[] { new { personId = owner.Value, shareBp = 10_000 } },
        });
        response.EnsureSuccessStatusCode();
        return new ItemId((await BankingApi.JsonAsync(response)).GetProperty("id").GetGuid());
    }

    private static Task<HttpResponseMessage> PatchAsync(NidApp app, PersonId actor, Guid account, object body)
        => app.RawAsync(actor, HttpMethod.Patch, $"{BankingApi.Root}/accounts/{account}", body);

    private static async Task<Dictionary<string, JsonElement>> ByNameAsync(NidApp app, PersonId actor, ItemId? envelope = null)
        => (await BankingApi.AccountsAsync(app, actor, envelope)).EnumerateArray().ToDictionary(a => a.GetProperty("name").GetString()!);

    [Fact]
    public async Task Associate_requires_write_on_a_checking_or_savings_envelope()
    {
        await using var app = await BankingApi.StartAsync(fixture);
        await BankingApi.ConnectAsync(app, app.AdultA, "code-a",
            BankingApi.Account("uid-1", "hash-1", null, "Courant"), BankingApi.Account("uid-2", "hash-2", null, "Livret", "SVGS"));
        await BankingApi.ConnectAsync(app, app.AdultB, "code-b", BankingApi.Account("uid-3", "hash-3", null, "Courant de B"));
        var accounts = await ByNameAsync(app, app.AdultA);
        var courant = accounts["Courant"].GetProperty("id").GetGuid();
        var livret = accounts["Livret"].GetProperty("id").GetGuid();
        var ofB = (await ByNameAsync(app, app.AdultB))["Courant de B"].GetProperty("id").GetGuid();

        await BankingApi.AssertRefusedAsync(await PatchAsync(app, app.AdultA, courant, new { envelopeId = (await EnvelopeAsync(app, app.AdultA, "pea")).Value }),
            HttpStatusCode.UnprocessableEntity, BankingCodes.EnvelopeKind);
        Assert.Equal(HttpStatusCode.NotFound,
            (await PatchAsync(app, app.AdultA, courant, new { envelopeId = (await EnvelopeAsync(app, app.AdultB, "checking")).Value })).StatusCode);
        // B reads A's shared envelope but does not write it.
        var shared = await EnvelopeAsync(app, app.AdultA, "checking", Visibility.Shared);
        Assert.Equal(HttpStatusCode.Forbidden, (await PatchAsync(app, app.AdultB, ofB, new { envelopeId = shared.Value })).StatusCode);
        // Nor does B see A's unassociated account.
        await BankingApi.AssertRefusedAsync(await PatchAsync(app, app.AdultB, courant, new { ignored = true }), HttpStatusCode.NotFound, BankingCodes.AccountNotFound);

        Assert.Equal(HttpStatusCode.OK, (await PatchAsync(app, app.AdultA, courant, new { envelopeId = shared.Value })).StatusCode);
        await BankingApi.AssertRefusedAsync(await PatchAsync(app, app.AdultA, livret, new { envelopeId = shared.Value }),
            HttpStatusCode.Conflict, BankingCodes.EnvelopeTaken);
        var savings = await EnvelopeAsync(app, app.AdultA, "savings");
        Assert.Equal(HttpStatusCode.OK, (await PatchAsync(app, app.AdultA, livret, new { envelopeId = savings.Value, ignored = true })).StatusCode);

        accounts = await ByNameAsync(app, app.AdultA);
        Assert.Equal((shared.Value, false), (accounts["Courant"].GetProperty("envelopeId").GetGuid(), accounts["Courant"].GetProperty("ignored").GetBoolean()));
        Assert.Equal((savings.Value, true), (accounts["Livret"].GetProperty("envelopeId").GetGuid(), accounts["Livret"].GetProperty("ignored").GetBoolean()));

        Assert.Equal(HttpStatusCode.OK, (await PatchAsync(app, app.AdultA, courant, new { clearEnvelope = true })).StatusCode);
        Assert.Equal(JsonValueKind.Null, (await ByNameAsync(app, app.AdultA))["Courant"].GetProperty("envelopeId").ValueKind);
        // A key nobody reads is refused.
        Assert.Equal(HttpStatusCode.BadRequest, (await PatchAsync(app, app.AdultA, courant, new { envelope = shared.Value })).StatusCode);
    }

    [Fact]
    public async Task An_account_goes_with_an_envelope_of_its_currency()
    {
        await using var app = await BankingApi.StartAsync(fixture);
        await BankingApi.ConnectAsync(app, app.AdultA, "code-a", BankingApi.Account("uid-1", "hash-1", null, "Dollars", currency: "USD"));
        var account = (await ByNameAsync(app, app.AdultA))["Dollars"].GetProperty("id").GetGuid();

        var refused = await PatchAsync(app, app.AdultA, account, new { envelopeId = (await EnvelopeAsync(app, app.AdultA, "checking")).Value });

        await BankingApi.AssertRefusedAsync(refused, HttpStatusCode.UnprocessableEntity, BankingCodes.CurrencyMismatch);
        Assert.Equal("envelopeId", (await BankingApi.JsonAsync(refused)).GetProperty("errors")[0].GetProperty("path").GetString());
        Assert.Equal(HttpStatusCode.OK, (await PatchAsync(app, app.AdultA, account, new { envelopeId = (await app.NewEnvelopeAsync("Dollars", app.AdultA, Visibility.Private, "USD")).Value })).StatusCode);
    }

    [Fact]
    public async Task Only_a_card_is_attached_and_only_to_a_checking_account_and_it_can_be_detached()
    {
        await using var app = await BankingApi.StartAsync(fixture);
        await BankingApi.ConnectAsync(app, app.AdultA, "code-a",
            BankingApi.Account("uid-1", "hash-1", null, "Courant"), BankingApi.Account("uid-2", "hash-2", null, "Livret", "SVGS"),
            BankingApi.Account("uid-3", "hash-3", null, "Carte", "CARD"));
        var ofA = await ByNameAsync(app, app.AdultA);
        Guid Id(string name) => ofA[name].GetProperty("id").GetGuid();

        foreach (var (account, parent) in new[] { ("Livret", "Courant"), ("Carte", "Livret") })
        {
            var refused = await PatchAsync(app, app.AdultA, Id(account), new { cardOfAccountId = Id(parent) });
            await BankingApi.AssertRefusedAsync(refused, HttpStatusCode.UnprocessableEntity, BankingCodes.CardParent);
            Assert.Equal("cardOfAccountId", (await BankingApi.JsonAsync(refused)).GetProperty("errors")[0].GetProperty("path").GetString());
        }

        Assert.Equal(HttpStatusCode.OK, (await PatchAsync(app, app.AdultA, Id("Carte"), new { cardOfAccountId = Id("Courant") })).StatusCode);
        Assert.Equal(Id("Courant"), (await ByNameAsync(app, app.AdultA))["Carte"].GetProperty("cardOfAccountId").GetGuid());
        Assert.Equal(HttpStatusCode.OK, (await PatchAsync(app, app.AdultA, Id("Carte"), new { clearCard = true })).StatusCode);
        Assert.Equal(JsonValueKind.Null, (await ByNameAsync(app, app.AdultA))["Carte"].GetProperty("cardOfAccountId").ValueKind);
    }

    [Fact]
    public async Task An_unassociated_account_stays_its_first_connection_creators_once_the_connection_is_gone()
    {
        await using var app = await BankingApi.StartAsync(fixture);
        var connection = await BankingApi.ConnectAsync(app, app.AdultA, "code-a", BankingApi.Account("uid-1", "hash-1", null, "Courant"));
        var account = (await ByNameAsync(app, app.AdultA))["Courant"].GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.NoContent, (await app.RawAsync(app.AdultA, HttpMethod.Delete, $"{BankingApi.Root}/connections/{connection}")).StatusCode);

        var kept = (await ByNameAsync(app, app.AdultA))["Courant"];
        Assert.Equal((account, false, true), (kept.GetProperty("id").GetGuid(), kept.GetProperty("synced").GetBoolean(), kept.GetProperty("canWrite").GetBoolean()));
        var envelope = await EnvelopeAsync(app, app.AdultA, "checking");
        Assert.Equal(HttpStatusCode.OK, (await PatchAsync(app, app.AdultA, account, new { envelopeId = envelope.Value })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PatchAsync(app, app.AdultA, account, new { clearEnvelope = true })).StatusCode);
        Assert.Equal(0, (await BankingApi.AccountsAsync(app, app.AdultB)).GetArrayLength());
        await BankingApi.AssertRefusedAsync(await PatchAsync(app, app.AdultB, account, new { ignored = true }), HttpStatusCode.NotFound, BankingCodes.AccountNotFound);
    }

    [Fact]
    public async Task Account_visibility_follows_the_envelope()
    {
        await using var app = await BankingApi.StartAsync(fixture);
        await BankingApi.ConnectAsync(app, app.AdultA, "code-a",
            BankingApi.Account("uid-1", "hash-1", null, "Joint"), BankingApi.Account("uid-2", "hash-2", null, "Commun", "SVGS"),
            BankingApi.Account("uid-3", "hash-3", null, "Perso"), BankingApi.Account("uid-4", "hash-4", null, "Carte", "CARD"),
            BankingApi.Account("uid-5", "hash-5", null, "Libre"));
        var ofA = await ByNameAsync(app, app.AdultA);
        Guid Id(string name) => ofA[name].GetProperty("id").GetGuid();
        var mine = await EnvelopeAsync(app, app.AdultA, "checking", Visibility.Shared); // B reads, A writes
        var both = await app.NewEnvelopeAsync("Commun", Visibility.Shared, (app.AdultA, 5_000), (app.AdultB, 5_000));
        var secret = await EnvelopeAsync(app, app.AdultA, "checking");
        foreach (var (account, envelope) in new[] { ("Joint", mine), ("Commun", both), ("Perso", secret) })
            Assert.Equal(HttpStatusCode.OK, (await PatchAsync(app, app.AdultA, Id(account), new { envelopeId = envelope.Value })).StatusCode);

        var joint = Assert.Single((await BankingApi.AccountsAsync(app, app.AdultB, mine)).EnumerateArray());
        Assert.Equal((Id("Joint"), false), (joint.GetProperty("id").GetGuid(), joint.GetProperty("canWrite").GetBoolean()));
        Assert.True(Assert.Single((await BankingApi.AccountsAsync(app, app.AdultA, mine)).EnumerateArray()).GetProperty("canWrite").GetBoolean());
        Assert.True(Assert.Single((await BankingApi.AccountsAsync(app, app.AdultB, both)).EnumerateArray()).GetProperty("canWrite").GetBoolean());
        Assert.Equal(0, (await BankingApi.AccountsAsync(app, app.AdultB, secret)).GetArrayLength());
        Assert.Equal(["Commun", "Joint"], (await ByNameAsync(app, app.AdultB)).Keys.Order());
        Assert.Equal(0, (await BankingApi.ConnectionsAsync(app, app.AdultB)).GetArrayLength());
        Assert.DoesNotContain("Perso", await app.GetRawAsync(app.AdultB, $"{BankingApi.Root}/accounts"));
        Assert.DoesNotContain("Banque Alpha", await app.GetRawAsync(app.AdultB, $"{BankingApi.Root}/accounts"));

        // A card takes its account's visibility, and its writers.
        Assert.Equal(HttpStatusCode.OK, (await PatchAsync(app, app.AdultA, Id("Carte"), new { cardOfAccountId = Id("Joint") })).StatusCode);
        var card = (await ByNameAsync(app, app.AdultB))["Carte"];
        Assert.Equal((Id("Joint"), false), (card.GetProperty("cardOfAccountId").GetGuid(), card.GetProperty("canWrite").GetBoolean()));
        Assert.Equal(HttpStatusCode.Forbidden, (await PatchAsync(app, app.AdultB, Id("Carte"), new { ignored = true })).StatusCode);
        // Attached to the account on A's private envelope, it is A's alone again.
        Assert.Equal(HttpStatusCode.OK, (await PatchAsync(app, app.AdultA, Id("Carte"), new { cardOfAccountId = Id("Perso") })).StatusCode);
        Assert.Equal(["Commun", "Joint"], (await ByNameAsync(app, app.AdultB)).Keys.Order());
        // A card is attached to an account its author may write.
        await BankingApi.AssertRefusedAsync(await PatchAsync(app, app.AdultA, Id("Carte"), new { cardOfAccountId = Guid.NewGuid() }),
            HttpStatusCode.NotFound, BankingCodes.AccountNotFound);
    }

    [Fact]
    public async Task Only_whose_bank_reads_an_account_moves_it_to_another_envelope()
    {
        await using var app = await BankingApi.StartAsync(fixture);
        await BankingApi.ConnectAsync(app, app.AdultA, "code-a",
            BankingApi.Account("uid-1", "hash-1", null, "Commun"), BankingApi.Account("uid-2", "hash-2", "FR7630006000011234567890189", "Joint"));
        var ofA = await ByNameAsync(app, app.AdultA);
        Guid Id(string name) => ofA[name].GetProperty("id").GetGuid();
        var both = await app.NewEnvelopeAsync("Commun", Visibility.Shared, (app.AdultA, 5_000), (app.AdultB, 5_000));
        var jointEnvelope = await app.NewEnvelopeAsync("Joint", Visibility.Shared, (app.AdultA, 5_000), (app.AdultB, 5_000));
        Assert.Equal(HttpStatusCode.OK, (await PatchAsync(app, app.AdultA, Id("Commun"), new { envelopeId = both.Value })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PatchAsync(app, app.AdultA, Id("Joint"), new { envelopeId = jointEnvelope.Value })).StatusCode);
        var ofB = await EnvelopeAsync(app, app.AdultB, "checking");

        // B writes the shared envelope, but moving A's account into B's private one would take it from A.
        Assert.Equal(HttpStatusCode.Forbidden, (await PatchAsync(app, app.AdultB, Id("Commun"), new { envelopeId = ofB.Value })).StatusCode);
        Assert.Equal(both.Value, (await ByNameAsync(app, app.AdultA))["Commun"].GetProperty("envelopeId").GetGuid());
        // B's own bank reads the joint account too: B may move it.
        await BankingApi.ConnectAsync(app, app.AdultB, "code-b", BankingApi.Account("uid-3", "hash-3", "FR7630006000011234567890189", "Joint"));
        Assert.Equal(HttpStatusCode.OK, (await PatchAsync(app, app.AdultB, Id("Joint"), new { envelopeId = ofB.Value })).StatusCode);
        // Taking it off the envelope gives it back to whose bank reads it.
        Assert.Equal(HttpStatusCode.OK, (await PatchAsync(app, app.AdultB, Id("Commun"), new { clearEnvelope = true })).StatusCode);
        Assert.True((await ByNameAsync(app, app.AdultA))["Commun"].GetProperty("canWrite").GetBoolean());
    }

    [Fact]
    public async Task A_card_values_no_envelope()
    {
        await using var app = await BankingApi.StartAsync(fixture);
        await BankingApi.ConnectAsync(app, app.AdultA, "code-a", BankingApi.Account("uid-1", "hash-1", null, "Carte", "CARD"));
        var card = (await ByNameAsync(app, app.AdultA))["Carte"].GetProperty("id").GetGuid();

        var refused = await PatchAsync(app, app.AdultA, card, new { envelopeId = (await EnvelopeAsync(app, app.AdultA, "checking")).Value });

        await BankingApi.AssertRefusedAsync(refused, HttpStatusCode.UnprocessableEntity, BankingCodes.EnvelopeKind);
        Assert.Equal("envelopeId", (await BankingApi.JsonAsync(refused)).GetProperty("errors")[0].GetProperty("path").GetString());
    }
}
