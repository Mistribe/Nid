using System.Globalization;
using Nid.Core.Contracts;
using Nid.TestSupport;

namespace Nid.Subscriptions.IntegrationTests;

/// <summary>GET /subscriptions/overview (the app clock is fixed at 2025-10-15).</summary>
public class OverviewEndpointTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    public sealed record Money(string Amount, string Currency);
    public sealed record PersonShare(Guid PersonId, Money? Amortized);
    public sealed record Overview(
        string Currency, Money? MonthlyAmortized, Money? AnnualAmortized, int ActiveCount, List<MonthRow> Months, List<PersonShare> ByPerson);
    public sealed record MonthRow(string Month, Money? Recurring, Money? LongCycle);

    private static decimal Value(Money? m) => decimal.Parse(Assert.IsType<Money>(m).Amount, CultureInfo.InvariantCulture);

    private static async Task PhaseAsync(NidApp app, ItemId id, string startsOn, string amount, string unit)
        => (await app.RawAsync(app.AdultA, HttpMethod.Post, $"/api/v1/subscriptions/{id.Value}/phases", new
        {
            startsOn,
            price = new { amount, currency = "EUR" },
            every = new { count = 1, unit },
            payer = new { type = "household" },
        })).EnsureSuccessStatusCode();

    [Fact]
    public async Task The_overview_of_a_household_without_subscriptions_is_empty_with_zero_totals()
    {
        await using var app = await NidApp.StartAsync(fixture);

        var overview = await app.GetAsync<Overview>(app.AdultA, "/api/v1/subscriptions/overview");

        Assert.Equal(0, overview.ActiveCount);
        Assert.Equal(0m, Value(overview.MonthlyAmortized));
        Assert.Equal(0m, Value(overview.AnnualAmortized));
        Assert.Equal(12, overview.Months.Count);
    }

    [Fact]
    public async Task The_overview_counts_a_monthly_and_a_yearly_subscription()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await app.NewMonthlyAsync(app.AdultA, 10.00m);
        var yearly = await app.NewSubscriptionAsync("yearly", app.AdultA, Visibility.Shared);
        await PhaseAsync(app, yearly, "2025-01-01", "120.00", "year");

        var overview = await app.GetAsync<Overview>(app.AdultA, "/api/v1/subscriptions/overview?scope=me");

        Assert.Equal(20.00m, Value(overview.MonthlyAmortized)); // #711: a twelfth of the year's 240.00
        Assert.Equal(2, overview.ActiveCount);
        Assert.Equal(12, overview.Months.Count);
        Assert.Contains(overview.ByPerson, p => p.PersonId == app.AdultA.Value);
    }

    [Fact]
    public async Task A_month_is_a_twelfth_of_the_year_and_the_twelve_months_add_up_to_it()
    {
        await using var app = await NidApp.StartAsync(fixture);
        // A free trial until 2025-11-15, then 12.00 a month: 11 charges by 2026-09-15.
        var trial = await app.NewSubscriptionAsync("trial", app.AdultA, Visibility.Shared);
        (await app.RawAsync(app.AdultA, HttpMethod.Post, $"/api/v1/subscriptions/{trial.Value}/phases", new
        {
            startsOn = "2025-10-15", endsOn = "2025-11-15",
            price = new { amount = "0", currency = "EUR" },
            every = new { count = 1, unit = "month" },
            payer = new { type = "household" },
        })).EnsureSuccessStatusCode();
        await PhaseAsync(app, trial, "2025-11-15", "12.00", "month");
        var yearly = await app.NewSubscriptionAsync("yearly", app.AdultA, Visibility.Shared);
        await PhaseAsync(app, yearly, "2025-01-01", "120.00", "year");

        var overview = await app.GetAsync<Overview>(app.AdultA, "/api/v1/subscriptions/overview?scope=me");
        Assert.Equal(252.00m, Value(overview.AnnualAmortized));
        Assert.Equal(21.00m, Value(overview.MonthlyAmortized));
        Assert.Equal("2025-10", overview.Months[0].Month);
        Assert.Equal(252.00m, overview.Months.Sum(m => (m.Recurring is null ? 0m : Value(m.Recurring)) + (m.LongCycle is null ? 0m : Value(m.LongCycle))));

        // The list's rows and totals on the same basis.
        var list = await app.GetAsync<AnnualCostTests.ListResponse>(app.AdultA, "/api/v1/subscriptions?scope=me");
        var row = list.Items.Single(i => i.Id == trial.Value);
        static decimal Of(AnnualCostTests.Money? m) => decimal.Parse(Assert.IsType<AnnualCostTests.Money>(m).Amount, CultureInfo.InvariantCulture);
        Assert.Equal((132.00m, 11.00m), (Of(row.AnnualCostConverted), Of(row.MonthlyCostConverted)));
        Assert.Equal((252.00m, 21.00m), (Of(list.HouseholdAnnualCostConverted), Of(list.HouseholdMonthlyCostConverted)));
    }

    [Fact]
    public async Task The_me_scope_hides_the_other_adults_private_subscription_everywhere()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var secret = await app.NewSubscriptionAsync("SecretTherapy", app.AdultB, Visibility.Private);
        (await app.RawAsync(app.AdultB, HttpMethod.Post, $"/api/v1/subscriptions/{secret.Value}/phases", new
        {
            startsOn = "2025-01-01",
            price = new { amount = "30.00", currency = "EUR" },
            every = new { count = 1, unit = "month" },
            payer = new { type = "household" },
        })).EnsureSuccessStatusCode();

        var mine = await app.NewSubscriptionAsync("MyShared", app.AdultA, Visibility.Shared);
        await PhaseAsync(app, mine, "2025-01-01", "10.00", "month");

        const string path = "/api/v1/subscriptions/overview?scope=me";
        var raw = await app.GetRawAsync(app.AdultA, path);
        var overview = await app.GetAsync<Overview>(app.AdultA, path);

        Assert.DoesNotContain("SecretTherapy", raw);
        Assert.DoesNotContain(secret.Value.ToString(), raw);
        Assert.Equal(1, overview.ActiveCount); // the reader's own subscription counts, the private one does not
    }

    [Fact]
    public async Task The_twelfth_month_is_complete()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await app.NewSubscriptionAsync("on-the-20th", app.AdultA, Visibility.Shared);
        await PhaseAsync(app, id, "2025-01-20", "10.00", "month");

        var overview = await app.GetAsync<Overview>(app.AdultA, "/api/v1/subscriptions/overview?scope=me");

        // #711: this month and the 11 after.
        var last = overview.Months[11];
        Assert.Equal("2026-09", last.Month);
        Assert.True(Value(last.Recurring) > 0m);
    }
}
