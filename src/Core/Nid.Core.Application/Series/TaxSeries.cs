using System.Globalization;
using Nid.Core.Application.Tax;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using NodaTime;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Core.Application.Series;

/// <summary>The net tax of each year's notice the reader holds (<c>tax:yearly</c>), dated 1 January; a notice without one gives no point.</summary>
public sealed class TaxSeries(IInvoker invoker) : ISeriesContribution
{
    private const string Key = "tax:yearly";
    private static readonly SeriesInfo Info = new(Key, SeriesKinds.TaxYearly, null, SeriesUnits.Currency(Currency.Eur), SeriesFrequencies.Yearly, null, null, null);

    public string Module => "core";

    public Task<IReadOnlyList<SeriesInfo>> ListAsync(PersonId reader, VisibilityScope scope, string? q, IReadOnlyCollection<string> kinds, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<SeriesInfo>>(SeriesSearch.Matches(Info, q, kinds) ? [Info] : []);

    public async Task<SeriesPoints?> PointsAsync(PersonId reader, VisibilityScope scope, string key, LocalDate? from, LocalDate? to, CancellationToken ct)
    {
        if (key != Key) return null;
        var list = await invoker.InvokeAsync(new ListTaxAssessmentsQuery(scope, null), ct);
        return list.TryGetValue(out var notices)
            ? new SeriesPoints(Info, SeriesWindow.Within(notices.Items.Where(n => n.NetTax is not null).OrderBy(n => n.Year)
                .Select(n => new SeriesPoint(new LocalDate(n.Year, 1, 1), decimal.Parse(n.NetTax!.Amount, CultureInfo.InvariantCulture))), from, to))
            : null;
    }
}
