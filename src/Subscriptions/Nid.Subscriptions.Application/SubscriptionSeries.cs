using System.Globalization;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using NodaTime;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Subscriptions.Application;

/// <summary>The household's subscription spend of each year, paid and still to come (<c>subscriptions:yearly</c>), as the overview's years show it, dated 1 January.</summary>
public sealed class SubscriptionSeries(IHouseholdSettings household, IInvoker invoker) : ISeriesContribution
{
    private const string Key = "subscriptions:yearly";

    public string Module => "subscriptions";

    public async Task<IReadOnlyList<SeriesInfo>> ListAsync(PersonId reader, VisibilityScope scope, string? q, IReadOnlyCollection<string> kinds, CancellationToken ct)
    {
        var info = InfoOf(SeriesUnits.Currency(await household.ReferenceCurrencyAsync(ct)));
        return SeriesSearch.Matches(info, q, kinds) ? [info] : [];
    }

    public async Task<SeriesPoints?> PointsAsync(PersonId reader, VisibilityScope scope, string key, LocalDate? from, LocalDate? to, CancellationToken ct)
    {
        if (key != Key) return null;
        var read = await invoker.InvokeAsync(new GetSubscriptionsOverviewQuery(scope), ct);
        return read.TryGetValue(out var overview)
            ? new SeriesPoints(InfoOf($"currency:{overview.Currency}"),
                SeriesWindow.Within(overview.Years.Where(y => y.Total is not null)
                    .Select(y => new SeriesPoint(new LocalDate(y.Year, 1, 1), decimal.Parse(y.Total!.Amount, CultureInfo.InvariantCulture))), from, to))
            : null;
    }

    private static SeriesInfo InfoOf(string unit) => new(Key, SeriesKinds.SubscriptionsYearly, null, unit, SeriesFrequencies.Yearly, null, null, null);
}
