using Nid.Core.Contracts;
using Nid.Subscriptions.Contracts;
using Nid.Subscriptions.Domain;
using Nid.Subscriptions.Infrastructure.Persistence;
using Nid.TestSupport;
using NodaTime;
using Duration = Nid.Core.Contracts.Duration;

namespace Nid.Subscriptions.IntegrationTests;

[Collection("postgres")]
public class SubscriptionStoreTests(PostgresFixture fixture)
{
    private static Money Eur(decimal amount) => Money.Create(amount, Currency.Eur).Ok();

    private static Duration Months(int count) => Duration.Create(count, DurationUnit.Month).Ok();

    private static async Task<(ISubscriptionStore store, ItemId id)> NewStoreAsync(PostgresFixture f)
    {
        var connectionString = await f.NewConnectionStringAsync();
        var store = new SubscriptionStore(PostgresFixture.NewSubscriptionsContext(connectionString));
        var id = new ItemId(Guid.CreateVersion7());
        (await store.CreateAsync(id, new NewSubscription("spotify", AutoRenew: true, [], null), default)).Ok();
        return (store, id);
    }

    [Fact]
    public async Task A_stored_subscription_loads_back_as_engine_input()
    {
        var (store, id) = await NewStoreAsync(fixture);

        var input = await store.LoadAsync(id, default);

        Assert.NotNull(input);
        Assert.Equal(id, input!.Subscription.Id);
        Assert.True(input.Subscription.AutoRenew);
        Assert.Empty(input.Phases);
        Assert.Empty(input.Purchases);
    }

    [Fact]
    public async Task An_unknown_subscription_loads_as_null()
        => Assert.Null(await (await NewStoreAsync(fixture)).store.LoadAsync(new ItemId(Guid.CreateVersion7()), default));

    [Fact]
    public async Task A_phase_round_trips_with_every_field()
    {
        var (store, id) = await NewStoreAsync(fixture);
        var phase = new BillingPhase(
            BillingPhaseId.New(), new LocalDate(2024, 6, 28), new LocalDate(2025, 6, 28),
            Eur(15.17m), Months(1), Months(3), MaxCycles: 4, new Payer(PayerType.Household, null));

        (await store.AddPhaseAsync(id, phase, default)).Ok();
        var loaded = (await store.LoadAsync(id, default))!.Phases.Single();

        Assert.Equal(phase.StartsOn, loaded.StartsOn);
        Assert.Equal(phase.EndsOn, loaded.EndsOn);
        Assert.Equal(15.17m, loaded.Price.Amount);
        Assert.Equal(Months(1), loaded.Every);
        Assert.Equal(Months(3), loaded.Covers);
        Assert.Equal(4, loaded.MaxCycles);
        Assert.Equal(PayerType.Household, loaded.Payer.Type);
    }

    [Fact]
    public async Task A_purchase_round_trips_with_its_grants_in_order()
    {
        var (store, id) = await NewStoreAsync(fixture);
        var purchase = new Purchase(
            PurchaseId.New(), new LocalDate(2025, 6, 22), "NordVPN 2-year", Eur(179.86m),
            new Payer(PayerType.Household, null), PurchaseKind.PrepaidTerm,
            [new Grant(id, Months(24), CoverageSource.Paid, new LocalDate(2025, 6, 22)),
             new Grant(id, Months(6), CoverageSource.Bonus, null)],
            AllocatedAmount: null);

        (await store.AddPurchaseAsync(purchase, default)).Ok();
        var loaded = (await store.LoadAsync(id, default))!.Purchases.Single();

        Assert.Equal(179.86m, loaded.Price.Amount);
        Assert.Equal(PurchaseKind.PrepaidTerm, loaded.Kind);
        Assert.Equal(2, loaded.Grants.Count);
        Assert.Equal(CoverageSource.Paid, loaded.Grants[0].Source);
        Assert.Equal(new LocalDate(2025, 6, 22), loaded.Grants[0].StartsOn);
        Assert.Equal(CoverageSource.Bonus, loaded.Grants[1].Source);
        Assert.Null(loaded.Grants[1].StartsOn);
    }

