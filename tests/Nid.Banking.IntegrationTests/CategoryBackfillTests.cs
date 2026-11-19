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

/// <summary>Transaction categories spec: the lines linked before categories existed get their link's category.</summary>
public class CategoryBackfillTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static readonly Instant At = new LocalDate(2026, 10, 29).At(new LocalTime(10, 0)).InUtc().ToInstant();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Guid Seed(string key) => CategorySeed.Find(key)!.Id;

    [Fact]
    public async Task Backfill_categorises_links_made_before_categories_and_leaves_others()
    {
        await using var app = await BankingApi.StartAsync(fixture);
        await BankingApi.ConnectAsync(app, app.AdultA, "code-a", [BankingApi.Account("uid-1", "hash-1", null, "Courant A")]);
        var account = (await BankingApi.AccountsAsync(app, app.AdultA)).EnumerateArray().Single().GetProperty("id").GetGuid();
        await using var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
        var transactions = scope.ServiceProvider.GetRequiredService<ITransactionStore>();
        var links = scope.ServiceProvider.GetRequiredService<ILinkStore>();
        var categories = scope.ServiceProvider.GetRequiredService<ICategoryStore>();
        var day = new LocalDate(2026, 10, 12);
        await transactions.AddAsync(account, new[] { "a", "b", "c" }.Select(r => new NewBankTransaction(r, day, null, -50m, Currency.Eur, "LIGNE " + r, null, null, "api")).ToList(), Ct);
        var lines = (await transactions.ListAsync([account], null, null, 0, 10, Ct)).OrderBy(t => t.ProviderRef).Select(t => t.Id).ToList();

        // No category key: the way links were made before categories existed.
        await links.SetAsync(new BankLink(lines[0], "childcare", ItemId.New(), null, null, LinkOutcome.AsExpected, null, "job", At), TransactionLinkState.Linked, Ct);
        await links.SetAsync(new BankLink(lines[1], null, null, null, account, LinkOutcome.InternalTransfer, null, "job", At), TransactionLinkState.Internal, Ct);
        await links.SetAsync(new BankLink(lines[2], null, null, null, account, LinkOutcome.InternalTransfer, null, "job", At), TransactionLinkState.Internal, Ct);
        await categories.SetForLinesAsync([lines[2]], Seed("misc"), CategorySource.Hand, "person:x", At, true, Ct);

        var backfill = scope.ServiceProvider.GetRequiredService<CategoryBackfill>();
        Assert.Equal(2, await backfill.RunAsync(Ct));

        async Task<(Guid?, CategorySource?)> Of(Guid id) => (await transactions.FindAsync(id, Ct)) is { } l ? (l.Category, l.CategorySource) : default;
        Assert.Equal((Seed("children.childcare"), CategorySource.Link), await Of(lines[0]));
        Assert.Equal((Seed("internal.transfer"), CategorySource.Link), await Of(lines[1]));
        Assert.Equal((Seed("misc"), CategorySource.Hand), await Of(lines[2]));
        Assert.Equal(0, await backfill.RunAsync(Ct));
    }
}
