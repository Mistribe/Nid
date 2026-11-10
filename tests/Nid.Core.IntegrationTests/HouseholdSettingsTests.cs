using Microsoft.EntityFrameworkCore;
using Nid.Core.Contracts;
using Nid.Core.Infrastructure.Persistence;
using Nid.TestSupport;
using NodaTime;

namespace Nid.Core.IntegrationTests;

[Collection("postgres")]
public class HouseholdSettingsTests(PostgresFixture postgres)
{
    private static string? Code(UnambitiousFx.Functional.Result<HouseholdSettingsInfo> result)
        => result.TryGetFailure(out var failure) ? failure!.Code : null;

    private static int? Months(UnambitiousFx.Functional.Result<HouseholdSettingsInfo> result)
    {
        Assert.True(result.TryGetValue(out var info));
        return info!.BankRetentionMonths;
    }

    [Fact]
    public async Task Reads_the_cap_the_currency_and_the_number_of_adults()
    {
        await using var db = await postgres.NewDatabaseAsync();
        var registry = new PersonRegistry(db, PostgresFixture.Clock);
        await registry.ProvisionAdultAsync("sub-a", "Alice", null, default, TestContext.Current.CancellationToken);
        await registry.PreRegisterAdultAsync("Bob", "bob@example.test", TestContext.Current.CancellationToken);
        await registry.CreateDependentAsync("Kid", TestContext.Current.CancellationToken);

        var settings = await new HouseholdSettings(db).ReadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(new HouseholdSettingsInfo(2, Currency.Eur, 2, false, false), settings);
    }

