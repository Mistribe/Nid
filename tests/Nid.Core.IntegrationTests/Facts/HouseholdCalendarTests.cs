using Microsoft.EntityFrameworkCore;
using Nid.Core.Infrastructure.Persistence;
using Nid.TestSupport;
using NodaTime;

namespace Nid.Core.IntegrationTests.Facts;

[Collection("postgres")]
public class HouseholdCalendarTests(PostgresFixture postgres)
{
    private sealed class FixedClock(Instant now) : IClock
    {
        public Instant GetCurrentInstant() => now;
    }

    private static readonly Instant EveningUtc = Instant.FromUtc(2026, 3, 10, 23, 30);

    [Fact]
    public async Task Without_settings_today_is_the_day_in_Paris()
    {
        await using var db = await postgres.NewDatabaseAsync();

        Assert.Equal(new LocalDate(2026, 3, 11), await new HouseholdCalendar(db, new FixedClock(EveningUtc)).TodayAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Today_follows_the_household_zone()
    {
        await using var db = await postgres.NewDatabaseAsync();
        db.HouseholdSettings.Add(new HouseholdSettingsRow { Timezone = "America/New_York" });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.Equal(new LocalDate(2026, 3, 10), await new HouseholdCalendar(db, new FixedClock(EveningUtc)).TodayAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task An_unknown_zone_falls_back_to_Paris()
    {
        await using var db = await postgres.NewDatabaseAsync();
        db.HouseholdSettings.Add(new HouseholdSettingsRow { Timezone = "Mars/Olympus" });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.Equal(new LocalDate(2026, 3, 11), await new HouseholdCalendar(db, new FixedClock(EveningUtc)).TodayAsync(TestContext.Current.CancellationToken));
    }
}
