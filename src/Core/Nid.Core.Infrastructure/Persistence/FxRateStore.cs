using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Nid.Core.Contracts;
using NodaTime;
using NodaTime.Text;

namespace Nid.Core.Infrastructure.Persistence;

public sealed class FxRateStore(CoreDbContext db) : IFxRateStore
{
    /// <summary>Rows per statement: the ECB history is about 200 000 rows, far too many parameters for one.</summary>
    public const int ChunkSize = 5000;

    public async Task<int> WriteAsync(IReadOnlyList<FxRate> rates, CancellationToken ct)
    {
        var written = 0;
        foreach (var chunk in rates.Chunk(ChunkSize))
        {
            // Dates travel as ISO text and are cast in SQL: arrays of NodaTime values are not worth the mapping risk.
            var dates = chunk.Select(r => LocalDatePattern.Iso.Format(r.OnDate)).ToArray();
            var quotes = chunk.Select(r => r.Quote.Code).ToArray();
            var values = chunk.Select(r => r.Rate).ToArray();
            var sources = chunk.Select(r => r.Source.ToWire()).ToArray();

            // The WHERE compares priorities (FxRateSource): never replace a higher one, replace an equal one.
            written += await db.Database.ExecuteSqlAsync($"""
                insert into core.fx_rate (on_date, quote, rate, source, fetched_at)
                select d::date, q, r, s, now()
                from unnest({dates}::text[], {quotes}::text[], {values}::numeric[], {sources}::text[]) as t(d, q, r, s)
                on conflict (on_date, quote) do update
                set rate = excluded.rate, source = excluded.source, fetched_at = excluded.fetched_at
                where (case fx_rate.source when 'manual' then 2 when 'subtracker_data' then 1 else 0 end)
                   <= (case excluded.source when 'manual' then 2 when 'subtracker_data' then 1 else 0 end)
                """, ct);
        }

        return written;
    }

    public async Task<IReadOnlyList<FxRate>> ReadAsync(IReadOnlyCollection<Currency> quotes, LocalDate from, LocalDate to, CancellationToken ct)
    {
        var codes = quotes.Select(q => q.Code).ToList();
        var rows = await db.FxRates.AsNoTracking()
            .Where(r => codes.Contains(r.Quote) && r.OnDate >= from && r.OnDate <= to)
            .OrderBy(r => r.Quote).ThenBy(r => r.OnDate)
            .ToListAsync(ct);
        return rows.Select(ToRate).ToList();
    }

    public async Task<IReadOnlyDictionary<Currency, decimal>> PreviousAsync(IReadOnlyCollection<Currency> quotes, LocalDate date, CancellationToken ct)
    {
        var codes = quotes.Select(q => q.Code).ToArray();
        var rows = await db.FxRates
            .FromSql($"select distinct on (quote) * from core.fx_rate where quote = any({codes}) and on_date < {date} order by quote, on_date desc")
            .AsNoTracking()
            .ToListAsync(ct);
        return rows.ToDictionary(r => ToRate(r).Quote, r => r.Rate);
    }

    public async Task<IReadOnlySet<LocalDate>> DatesFromAsync(FxRateSource source, LocalDate from, CancellationToken ct)
    {
        var wire = source.ToWire();
        var dates = await db.FxRates.AsNoTracking()
            .Where(r => r.Source == wire && r.OnDate >= from)
            .Select(r => r.OnDate).Distinct()
            .ToListAsync(ct);
        return dates.ToHashSet();
    }

    public async Task<IReadOnlyList<FxQuoteSpan>> QuotesAsync(CancellationToken ct)
    {
        var rows = await db.FxRates.AsNoTracking()
            .GroupBy(r => r.Quote)
            .Select(g => new { Quote = g.Key, First = g.Min(r => r.OnDate), Last = g.Max(r => r.OnDate) })
            .OrderBy(g => g.Quote)
            .ToListAsync(ct);
        return rows.Select(r => new FxQuoteSpan(
            Currency.TryCreate(r.Quote.Trim(), out var quote)
                ? quote
                : throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"Stored quote '{r.Quote}' is not a currency.")),
            r.First, r.Last)).ToList();
    }

    private static FxRate ToRate(FxRateRow row)
        => new(row.OnDate,
            Currency.TryCreate(row.Quote.Trim(), out var quote)
                ? quote
                : throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"Stored quote '{row.Quote}' is not a currency.")),
            row.Rate, FxRateSourceNames.FromWire(row.Source));
}
