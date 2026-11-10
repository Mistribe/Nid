using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nid.Banking.Application;
using Nid.Banking.Contracts;
using Nid.Banking.Infrastructure.Persistence;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.TestSupport;
using NodaTime;
using Duration = NodaTime.Duration;

namespace Nid.Banking.IntegrationTests;

/// <summary>#23: an item delete removes the bank links and rules aimed at it and frees its account.</summary>
public class BankingDeletionPartTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task Counts_and_removes_bank_links_rules_and_account()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var app = await NidApp.StartAsync(fixture);
        await using var scope = app.Services.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        sp.GetRequiredService<IActorAccessor>().Current = Actor.System;
        var now = NidApp.GoldenCaseToday.AtMidnight().InUtc().ToInstant();
        var connection = await sp.GetRequiredService<IConnectionStore>().CreatePendingAsync(app.AdultA, "Banque", "FR", [1, 2, 3], now + Duration.FromMinutes(15), ct);
        await sp.GetRequiredService<IConnectionStore>().ActivateAsync(connection, "session", now + Duration.FromDays(180), ct);
        var account = (await sp.GetRequiredService<IAccountStore>()
            .UpsertFromSessionAsync(connection, [new SessionAccount("uid", "hash", "FR7630006000011234567890189", "Compte", "CACC", "EUR")], ct))[0].Id;
        var transactions = sp.GetRequiredService<ITransactionStore>();
        await transactions.AddAsync(account, [new NewBankTransaction("a", new LocalDate(2026, 10, 12), null, -13.49m, Currency.Eur, "PRLV SEPA NETFLIX", null, null, "api")], ct);
        var line = Assert.Single(await transactions.ListAsync([account], null, null, 0, 10, ct));
        var item = ItemId.New();
        await sp.GetRequiredService<ILinkStore>().SetAsync(
            new BankLink(line.Id, "subscriptions", item, "plan", null, LinkOutcome.AsExpected, null, "job", now), TransactionLinkState.Linked, ct);
        await sp.GetRequiredService<IRuleStore>().UpsertAsync(account, "NETFLIX", "subscriptions", item, app.AdultA, ct);
        var db = sp.GetRequiredService<BankingDbContext>();
        var associated = await db.Accounts.SingleAsync(a => a.Id == account, ct);
        (associated.EnvelopeItemId, associated.AssociatedOn, associated.AssociatedByPersonId) = (item.Value, new LocalDate(2026, 10, 1), app.AdultA.Value);
        await db.SaveChangesAsync(ct);

        var part = new BankingDeletionPart(db);
        var access = new ItemAccess(item, "Netflix", Visibility.Shared, []);
        Assert.Equal([("bank_links", 1), ("bank_rules", 1), ("bank_account", 1)], (await part.CountAsync(access, ct)).Select(c => (c.Key, c.Count)));

        await part.RemoveAsync(access, ct);

        Assert.Equal([("bank_links", 0), ("bank_rules", 0), ("bank_account", 0)], (await part.CountAsync(access, ct)).Select(c => (c.Key, c.Count)));
        var freed = await db.Accounts.AsNoTracking().SingleAsync(a => a.Id == account, ct);
        Assert.Null(freed.EnvelopeItemId);
        Assert.Null(freed.AssociatedOn);
        Assert.Null(freed.AssociatedByPersonId);
    }
}
