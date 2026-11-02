using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.Platform.Infrastructure.Audit;
using Nid.Subscriptions.Contracts;
using Nid.TestSupport;
using NodaTime;
using Duration = Nid.Core.Contracts.Duration;

namespace Nid.Host.IntegrationTests.Audit;

/// <summary>1F3a spec §4 and §9 tests 1–2: what a write leaves in the log, and when it leaves nothing.</summary>
public class WriteAuditTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static async Task<List<AuditEntryRow>> WritesAsync(NidApp app, string entity)
    {
        await using var platform = PostgresFixture.NewPlatformContext(app.ConnectionString);
        return await platform.AuditLog.Where(r => r.Category == "write" && r.Entity == entity).OrderBy(r => r.Seq).ToListAsync();
    }

    private static Purchase OneMonthFor(params ItemId[] items) => new(
        PurchaseId.New(), new LocalDate(2025, 1, 1), "audit-probe", Money.Create(1m, Currency.Eur).Ok(),
        new Payer(PayerType.Household, null), PurchaseKind.PrepaidTerm,
        items.Select(i => new Grant(i, Duration.Create(1, DurationUnit.Month).Ok(), CoverageSource.Paid, null)).ToList(), null);

    [Fact]
    public async Task Creating_a_subscription_audits_each_row_under_the_new_item()
    {
        await using var app = await NidApp.StartAsync(fixture);

        var id = await app.NewSubscriptionAsync("audited", app.AdultA, Visibility.Shared);

        foreach (var entity in new[] { "core.item", "core.ownership", "subscriptions.subscription" })
        {
            var entry = Assert.Single(await WritesAsync(app, entity));
            Assert.Equal(("insert", "person", app.AdultA.Value, "ui"), (entry.Action, entry.ActorKind, entry.PersonId!.Value, entry.Via));
            Assert.Equal([id.Value], entry.ItemIds);
            Assert.Equal("CreateSubscriptionCommand", entry.Request);
        }

        var item = Assert.Single(await WritesAsync(app, "core.item"));
        using var data = JsonDocument.Parse(item.Data);
        Assert.Equal(JsonValueKind.Null, data.RootElement.GetProperty("before").ValueKind);
        Assert.Equal("audited", data.RootElement.GetProperty("after").GetProperty("display_name").GetString());
        Assert.False(data.RootElement.GetProperty("after").TryGetProperty("xmin", out _));
    }

    [Fact]
    public async Task An_update_records_only_the_columns_it_changed()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await app.NewSubscriptionAsync("edited", app.AdultA, Visibility.Shared);

        (await app.RawAsync(app.AdultA, HttpMethod.Patch, $"/api/v1/subscriptions/{id.Value}", new { note = "new note" }))
            .EnsureSuccessStatusCode();

        var update = Assert.Single(await WritesAsync(app, "subscriptions.subscription"), r => r.Action == "update");
        using var data = JsonDocument.Parse(update.Data);
        Assert.Equal(["note"], data.RootElement.GetProperty("after").EnumerateObject().Select(p => p.Name));
        Assert.Equal(JsonValueKind.Null, data.RootElement.GetProperty("before").GetProperty("note").ValueKind);
        Assert.Equal("new note", data.RootElement.GetProperty("after").GetProperty("note").GetString());
    }

    [Fact]
    public async Task A_rolled_back_unit_of_work_leaves_no_audit_row()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await app.NewSubscriptionAsync("rolled-back", app.AdultA, Visibility.Shared);

        await using (var scope = app.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.Browser(app.AdultA);
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            await using var transaction = await unitOfWork.BeginAsync(TestContext.Current.CancellationToken);
            (await scope.ServiceProvider.GetRequiredService<ISubscriptionStore>()
                .AddPurchaseAsync(OneMonthFor(id), RowOrigin.None, TestContext.Current.CancellationToken)).Ok();
            // disposed without CommitAsync: rolled back
        }

        Assert.Empty(await WritesAsync(app, "subscriptions.purchase"));
        Assert.Empty(await WritesAsync(app, "subscriptions.coverage_grant"));
    }

    [Fact]
    public async Task A_committed_unit_of_work_keeps_one_row_per_written_row()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await app.NewSubscriptionAsync("committed", app.AdultA, Visibility.Shared);

        await using (var scope = app.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.Browser(app.AdultA);
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            await using var transaction = await unitOfWork.BeginAsync(TestContext.Current.CancellationToken);
            (await scope.ServiceProvider.GetRequiredService<ISubscriptionStore>()
                .AddPurchaseAsync(OneMonthFor(id), RowOrigin.None, TestContext.Current.CancellationToken)).Ok();
            await transaction.CommitAsync(TestContext.Current.CancellationToken);
        }

        Assert.Single(await WritesAsync(app, "subscriptions.purchase"));
        Assert.Single(await WritesAsync(app, "subscriptions.coverage_grant"));
    }

    [Fact]
    public async Task An_audited_write_with_no_actor_throws_and_writes_nothing()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await app.NewSubscriptionAsync("no-actor", app.AdultA, Visibility.Shared);

        await using (var scope = app.Services.CreateAsyncScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<ISubscriptionStore>();
            await Assert.ThrowsAsync<AuditActorMissingException>(() => store.AddPurchaseAsync(OneMonthFor(id), RowOrigin.None, TestContext.Current.CancellationToken));
        }

        await using var subs = PostgresFixture.NewSubscriptionsContext(app.ConnectionString);
        Assert.Empty(await subs.Purchases.ToListAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_journal_write_needs_no_actor()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await using (var scope = app.Services.CreateAsyncScope())
        {
            // The run journal is excluded (spec §11.2): startup work like CatalogStartupSync's touches it with no
            // actor. TryStartAsync saves through the change tracker, so the interceptor sees a save of excluded rows
            // only, and must let it through rather than ask for an actor.
            var run = await scope.ServiceProvider.GetRequiredService<ICatalogRunLog>().TryStartAsync("journal-probe", TestContext.Current.CancellationToken);
            Assert.NotNull(run);
        }

        await using var subs = PostgresFixture.NewSubscriptionsContext(app.ConnectionString);
        Assert.Equal("journal-probe", (await subs.CatalogSyncRuns.SingleAsync(cancellationToken: TestContext.Current.CancellationToken)).Commit);
        Assert.Empty(await WritesAsync(app, "subscriptions.catalog_sync_run"));
    }

    [Fact]
    public async Task Deleting_a_purchase_audits_it_and_each_grant_under_every_subscription_it_granted()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var first = await app.NewSubscriptionAsync("first", app.AdultA, Visibility.Shared);
        var second = await app.NewSubscriptionAsync("second", app.AdultA, Visibility.Shared);
        var purchase = await app.SeedPurchaseGrantingToAsync(first, second);

        (await app.RawAsync(app.AdultA, HttpMethod.Delete, $"/api/v1/subscriptions/{first.Value}/purchases/{purchase.Value}"))
            .EnsureSuccessStatusCode();

        var deleted = Assert.Single(await WritesAsync(app, "subscriptions.purchase"), r => r.Action == "delete");
        Assert.Equal(new[] { first.Value, second.Value }.Order(), deleted.ItemIds.Order());
        var grants = (await WritesAsync(app, "subscriptions.coverage_grant")).Where(r => r.Action == "delete").ToList();
        Assert.Equal(2, grants.Count);
        Assert.Equal(new[] { first.Value, second.Value }.Order(), grants.Select(g => g.ItemIds.Single()).Order());
    }

    [Fact]
    public async Task A_save_that_fails_leaves_no_line_for_the_next_save()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await app.SeedProviderAsync("taken", "Taken", ProviderOrigin.Custom);
        static Provider Custom(string key, PersonId by) => new(
            key, ProviderOrigin.Custom, "Probe", null, null, [CatalogLimits.OtherLabel], null, by, null, null);

        await using (var scope = app.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.Browser(app.AdultA);
            var providers = scope.ServiceProvider.GetRequiredService<IProviderStore>();
            // The key race CreateProviderHandler retries on: the store catches the conflict, then saves again.
            Assert.True((await providers.CreateAsync(Custom("taken", app.AdultA), TestContext.Current.CancellationToken)).TryGetFailure(out _));
            Assert.False((await providers.CreateAsync(Custom("free", app.AdultA), TestContext.Current.CancellationToken)).TryGetFailure(out _));
        }

        var inserted = Assert.Single(await WritesAsync(app, "subscriptions.provider"));
        Assert.Equal("free", inserted.EntityKey);
    }

    [Fact]
    public async Task A_rate_is_never_audited()
    {
        await using var app = await NidApp.StartAsync(fixture);

        (await app.RawAsync(app.AdultA, HttpMethod.Put, "/api/v1/core/fx-rates/2025-01-02/GBP", new { rate = "0.79" }))
            .EnsureSuccessStatusCode();

        Assert.Empty(await WritesAsync(app, "core.fx_rate"));
    }
}
