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

    [Fact]
    public async Task Reads_the_cap_the_currency_and_the_number_of_adults()
    {
        await using var db = await postgres.NewDatabaseAsync();
        var registry = new PersonRegistry(db, PostgresFixture.Clock);
        await registry.ProvisionAdultAsync("sub-a", "Alice", null, default);
        await registry.PreRegisterAdultAsync("Bob", "bob@example.test", default);
        await registry.CreateDependentAsync("Kid", default);

        var settings = await new HouseholdSettings(db).ReadAsync(default);

        Assert.Equal(new HouseholdSettingsInfo(2, Currency.Eur, 2, false, false), settings);
    }

    [Fact]
    public async Task Changes_the_cap_and_the_currency_but_never_below_the_number_of_adults()
    {
        await using var db = await postgres.NewDatabaseAsync();
        var registry = new PersonRegistry(db, PostgresFixture.Clock);
        await registry.ProvisionAdultAsync("sub-a", "Alice", null, default);
        await registry.PreRegisterAdultAsync("Bob", "bob@example.test", default);
        var settings = new HouseholdSettings(db);

        Assert.Equal(PersonCodes.CapBelowCount, Code(await settings.UpdateAsync(new HouseholdSettingsChange(1, null), default)));
        Assert.Equal(PersonCodes.CapBelowCount, Code(await settings.UpdateAsync(new HouseholdSettingsChange(0, null), default)));
        Assert.Equal(PersonCodes.UnknownCurrency, Code(await settings.UpdateAsync(new HouseholdSettingsChange(null, "usd"), default)));

        Assert.True((await settings.UpdateAsync(new HouseholdSettingsChange(4, "USD"), default)).TryGetValue(out var changed));
        Assert.Equal(new HouseholdSettingsInfo(4, Currency.Usd, 2, false, false), changed);

        Assert.True((await settings.UpdateAsync(new HouseholdSettingsChange(null, null, IrlEnabled: true), default)).TryGetValue(out var irl));
        Assert.True(irl.IrlEnabled);
        Assert.Equal(Currency.Usd, await settings.ReferenceCurrencyAsync(default));
    }

    [Fact]
    public async Task Banking_is_off_by_default_and_keeps_its_application_id()
    {
        await using var db = await postgres.NewDatabaseAsync();
        var settings = new HouseholdSettings(db);

        var initial = await settings.ReadAsync(default);
        Assert.Equal((false, null), (initial.BankingEnabled, initial.EnableBankingAppId));

        Assert.True((await settings.UpdateAsync(new HouseholdSettingsChange(null, null, BankingEnabled: true, EnableBankingAppId: " 0a1b-app "), default))
            .TryGetValue(out var on));
        Assert.Equal((true, "0a1b-app"), (on.BankingEnabled, on.EnableBankingAppId));

        // A field left out is left unchanged; an empty id clears it.
        Assert.True((await settings.UpdateAsync(new HouseholdSettingsChange(null, null, BankingEnabled: false), default)).TryGetValue(out var off));
        Assert.Equal((false, "0a1b-app"), (off.BankingEnabled, off.EnableBankingAppId));
        Assert.True((await settings.UpdateAsync(new HouseholdSettingsChange(null, null, EnableBankingAppId: ""), default)).TryGetValue(out var cleared));
        Assert.Null(cleared.EnableBankingAppId);
    }

    [Fact]
    public async Task Projection_assumptions_round_trip()
    {
        await using var db = await postgres.NewDatabaseAsync();
        var settings = new HouseholdSettings(db);

        Assert.Equal(ProjectionDefaults.Assumptions, await settings.ProjectionAssumptionsAsync(default));

        var saved = ProjectionDefaults.Assumptions with
        {
            ClassRates = new Dictionary<string, decimal>(ProjectionDefaults.ClassRates) { [AllocationClasses.Equity] = 0.07m },
            MonthlySavings = 500m,
        };
        await settings.SaveProjectionAssumptionsAsync(saved, default);

        var read = await settings.ProjectionAssumptionsAsync(default);
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
        }, default);

        var read = await settings.ProjectionAssumptionsAsync(default);

        Assert.Equal(ProjectionDefaults.ClassRates.Keys.Order(StringComparer.Ordinal), read.ClassRates.Keys.Order(StringComparer.Ordinal));
        Assert.Equal((0.07m, 0m), (read.ClassRates[AllocationClasses.Equity], read.ClassRates[AllocationClasses.Cash]));
        Assert.Equal(ProjectionDefaults.ClassRates[AllocationClasses.Bond], read.ClassRates[AllocationClasses.Bond]);
        Assert.Equal(ProjectionDefaults.ClassRates[AllocationClasses.RealEstate], read.ClassRates[AllocationClasses.RealEstate]);
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
        await db.SaveChangesAsync();

        var now = await new HouseholdCalendar(db, new FixedClock(Instant.FromUtc(2025, 10, 15, 3, 0))).NowAsync(default);

        Assert.Equal(new LocalDateTime(2025, 10, 14, 23, 0), now);
    }
}
