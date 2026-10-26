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
