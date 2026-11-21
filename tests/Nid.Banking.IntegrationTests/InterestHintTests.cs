using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Nid.Banking.Application;
using Nid.Banking.Application.Reconciliation;
using Nid.Banking.Contracts;
using Nid.Banking.Domain;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.TestSupport;
using NodaTime;

namespace Nid.Banking.IntegrationTests;

/// <summary>The interest label counts only on an account associated with a savings envelope: <c>CategoryRuleApplier.HintAsync</c>.</summary>
public class InterestHintTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task An_interest_credit_is_hinted_on_a_savings_account_only()
    {
        await using var app = await BankingApi.StartAsync(fixture);
        await BankingApi.ConnectAsync(app, app.AdultA, "code-a", BankingApi.Account("uid-1", "hash-1", null, "Livret Alice"),
            BankingApi.Account("uid-2", "hash-2", null, "Courant Alice"));
        var ids = (await BankingApi.AccountsAsync(app, app.AdultA)).EnumerateArray()
            .ToDictionary(a => a.GetProperty("name").GetString()!, a => a.GetProperty("id").GetGuid());
        foreach (var (name, kind) in new[] { ("Livret Alice", "savings"), ("Courant Alice", "checking") })
            Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Patch, $"{BankingApi.Root}/accounts/{ids[name]}",
                new { envelopeId = (await app.NewEnvelopeAsync(name, app.AdultA, Visibility.Shared, kind: kind)).Value })).StatusCode);

        await using var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
        var store = scope.ServiceProvider.GetRequiredService<ITransactionStore>();
        foreach (var account in ids.Values)
            await store.AddAsync(account, [new NewBankTransaction($"ref-{account}", new LocalDate(2026, 10, 5), null, 5m, Currency.Eur, "INTERETS 2026", null, null, "api")], TestContext.Current.CancellationToken);
        var lines = await store.ListAsync([.. ids.Values], null, null, 0, 100, TestContext.Current.CancellationToken);

        await scope.ServiceProvider.GetRequiredService<CategoryRuleApplier>().HintAsync(lines, TestContext.Current.CancellationToken);

        var after = await store.ListAsync([.. ids.Values], null, null, 0, 100, TestContext.Current.CancellationToken);
        Assert.Equal(CategorySource.Hint, after.Single(l => l.Account == ids["Livret Alice"]).CategorySource);
        Assert.Equal(CategorySeed.Find("income.interest")!.Id, after.Single(l => l.Account == ids["Livret Alice"]).Category);
        Assert.Null(after.Single(l => l.Account == ids["Courant Alice"]).Category);
    }
}
