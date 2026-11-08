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

        // A projected piece converts at today's rate (§5.1), whatever its own charge date: when every foreign
        // piece lies beyond today (a window entirely in the future), `from` is later than today and the range
        // must still reach back from today, or the rate that projection needs is never loaded.
        var start = from < today ? from : today;
        var end = to > today ? to : today;
        var rows = await store.ReadAsync(needed, start.PlusDays(-options.MaxGapDays), end, ct);
        return new RateTable(rows, today, options.MaxGapDays);
    }
}
