using Nid.Assets.Domain;
using NodaTime;

namespace Nid.Assets.Tests;

public class OwnershipMathTests
{
    private static readonly LocalDate From = new(2025, 12, 1);
    private static readonly LocalDate To = new(2026, 11, 30);
    private static readonly Guid Insurance = Guid.NewGuid();
    private static readonly Guid Loan = Guid.NewGuid();

    private static AssetValue? Worth(LocalDate on) => new(on == To ? 18_000m : 20_000m, on, false);

    private static OwnershipCost Compute(
        IReadOnlyDictionary<string, decimal>? running = null, IReadOnlyList<(Guid, string, decimal?)>? subscriptions = null,
        IReadOnlyList<(Guid, string, decimal?)>? loans = null, Func<LocalDate, AssetValue?>? valueAt = null,
        IReadOnlyList<MileageReading>? readings = null, LocalDate? purchasedOn = null)
        => OwnershipMath.Compute(From, To, purchasedOn,
            running ?? new Dictionary<string, decimal> { ["service"] = 600m, ["fuel"] = 1_200m },
            subscriptions ?? [(Insurance, "Assurance auto", 600m)],
            loans ?? [(Loan, "Crédit auto", 240m)],
            valueAt ?? Worth, readings ?? []);

    private static decimal? Part(OwnershipCost cost, string code) => Assert.Single(cost.Parts, p => p.Code == code).Amount;

    [Fact]
    public void Twelve_months_add_every_part()
    {
        var cost = Compute();

        Assert.Equal((4_640m, 11.99m, 386.99m, false), (cost.Total, cost.Months, cost.Monthly, cost.Incomplete));
        Assert.Equal(["running:service", "running:fuel", "subscription", "credit", "depreciation"], cost.Parts.Select(p => p.Code));
        Assert.Equal((600m, 1_200m, 600m, 240m, 2_000m),
            (Part(cost, "running:service"), Part(cost, "running:fuel"), Part(cost, "subscription"), Part(cost, "credit"), Part(cost, "depreciation")));
        Assert.Equal((Insurance, "Assurance auto"), (cost.Parts[2].Source, cost.Parts[2].Name));
        Assert.Equal((Loan, "Crédit auto"), (cost.Parts[3].Source, cost.Parts[3].Name));
    }

    [Fact]
    public void A_part_without_data_is_missing_and_left_out()
    {
        var cost = Compute(running: new Dictionary<string, decimal> { ["service"] = 600m }, subscriptions: []);

        Assert.Null(Part(cost, "running:fuel"));
        Assert.Null(Part(cost, "subscription"));
        Assert.True(cost.Incomplete);
        Assert.Equal(600m + 240m + 2_000m, cost.Total);
    }

    [Fact]
    public void A_vehicle_without_a_loan_has_no_credit_part_and_stays_complete()
    {
        var cost = Compute(loans: []);

        Assert.DoesNotContain(cost.Parts, p => p.Code == "credit");
        Assert.False(cost.Incomplete);
    }

    [Fact]
    public void A_loan_or_subscription_without_an_exchange_rate_is_missing()
    {
        var cost = Compute(subscriptions: [(Insurance, "Assurance auto", null)], loans: [(Loan, "Crédit auto", null)]);

        Assert.Null(Part(cost, "subscription"));
        Assert.Null(Part(cost, "credit"));
        Assert.True(cost.Incomplete);
    }

    [Fact]
    public void Depreciation_is_missing_without_a_value_at_the_start()
    {
        var cost = Compute(valueAt: on => on == To ? new AssetValue(18_000m, on, false) : null);

        Assert.Null(Part(cost, "depreciation"));
        Assert.True(cost.Incomplete);
        Assert.Equal(600m + 1_200m + 600m + 240m, cost.Total);
    }

    [Fact]
    public void A_vehicle_bought_in_the_period_starts_the_period_at_its_purchase()
    {
        var bought = new LocalDate(2026, 6, 1);
        LocalDate? asked = null;

        var cost = Compute(purchasedOn: bought, valueAt: on => { asked ??= on; return Worth(on); });

        Assert.Equal((bought, To, 6.01m), (cost.From, cost.To, cost.Months));
        Assert.Equal(bought, asked);
    }

    [Fact]
    public void Per_km_needs_two_readings_thirty_days_apart_going_up()
    {
        MileageReading[] year = [new(new LocalDate(2025, 12, 1), 10_000), new(new LocalDate(2026, 11, 30), 22_000)];

        Assert.Equal(0.39m, Compute(readings: year).PerKm);
        Assert.Null(Compute(readings: [new(new LocalDate(2026, 6, 1), 10_000), new(new LocalDate(2026, 6, 11), 10_500)]).PerKm);
        Assert.Null(Compute(readings: [new(new LocalDate(2025, 12, 1), 22_000), new(new LocalDate(2026, 11, 30), 10_000)]).PerKm);
        Assert.Null(Compute(readings: [year[0]]).PerKm);
    }

    [Fact]
    public void Per_km_takes_the_readings_just_outside_the_period()
    {
        var cost = Compute(readings: [new(new LocalDate(2024, 1, 1), 1_000), new(new LocalDate(2025, 11, 1), 9_000),
            new(new LocalDate(2026, 12, 31), 21_000), new(new LocalDate(2027, 6, 1), 30_000)]);

        // 12 000 km over 425 days (2025-11-01 .. 2026-12-31): 859.29 km a month.
        Assert.Equal(0.45m, cost.PerKm);
    }
}
