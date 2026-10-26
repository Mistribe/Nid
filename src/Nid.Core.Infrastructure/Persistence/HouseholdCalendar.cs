using Microsoft.EntityFrameworkCore;
using Nid.Core.Contracts;
using NodaTime;

namespace Nid.Core.Infrastructure.Persistence;

/// <summary>
/// <c>core-data-model.md</c> §2. Only 2A's rules use it; stage 1 callers still take the UTC date (spec §5, a known
/// divergence recorded in <c>core-data-model.md</c> §12).
/// </summary>
public sealed class HouseholdCalendar(CoreDbContext db, IClock clock) : IHouseholdCalendar
{
    private static readonly DateTimeZone Fallback = DateTimeZoneProviders.Tzdb["Europe/Paris"];

    public async Task<LocalDate> TodayAsync(CancellationToken ct) => (await NowAsync(ct)).Date;

    public async Task<LocalDateTime> NowAsync(CancellationToken ct)
    {
        var id = await db.HouseholdSettings.AsNoTracking().Select(s => s.Timezone).FirstOrDefaultAsync(ct);
        var zone = id is null ? Fallback : DateTimeZoneProviders.Tzdb.GetZoneOrNull(id) ?? Fallback;
        return clock.GetCurrentInstant().InZone(zone).LocalDateTime;
    }
}