    [Fact]
    public async Task A_purchase_granting_elsewhere_is_loaded_too_so_the_engine_can_filter_it()
    {
        var (store, id) = await NewStoreAsync(fixture);
        var other = new ItemId(Guid.CreateVersion7());
        var purchase = new Purchase(
            PurchaseId.New(), new LocalDate(2025, 1, 1), "bundle", Eur(20m),
            new Payer(PayerType.Household, null), PurchaseKind.Bundle,
            [new Grant(other, Months(1), CoverageSource.Included, null),
             new Grant(id, Months(1), CoverageSource.Included, null)],
            AllocatedAmount: 0m);

        (await store.AddPurchaseAsync(purchase, default)).Ok();
        var loaded = (await store.LoadAsync(id, default))!.Purchases.Single();

        // Both grants are loaded, in their stored order. Filtering is the engine's job, not the store's.
        Assert.Equal(2, loaded.Grants.Count);
        Assert.Contains(loaded.Grants, g => g.SubscriptionId == other);
    }

    [Fact]
    public async Task The_Spotify_golden_case_survives_a_round_trip_through_the_database()
    {
        var (store, id) = await NewStoreAsync(fixture);
        (await store.AddPhaseAsync(id, new BillingPhase(
            BillingPhaseId.New(), new LocalDate(2024, 6, 28), new LocalDate(2025, 6, 28),
            Eur(15.17m), Months(1), null, null, new Payer(PayerType.Household, null)), default)).Ok();
        (await store.AddPhaseAsync(id, new BillingPhase(
            BillingPhaseId.New(), new LocalDate(2025, 6, 28), null,
            Eur(17.20m), Months(1), null, null, new Payer(PayerType.Household, null)), default)).Ok();
        (await store.AddPurchaseAsync(new Purchase(
            PurchaseId.New(), new LocalDate(2025, 10, 10), "gift", Eur(0m),
            new Payer(PayerType.External, null), PurchaseKind.Gift,
            [new Grant(id, Months(3), CoverageSource.Gift, null)], null), default)).Ok();

        var timeline = SubscriptionTimeline
            .Build((await store.LoadAsync(id, default))!, new LocalDate(2025, 10, 15), new LocalDate(2026, 6, 1)).Ok();

        // The documented result of docs/subscription-data-model.md, reproduced from stored rows.
        var gift = Assert.Single(timeline.Coverage, p => p.Source == CoverageSource.Gift);
        Assert.Equal(new LocalDate(2025, 10, 28), gift.Start);
        Assert.Equal(new LocalDate(2026, 1, 28), gift.EndBefore);
        Assert.Equal(new LocalDate(2026, 1, 28), timeline.NextCharge()!.Date);
        Assert.Equal(17.20m, timeline.NextCharge()!.Price.Amount);
    }

    [Fact]
    public async Task Closing_a_phase_sets_its_end()
    {
        var (store, id) = await NewStoreAsync(fixture);
        var phase = new BillingPhase(BillingPhaseId.New(), new LocalDate(2025, 1, 1), null,
                                     Eur(10m), Months(1), null, null, new Payer(PayerType.Household, null));
        (await store.AddPhaseAsync(id, phase, default)).Ok();

        (await store.ClosePhaseAsync(id, phase.Id, new LocalDate(2025, 6, 1), default)).Ok();

        Assert.Equal(new LocalDate(2025, 6, 1), (await store.LoadAsync(id, default))!.Phases.Single().EndsOn);
    }

    [Fact]
    public async Task Deleting_a_purchase_removes_its_grants()
    {
        var (store, id) = await NewStoreAsync(fixture);
        var purchase = new Purchase(PurchaseId.New(), new LocalDate(2025, 1, 1), "gift", Eur(0m),
                                    new Payer(PayerType.External, null), PurchaseKind.Gift,
                                    [new Grant(id, Months(1), CoverageSource.Gift, null)], null);
        (await store.AddPurchaseAsync(purchase, default)).Ok();

        (await store.DeletePurchaseAsync(id, purchase.Id, default)).Ok();

        Assert.Empty((await store.LoadAsync(id, default))!.Purchases);
    }
}
