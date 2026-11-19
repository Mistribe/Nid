using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nid.Banking.Application;
using Nid.Banking.Application.Reconciliation;
using Nid.Banking.Contracts;
using Nid.Banking.Domain;
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

    [Fact]
    public async Task Deleting_a_linked_subscription_leaves_its_lines_to_the_next_pass()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var app = await BankingApi.StartAsync(fixture);
        await BankingApi.ConnectAsync(app, app.AdultA, "code-a", BankingApi.Account("uid-1", "hash-1", null, "Courant A"));
        var account = (await BankingApi.AccountsAsync(app, app.AdultA)).EnumerateArray().Single().GetProperty("id").GetGuid();
        var netflix = await app.NewSubscriptionAsync("Netflix", app.AdultA, Visibility.Shared);
        Guid line;
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var sp = scope.ServiceProvider;
            sp.GetRequiredService<IActorAccessor>().Current = Actor.System;
            var transactions = sp.GetRequiredService<ITransactionStore>();
            await transactions.AddAsync(account, [new NewBankTransaction("a", new LocalDate(2026, 10, 5), null, -12.99m, Currency.Eur, "PRLV SEPA NETFLIX 123", null, null, "api")], ct);
            line = Assert.Single(await transactions.ListAsync([account], null, null, 0, 10, ct)).Id;
            await sp.GetRequiredService<ICategoryRuleStore>().CreateAsync(
                new CategoryRule(Guid.Empty, "NETFLIX", null, null, null, null, CategorySeed.Find("home.telecom")!.Id, app.AdultA, default, null), ct);
        }
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Post, $"{BankingApi.Root}/transactions/{line}/link",
            new { module = "subscriptions", itemId = netflix.Value })).StatusCode);
        await PassAsync(app, ct);
        Assert.Equal(("linked", "link", true), await StateAsync(app, line, ct));

        var url = $"/api/v1/subscriptions/{netflix.Value}";
        var version = JsonDocument.Parse(await app.GetRawAsync(app.AdultA, $"{url}/deletion")).RootElement.GetProperty("version").GetUInt32();
        Assert.Equal(HttpStatusCode.NoContent, (await app.RawAsync(app.AdultA, HttpMethod.Delete, $"{url}?version={version}")).StatusCode);

        Assert.Equal(("unlinked", null, false), await StateAsync(app, line, ct));
        await PassAsync(app, ct);
        Assert.Equal(("unlinked", "rule", true), await StateAsync(app, line, ct));
    }

    private static async Task PassAsync(NidApp app, CancellationToken ct)
    {
        await using var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
        await scope.ServiceProvider.GetRequiredService<BankReconciliation>().RunAsync(null, new LocalDate(2026, 10, 29), ct);
    }

    private static async Task<(string LinkState, string? Source, bool Reconciled)> StateAsync(NidApp app, Guid line, CancellationToken ct)
    {
        await using var db = new BankingDbContext(new DbContextOptionsBuilder<BankingDbContext>().Configure(app.ConnectionString).Options);
        var row = await db.Transactions.AsNoTracking().SingleAsync(t => t.Id == line, ct);
        return (row.LinkState, row.CategorySource, row.ReconciledAt is not null);
    }
}
