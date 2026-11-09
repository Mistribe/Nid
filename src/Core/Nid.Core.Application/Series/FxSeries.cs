using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using NodaTime;

namespace Nid.Core.Application.Series;

/// <summary>One series per stored quote, <c>fx:EUR</c>: units of the quote for 1 USD, daily.</summary>
public sealed class FxSeries(IFxRateStore rates) : ISeriesContribution
{
    private const string Prefix = "fx:";

    public string Module => "core";

    public async Task<IReadOnlyList<SeriesInfo>> ListAsync(PersonId reader, VisibilityScope scope, string? q, IReadOnlyCollection<string> kinds, CancellationToken ct)
        => (await rates.QuotesAsync(ct)).Select(s => Info(s.Quote) with { First = s.First, Last = s.Last }).Where(i => SeriesSearch.Matches(i, q, kinds)).ToList();

    public async Task<SeriesPoints?> PointsAsync(PersonId reader, VisibilityScope scope, string key, LocalDate? from, LocalDate? to, CancellationToken ct)
    {
        if (!key.StartsWith(Prefix, StringComparison.Ordinal) || !Currency.TryCreate(key[Prefix.Length..], out var quote)) return null;
        if ((await rates.QuotesAsync(ct)).FirstOrDefault(s => s.Quote == quote) is not { } span) return null;
        var stored = await rates.ReadAsync([quote], from ?? span.First, to ?? span.Last, ct);
        return new SeriesPoints(Info(quote), stored.Select(r => new SeriesPoint(r.OnDate, r.Rate)).ToList());
    }

    private static SeriesInfo Info(Currency quote)
        => new(Prefix + quote.Code, SeriesKinds.Fx, quote.Code, SeriesUnits.Rate(quote), SeriesFrequencies.Daily, null, null, null);
}
