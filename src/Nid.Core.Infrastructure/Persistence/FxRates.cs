using Nid.Core.Contracts;
using NodaTime;

namespace Nid.Core.Infrastructure.Persistence;

public sealed class FxRates(IFxRateStore store, FxOptions options, IClock clock) : IFxRates
{
    public async Task<RateTable> LoadAsync(IReadOnlyCollection<Currency> quotes, LocalDate from, LocalDate to, CancellationToken ct)
    {
        var today = clock.GetCurrentInstant().InUtc().Date;
        var needed = quotes.Where(q => q != Currency.Usd).Distinct().ToList();
        if (needed.Count == 0)
            return RateTable.Empty(today, options.MaxGapDays);

        var end = to > today ? to : today;
        var rows = await store.ReadAsync(needed, from.PlusDays(-options.MaxGapDays), end, ct);
        return new RateTable(rows, today, options.MaxGapDays);
    }
}
