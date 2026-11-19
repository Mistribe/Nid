using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Nid.Banking.Application.Spending;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.TestSupport;
using NodaTime;

namespace Nid.Banking.IntegrationTests;

/// <summary>#85 decision 2: which bank accounts « Foyer » and « Moi » count, for each adult.</summary>
public class SpendingAccountsTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static async Task<Dictionary<string, Guid>> AccountsAsync(NidApp app, PersonId person)
        => (await BankingApi.AccountsAsync(app, person)).EnumerateArray().ToDictionary(a => a.GetProperty("name").GetString()!, a => a.GetProperty("id").GetGuid());

    private static async Task PatchAsync(NidApp app, Guid account, object body)
        => Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Patch, $"{BankingApi.Root}/accounts/{account}", body)).StatusCode);

    private static async Task<string[]> ScopeAsync(NidApp app, PersonId person, VisibilityScope scope, Dictionary<string, Guid> names)
    {
        await using var services = app.Services.CreateAsyncScope();
        services.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.Browser(person);
        var ids = await services.ServiceProvider.GetRequiredService<SpendingAccounts>().ForAsync(scope, TestContext.Current.CancellationToken);
        return [.. names.Where(n => ids.Contains(n.Value)).Select(n => n.Key).Order()];
    }

    [Fact]
    public async Task Accounts_on_envelopes_follow_the_envelope_s_scope_and_cards_their_account()
    {
        await using var app = await BankingApi.StartAsync(fixture);
        await BankingApi.ConnectAsync(app, app.AdultA, "code-a",
            BankingApi.Account("uid-1", "hash-1", null, "Commun"), BankingApi.Account("uid-2", "hash-2", null, "Perso A"),
            BankingApi.Account("uid-3", "hash-3", null, "Carte A", "CARD"), BankingApi.Account("uid-4", "hash-4", null, "Compte de Léa"),
            BankingApi.Account("uid-5", "hash-5", null, "Ancien"), BankingApi.Account("uid-6", "hash-6", null, "Carte Ancien", "CARD"),
            BankingApi.Account("uid-7", "hash-7", null, "Clos"));
        var accounts = await AccountsAsync(app, app.AdultA);
        var shared = await app.NewEnvelopeAsync("Commun", Visibility.Shared, (app.AdultA, 5_000), (app.AdultB, 5_000));
        await PatchAsync(app, accounts["Commun"], new { envelopeId = shared.Value });
        await PatchAsync(app, accounts["Perso A"], new { envelopeId = (await app.NewEnvelopeAsync("Perso A", app.AdultA, Visibility.Private)).Value });
        await PatchAsync(app, accounts["Carte A"], new { cardOfAccountId = accounts["Perso A"] });
        await PatchAsync(app, accounts["Compte de Léa"], new { envelopeId = (await app.NewEnvelopeAsync("Léa", Visibility.Shared, (app.Dependent, 10_000))).Value });
        var old = await app.NewEnvelopeAsync("Ancien", Visibility.Shared, (app.AdultA, 5_000), (app.AdultB, 5_000));
        await PatchAsync(app, accounts["Carte Ancien"], new { cardOfAccountId = accounts["Ancien"] });
        await PatchAsync(app, accounts["Ancien"], new { envelopeId = old.Value, ignored = true });
        // An archived envelope's account still counts: its past months were spent.
        var closed = await app.NewEnvelopeAsync("Clos", Visibility.Shared, (app.AdultA, 5_000), (app.AdultB, 5_000));
        await PatchAsync(app, accounts["Clos"], new { envelopeId = closed.Value });
        await using (var services = app.Services.CreateAsyncScope())
        {
            services.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.Browser(app.AdultA);
            Assert.True((await services.ServiceProvider.GetRequiredService<IItemRegistry>().ArchiveAsync(closed, Instant.FromUtc(2026, 6, 30, 12, 0), TestContext.Current.CancellationToken)).IsSuccess);
        }

        foreach (var adult in new[] { app.AdultA, app.AdultB })
            Assert.Equal(["Clos", "Commun", "Compte de Léa"], await ScopeAsync(app, adult, VisibilityScope.Household, accounts));
        Assert.Equal(["Carte A", "Clos", "Commun", "Perso A"], await ScopeAsync(app, app.AdultA, VisibilityScope.Mine, accounts));
        Assert.Equal(["Clos", "Commun"], await ScopeAsync(app, app.AdultB, VisibilityScope.Mine, accounts));
        // Me keeps the child's account, which Mine leaves out (#360).
        Assert.Equal(["Carte A", "Clos", "Commun", "Compte de Léa", "Perso A"], await ScopeAsync(app, app.AdultA, VisibilityScope.Me, accounts));
    }

    [Fact]
    public async Task An_account_on_no_envelope_is_the_household_s_when_every_adult_reads_it()
    {
        await using var app = await BankingApi.StartAsync(fixture);
        await BankingApi.ConnectAsync(app, app.AdultA, "code-a",
            BankingApi.Account("uid-1", "hash-1", "FR7630006000011234567890189", "Joint"), BankingApi.Account("uid-2", "hash-2", null, "Solo"),
            BankingApi.Account("uid-3", "hash-3", null, "Carte J", "CARD"), BankingApi.Account("uid-4", "hash-4", null, "Ignoré"));
        await BankingApi.ConnectAsync(app, app.AdultB, "code-b", BankingApi.Account("uid-b", "hash-b", "FR7630006000011234567890189", "Joint"));
        var accounts = await AccountsAsync(app, app.AdultA);
        await PatchAsync(app, accounts["Carte J"], new { cardOfAccountId = accounts["Joint"] });
        await PatchAsync(app, accounts["Ignoré"], new { ignored = true });

        foreach (var adult in new[] { app.AdultA, app.AdultB })
            Assert.Equal(["Carte J", "Joint"], await ScopeAsync(app, adult, VisibilityScope.Household, accounts));
        Assert.Equal(["Carte J", "Joint", "Solo"], await ScopeAsync(app, app.AdultA, VisibilityScope.Mine, accounts));
        Assert.Equal(["Carte J", "Joint"], await ScopeAsync(app, app.AdultB, VisibilityScope.Mine, accounts));
    }
}
