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
        (await store.CreateAsync(id, new NewSubscription(null, AutoRenew: true, [], null), default)).Ok();
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

        (await store.AddPhaseAsync(id, phase, RowOrigin.None, default)).Ok();
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

        (await store.AddPurchaseAsync(purchase, RowOrigin.None, default)).Ok();
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

        (await store.AddPurchaseAsync(purchase, RowOrigin.None, default)).Ok();
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
            Eur(15.17m), Months(1), null, null, new Payer(PayerType.Household, null)), RowOrigin.None, default)).Ok();
        (await store.AddPhaseAsync(id, new BillingPhase(
            BillingPhaseId.New(), new LocalDate(2025, 6, 28), null,
            Eur(17.20m), Months(1), null, null, new Payer(PayerType.Household, null)), RowOrigin.None, default)).Ok();
        (await store.AddPurchaseAsync(new Purchase(
            PurchaseId.New(), new LocalDate(2025, 10, 10), "gift", Eur(0m),
            new Payer(PayerType.External, null), PurchaseKind.Gift,
            [new Grant(id, Months(3), CoverageSource.Gift, null)], null), RowOrigin.None, default)).Ok();

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
        (await store.AddPhaseAsync(id, phase, RowOrigin.None, default)).Ok();

        (await store.ClosePhaseAsync(id, phase.Id, new LocalDate(2025, 6, 1), null, default)).Ok();

        Assert.Equal(new LocalDate(2025, 6, 1), (await store.LoadAsync(id, default))!.Phases.Single().EndsOn);
    }

    [Fact]
    public async Task Deleting_a_purchase_removes_its_grants()
    {
        var (store, id) = await NewStoreAsync(fixture);
        var purchase = new Purchase(PurchaseId.New(), new LocalDate(2025, 1, 1), "gift", Eur(0m),
                                    new Payer(PayerType.External, null), PurchaseKind.Gift,
                                    [new Grant(id, Months(1), CoverageSource.Gift, null)], null);
        (await store.AddPurchaseAsync(purchase, RowOrigin.None, default)).Ok();

        (await store.DeletePurchaseAsync(id, purchase.Id, default)).Ok();

        Assert.Empty((await store.LoadAsync(id, default))!.Purchases);
    }

    [Fact]
    public async Task Imported_rows_keep_their_importer_and_the_same_ref_can_be_used_by_two_importers()
    {
        var (store, id) = await NewStoreAsync(fixture);
        var a = new PersonId(Guid.CreateVersion7());
        var b = new PersonId(Guid.CreateVersion7());
        var other = new ItemId(Guid.CreateVersion7());
        (await store.CreateAsync(other, new NewSubscription(null, true, [], null, Import: new ImportRef("sub-x", new string('b', 64), b)), default)).Ok();
        var phase = new BillingPhase(
            BillingPhaseId.New(), new LocalDate(2025, 1, 1), null, Eur(5), Months(1), null, MaxCycles: null, new Payer(PayerType.Household, null));
        (await store.AddPhaseAsync(id, phase, new RowOrigin(null, new ImportRef("ph-x", new string('a', 64), a)), default)).Ok();
        var purchase = new Purchase(
            PurchaseId.New(), new LocalDate(2025, 2, 1), "Gift", Eur(0),
            new Payer(PayerType.External, null), PurchaseKind.Gift,
            [new Grant(id, Months(1), CoverageSource.Gift, null),
             new Grant(other, Months(1), CoverageSource.Gift, null),
             new Grant(id, Months(1), CoverageSource.Gift, null)],
            AllocatedAmount: null);
        (await store.AddPurchaseAsync(purchase, new RowOrigin(null, new ImportRef("pu-x", new string('a', 64), a)), default)).Ok();
        var second = new ItemId(Guid.CreateVersion7());
        (await store.CreateAsync(second, new NewSubscription(null, true, [], null, Import: new ImportRef("sub-x", new string('c', 64), a)), default)).Ok();

        var subscriptions = await store.FindImportedAsync(ImportedTable.Subscription, ["sub-x", "sub-none"], default);
        var phases = await store.FindImportedAsync(ImportedTable.BillingPhase, ["ph-x"], default);
        var purchases = await store.FindImportedAsync(ImportedTable.Purchase, ["pu-x"], default);

        Assert.False(subscriptions.ContainsKey("sub-none"));
        Assert.Equal(
            new[] { (other.Value, (PersonId?)b), (second.Value, (PersonId?)a) }.OrderBy(x => x.Item1).ToList(),
            subscriptions["sub-x"].Select(r => (r.Id, r.ImportedBy)).ToList());
        Assert.Equal([second], subscriptions["sub-x"].Single(r => r.ImportedBy == a).Items);
        var storedPhase = Assert.Single(phases["ph-x"]);
        Assert.Equal(a, storedPhase.ImportedBy);
        Assert.Equal([id], storedPhase.Items);
        Assert.Equal([id, other], Assert.Single(purchases["pu-x"]).Items);
    }

    [Fact]
    public async Task A_row_written_without_an_importer_is_found_with_none()
    {
        var connectionString = await fixture.NewConnectionStringAsync();
        var store = new SubscriptionStore(PostgresFixture.NewSubscriptionsContext(connectionString));
        var id = new ItemId(Guid.CreateVersion7());
        (await store.CreateAsync(id, new NewSubscription(null, true, [], null, Import: new ImportRef("sub-old", new string('a', 64))), default)).Ok();

        var found = Assert.Single((await store.FindImportedAsync(ImportedTable.Subscription, ["sub-old"], default))["sub-old"]);

        Assert.Null(found.ImportedBy);
    }
}