    [Fact]
    public async Task Changes_the_cap_and_the_currency_but_never_below_the_number_of_adults()
    {
        await using var db = await postgres.NewDatabaseAsync();
        var registry = new PersonRegistry(db, PostgresFixture.Clock);
        await registry.ProvisionAdultAsync("sub-a", "Alice", null, default, TestContext.Current.CancellationToken);
        await registry.PreRegisterAdultAsync("Bob", "bob@example.test", TestContext.Current.CancellationToken);
        var settings = new HouseholdSettings(db);

        Assert.Equal(PersonCodes.CapBelowCount, Code(await settings.UpdateAsync(new HouseholdSettingsChange(1, null), TestContext.Current.CancellationToken)));
        Assert.Equal(PersonCodes.CapBelowCount, Code(await settings.UpdateAsync(new HouseholdSettingsChange(0, null), TestContext.Current.CancellationToken)));
        Assert.Equal(PersonCodes.UnknownCurrency, Code(await settings.UpdateAsync(new HouseholdSettingsChange(null, "usd"), TestContext.Current.CancellationToken)));

        Assert.True((await settings.UpdateAsync(new HouseholdSettingsChange(4, "USD"), TestContext.Current.CancellationToken)).TryGetValue(out var changed));
        Assert.Equal(new HouseholdSettingsInfo(4, Currency.Usd, 2, false, false), changed);

        Assert.True((await settings.UpdateAsync(new HouseholdSettingsChange(null, null, IrlEnabled: true), TestContext.Current.CancellationToken)).TryGetValue(out var irl));
        Assert.True(irl.IrlEnabled);
        Assert.Equal(Currency.Usd, await settings.ReferenceCurrencyAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Banking_is_off_by_default_and_keeps_its_application_id()
    {
        await using var db = await postgres.NewDatabaseAsync();
        var settings = new HouseholdSettings(db);

        var initial = await settings.ReadAsync(TestContext.Current.CancellationToken);
        Assert.Equal((false, null), (initial.BankingEnabled, initial.EnableBankingAppId));

        Assert.True((await settings.UpdateAsync(new HouseholdSettingsChange(null, null, BankingEnabled: true, EnableBankingAppId: " 0a1b-app "), TestContext.Current.CancellationToken))
            .TryGetValue(out var on));
        Assert.Equal((true, "0a1b-app"), (on.BankingEnabled, on.EnableBankingAppId));

        // A field left out is left unchanged; an empty id clears it.
        Assert.True((await settings.UpdateAsync(new HouseholdSettingsChange(null, null, BankingEnabled: false), TestContext.Current.CancellationToken)).TryGetValue(out var off));
        Assert.Equal((false, "0a1b-app"), (off.BankingEnabled, off.EnableBankingAppId));
        Assert.True((await settings.UpdateAsync(new HouseholdSettingsChange(null, null, EnableBankingAppId: ""), TestContext.Current.CancellationToken)).TryGetValue(out var cleared));
        Assert.Null(cleared.EnableBankingAppId);
    }

    [Fact]
    public async Task Bank_retention_is_unlimited_by_default_between_1_and_600_months_and_can_be_cleared()
    {
        await using var db = await postgres.NewDatabaseAsync();
        var settings = new HouseholdSettings(db);
        Assert.Null((await settings.ReadAsync(TestContext.Current.CancellationToken)).BankRetentionMonths);

        Assert.Equal(12, Months(await settings.UpdateAsync(new HouseholdSettingsChange(null, null, BankRetentionMonths: 12), TestContext.Current.CancellationToken)));
        // A field left out is left unchanged.
        Assert.Equal(12, Months(await settings.UpdateAsync(new HouseholdSettingsChange(null, null, BankingEnabled: true), TestContext.Current.CancellationToken)));
        Assert.Equal(PersonCodes.InvalidBankRetention, Code(await settings.UpdateAsync(new HouseholdSettingsChange(null, null, BankRetentionMonths: 0), TestContext.Current.CancellationToken)));
        Assert.Equal(PersonCodes.InvalidBankRetention, Code(await settings.UpdateAsync(new HouseholdSettingsChange(null, null, BankRetentionMonths: 601), TestContext.Current.CancellationToken)));
        Assert.Equal(600, Months(await settings.UpdateAsync(new HouseholdSettingsChange(null, null, BankRetentionMonths: 600), TestContext.Current.CancellationToken)));
        Assert.Null(Months(await settings.UpdateAsync(new HouseholdSettingsChange(null, null, ClearBankRetention: true), TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task Projection_assumptions_round_trip()
    {
        await using var db = await postgres.NewDatabaseAsync();
        var settings = new HouseholdSettings(db);

        Assert.Equal(ProjectionDefaults.Assumptions, await settings.ProjectionAssumptionsAsync(TestContext.Current.CancellationToken));

        var saved = ProjectionDefaults.Assumptions with
        {
            ClassRates = new Dictionary<string, decimal>(ProjectionDefaults.ClassRates) { [AllocationClasses.Equity] = 0.07m },
            MonthlySavings = 500m,
        };
        await settings.SaveProjectionAssumptionsAsync(saved, TestContext.Current.CancellationToken);

        var read = await settings.ProjectionAssumptionsAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0.07m, read.ClassRates[AllocationClasses.Equity]);
        Assert.Equal(500m, read.MonthlySavings);
        Assert.Equal(saved.Inflation, read.Inflation);
        Assert.Equal(saved.SavingsClass, read.SavingsClass);
        Assert.Equal(saved.FreedPaymentsToSavings, read.FreedPaymentsToSavings);
    }

    [Fact]
    public async Task Projection_assumptions_missing_a_class_take_its_default_rate()
    {
        await using var db = await postgres.NewDatabaseAsync();
        var settings = new HouseholdSettings(db);
        await settings.SaveProjectionAssumptionsAsync(ProjectionDefaults.Assumptions with
        {
            ClassRates = new Dictionary<string, decimal> { [AllocationClasses.Equity] = 0.07m, [AllocationClasses.Cash] = 0m },
        }, TestContext.Current.CancellationToken);

        var read = await settings.ProjectionAssumptionsAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProjectionDefaults.ClassRates.Keys.Order(StringComparer.Ordinal), read.ClassRates.Keys.Order(StringComparer.Ordinal));
        Assert.Equal((0.07m, 0m), (read.ClassRates[AllocationClasses.Equity], read.ClassRates[AllocationClasses.Cash]));
        Assert.Equal(ProjectionDefaults.ClassRates[AllocationClasses.Bond], read.ClassRates[AllocationClasses.Bond]);
        Assert.Equal(ProjectionDefaults.ClassRates[AllocationClasses.RealEstate], read.ClassRates[AllocationClasses.RealEstate]);
    }

    [Fact]
    public async Task Projection_assumptions_round_trip_the_yearly_tables()
    {
        await using var db = await postgres.NewDatabaseAsync();
        var settings = new HouseholdSettings(db);
        var saved = ProjectionDefaults.Assumptions with
        {
            InflationByYear = new Dictionary<int, decimal> { [2026] = 0.025m, [2027] = 0.022m },
            ClassRatesByYear = new Dictionary<string, IReadOnlyDictionary<int, decimal>>
            {
                [AllocationClasses.Cash] = new Dictionary<int, decimal> { [2026] = 0.03m },
            },
            IncomeGrowth = 0.005m,
        };
        await settings.SaveProjectionAssumptionsAsync(saved, TestContext.Current.CancellationToken);

        var read = await settings.ProjectionAssumptionsAsync(TestContext.Current.CancellationToken);

        Assert.Equal(saved.InflationByYear, read.InflationByYear);
        Assert.Equal(saved.ClassRatesByYear[AllocationClasses.Cash], read.ClassRatesByYear![AllocationClasses.Cash]);
        Assert.Equal(0.005m, read.IncomeGrowth);
    }

    [Fact]
    public async Task Old_assumptions_json_reads_with_empty_yearly_tables()
    {
        await using var db = await postgres.NewDatabaseAsync();
        // The shape stored before the yearly tables (#419 PR 3).
        var row = await db.HouseholdSettings.FirstOrDefaultAsync(TestContext.Current.CancellationToken)
            ?? db.HouseholdSettings.Add(new HouseholdSettingsRow()).Entity;
        row.ProjectionAssumptions =
            """{"ClassRates":{"cash":0.02,"equity":0.07},"Inflation":0.021,"MonthlySavings":500,"SavingsClass":"cash","FreedPaymentsToSavings":true}""";
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var read = await new HouseholdSettings(db).ProjectionAssumptionsAsync(TestContext.Current.CancellationToken);

        Assert.True(read.InflationByYear is null or { Count: 0 });
        Assert.True(read.ClassRatesByYear is null or { Count: 0 });
        Assert.Equal(0m, read.IncomeGrowth);
        Assert.Equal((0.04m, 0m), (read.WithdrawalRate, read.MonthlyLivingCost)); // retirement spec §3: never 0 (CapitalNeeded divides by it)
        Assert.Equal((0.021m, 500m, 0.07m), (read.Inflation, read.MonthlySavings, read.ClassRates[AllocationClasses.Equity]));
    }

    private sealed class FixedClock(Instant now) : IClock
    {
        public Instant GetCurrentInstant() => now;
    }

    [Fact]
    public async Task NowAsync_is_in_the_household_zone()
    {
        await using var db = await postgres.NewDatabaseAsync();
        db.HouseholdSettings.Add(new HouseholdSettingsRow { Timezone = "America/New_York" });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var now = await new HouseholdCalendar(db, new FixedClock(Instant.FromUtc(2025, 10, 15, 3, 0))).NowAsync(TestContext.Current.CancellationToken);

        Assert.Equal(new LocalDateTime(2025, 10, 14, 23, 0), now);
    }

    [Fact]
    public async Task Inflation_is_enabled_by_default_and_can_be_turned_off()
    {
        await using var db = await postgres.NewDatabaseAsync();
        var settings = new HouseholdSettings(db);

        Assert.True((await settings.ReadAsync(TestContext.Current.CancellationToken)).InflationEnabled);
        Assert.True((await settings.UpdateAsync(new HouseholdSettingsChange(null, null, InflationEnabled: false), TestContext.Current.CancellationToken)).TryGetValue(out var off));
        Assert.False(off.InflationEnabled);
        Assert.False((await settings.ReadAsync(TestContext.Current.CancellationToken)).InflationEnabled);
    }
}
