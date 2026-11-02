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

/// <summary>Bank sync spec §2.6, §6.2: links, rules, recurring groups and the reconciliation mark.</summary>
public class LinkStoreTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static readonly LocalDate Day = new(2026, 10, 12);

    private static NewBankTransaction Line(string providerRef, decimal amount = -13.49m)
        => new(providerRef, Day, null, amount, Currency.Eur, "PRLV SEPA NETFLIX", null, null, "api");

    private static async Task<(AsyncServiceScope Scope, Guid Account)> SetUpAsync(NidApp app)
    {
        var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
        var connections = scope.ServiceProvider.GetRequiredService<IConnectionStore>();
        var now = NidApp.GoldenCaseToday.AtMidnight().InUtc().ToInstant();
        var id = await connections.CreatePendingAsync(app.AdultA, "Boursorama", "FR", [1, 2, 3], now + Duration.FromMinutes(15), default);
        await connections.ActivateAsync(id, "session", now + Duration.FromDays(180), default);
        var accounts = await scope.ServiceProvider.GetRequiredService<IAccountStore>()
            .UpsertFromSessionAsync(id, [new SessionAccount("uid", "hash", "FR7630006000011234567890189", "Compte", "CACC", "EUR")], default);
        return (scope, accounts[0].Id);
    }

    [Fact]
    public async Task A_failed_link_leaves_nothing_pending()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var (scope, account) = await SetUpAsync(app);
        await using var _ = scope;
        var transactions = scope.ServiceProvider.GetRequiredService<ITransactionStore>();
        var links = scope.ServiceProvider.GetRequiredService<ILinkStore>();
        await transactions.AddAsync(account, [Line("a")], TestContext.Current.CancellationToken);
        var line = Assert.Single(await transactions.ListAsync([account], null, null, 0, 10, TestContext.Current.CancellationToken));
        var at = NidApp.GoldenCaseToday.AtMidnight().InUtc().ToInstant();
        var unknown = Guid.NewGuid();
        await Assert.ThrowsAnyAsync<Exception>(() => links.SetAsync(
            new BankLink(unknown, null, null, null, account, LinkOutcome.InternalTransfer, null, "job", at), TransactionLinkState.Internal, TestContext.Current.CancellationToken));

        // The same scope's next write carries nothing of the failed one.
        await links.SetAsync(new BankLink(line.Id, null, null, null, account, LinkOutcome.InternalTransfer, null, "job", at), TransactionLinkState.Internal, TestContext.Current.CancellationToken);

        Assert.Equal([line.Id], (await links.ForTransactionsAsync([line.Id, unknown], TestContext.Current.CancellationToken)).Keys);
    }

    [Fact]
    public async Task A_link_sets_and_clears_the_line_state()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var (scope, account) = await SetUpAsync(app);
        await using var _ = scope;
        var transactions = scope.ServiceProvider.GetRequiredService<ITransactionStore>();
        var links = scope.ServiceProvider.GetRequiredService<ILinkStore>();
        await transactions.AddAsync(account, [Line("a")], TestContext.Current.CancellationToken);
        var line = Assert.Single(await transactions.ListAsync([account], null, null, 0, 10, TestContext.Current.CancellationToken));
        var item = ItemId.New();
        var at = NidApp.GoldenCaseToday.AtMidnight().InUtc().ToInstant();
        var expected = Money.Create(13.99m, Currency.Eur).TryGetValue(out var money) ? money : throw new InvalidOperationException();

        await links.SetAsync(new BankLink(line.Id, "subscriptions", item, "2026-10-12", null, LinkOutcome.PriceDiffers, expected, "job", at), TransactionLinkState.Linked, TestContext.Current.CancellationToken);

        Assert.Equal(TransactionLinkState.Linked, (await transactions.FindAsync(line.Id, TestContext.Current.CancellationToken))!.LinkState);
        var link = (await links.ForTransactionsAsync([line.Id], TestContext.Current.CancellationToken))[line.Id];
        Assert.Equal(LinkOutcome.PriceDiffers, link.Outcome);
        Assert.Equal(expected, link.Expected);
        Assert.Equal(item, link.TargetItem);
        Assert.Equal(line.Id, Assert.Single(await links.ForItemsAsync([item], TestContext.Current.CancellationToken)).Line.Id);
        Assert.Single(await links.OutcomeSinceAsync(LinkOutcome.PriceDiffers, Day, TestContext.Current.CancellationToken));
        Assert.Empty(await links.OutcomeSinceAsync(LinkOutcome.PriceDiffers, Day.PlusDays(1), TestContext.Current.CancellationToken));

        await links.ClearAsync(line.Id, TransactionLinkState.Ignored, TestContext.Current.CancellationToken);

        Assert.Equal(TransactionLinkState.Ignored, (await transactions.FindAsync(line.Id, TestContext.Current.CancellationToken))!.LinkState);
        Assert.Empty(await links.ForTransactionsAsync([line.Id], TestContext.Current.CancellationToken));
        Assert.Single(await transactions.ListAsync([account], null, null, 0, 10, TestContext.Current.CancellationToken, "ignored"));
        Assert.Empty(await transactions.ListAsync([account], null, null, 0, 10, TestContext.Current.CancellationToken, "linked"));
    }

    [Fact]
    public async Task A_flow_is_taken_once_a_link_targets_it()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var (scope, account) = await SetUpAsync(app);
        await using var _ = scope;
        var transactions = scope.ServiceProvider.GetRequiredService<ITransactionStore>();
        var links = scope.ServiceProvider.GetRequiredService<ILinkStore>();
        await transactions.AddAsync(account, [Line("a", 900m)], TestContext.Current.CancellationToken);
        var line = Assert.Single(await transactions.ListAsync([account], null, null, 0, 10, TestContext.Current.CancellationToken));
        var flow = Guid.NewGuid();

        Assert.False(await links.FlowTakenAsync(flow, TestContext.Current.CancellationToken));
        await links.SetAsync(
            new BankLink(line.Id, "realestate", ItemId.New(), $"flow:{flow:N}", null, LinkOutcome.Income, null, "job", Instant.FromUtc(2026, 10, 12, 0, 0)),
            TransactionLinkState.Linked, TestContext.Current.CancellationToken);
        Assert.True(await links.FlowTakenAsync(flow, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_rule_upsert_replaces_its_target()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var (scope, account) = await SetUpAsync(app);
        await using var _ = scope;
        var rules = scope.ServiceProvider.GetRequiredService<IRuleStore>();
        var (first, second) = (ItemId.New(), ItemId.New());

        var created = await rules.UpsertAsync(account, "NETFLIX", "subscriptions", first, app.AdultA, TestContext.Current.CancellationToken);
        var replaced = await rules.UpsertAsync(account, "NETFLIX", "subscriptions", second, app.AdultA, TestContext.Current.CancellationToken);

        Assert.Equal(created.Id, replaced.Id);
        Assert.Equal(second, Assert.Single(await rules.ForAccountAsync(account, TestContext.Current.CancellationToken)).TargetItem);
        Assert.Single(await rules.ByAsync(app.AdultA, TestContext.Current.CancellationToken));
        Assert.Empty(await rules.ByAsync(app.AdultB, TestContext.Current.CancellationToken));

        var at = Instant.FromUtc(2026, 10, 30, 8, 0);
        await rules.TouchAsync(created.Id, at, TestContext.Current.CancellationToken);
        Assert.Equal(at, (await rules.ForAccountAsync(account, TestContext.Current.CancellationToken)).Single().LastUsedAt);

        Assert.False(await rules.DeleteAsync(created.Id, app.AdultB, TestContext.Current.CancellationToken));
        Assert.True(await rules.DeleteAsync(created.Id, app.AdultA, TestContext.Current.CancellationToken));
        Assert.Empty(await rules.ForAccountAsync(account, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_group_is_remembered_once()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var (scope, account) = await SetUpAsync(app);
        await using var _ = scope;
        var rules = scope.ServiceProvider.GetRequiredService<IRuleStore>();
        var at = Instant.FromUtc(2026, 10, 30, 8, 0);

        Assert.True(await rules.TryRememberGroupAsync(account, "NETFLIX", at, TestContext.Current.CancellationToken));
        Assert.False(await rules.TryRememberGroupAsync(account, "NETFLIX", at, TestContext.Current.CancellationToken));
        Assert.True(await rules.TryRememberGroupAsync(account, "SPOTIFY", at, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Existing_lines_are_reconciled_by_the_migration()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var (scope, account) = await SetUpAsync(app);
        await using var _ = scope;
        var transactions = scope.ServiceProvider.GetRequiredService<ITransactionStore>();
        await transactions.AddAsync(account, [Line("old")], TestContext.Current.CancellationToken, reconciled: true);
        await transactions.AddAsync(account, [Line("new", -5m)], TestContext.Current.CancellationToken);

        var pending = Assert.Single(await transactions.UnreconciledAsync([account], TestContext.Current.CancellationToken));
        Assert.Equal("new", pending.ProviderRef);
        Assert.Single(await transactions.UnreconciledAsync(null, TestContext.Current.CancellationToken));

        await transactions.MarkReconciledAsync([pending.Id], Instant.FromUtc(2026, 10, 30, 8, 0), TestContext.Current.CancellationToken);
        Assert.Empty(await transactions.UnreconciledAsync([account], TestContext.Current.CancellationToken));
        Assert.Equal(2, (await transactions.UnlinkedBetweenAsync(Day, Day, TestContext.Current.CancellationToken)).Count);
    }
}
