using System.Globalization;
using Nid.Core.Application.Dashboard;
using Nid.Core.Application.Projection;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using NodaTime;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Core.Application.Series;

/// <summary>
/// The household's net worth as the dashboard curve shows it (<c>networth</c>, over all its history) and the projection's nominal
/// curve (<c>networth:projected</c>, over its default horizon), both in the selector's scope and the reference currency.
/// </summary>
public sealed class NetWorthSeries(IHouseholdSettings household, IInvoker invoker) : ISeriesContribution
{
    private const string Actual = "networth";
    private const string Projected = "networth:projected";

    public string Module => "core";

    public async Task<IReadOnlyList<SeriesInfo>> ListAsync(PersonId reader, VisibilityScope scope, string? q, IReadOnlyCollection<string> kinds, CancellationToken ct)
    {
        var unit = SeriesUnits.Currency(await household.ReferenceCurrencyAsync(ct));
        return new[] { InfoOf(Actual, SeriesKinds.NetWorth, unit), InfoOf(Projected, SeriesKinds.NetWorthProjected, unit) }
            .Where(i => SeriesSearch.Matches(i, q, kinds)).ToList();
    }

    public async Task<SeriesPoints?> PointsAsync(PersonId reader, VisibilityScope scope, string key, LocalDate? from, LocalDate? to, CancellationToken ct)
    {
        if (key == Actual)
        {
            var history = await invoker.InvokeAsync(new GetNetWorthHistoryQuery(scope, NetWorthPeriod.All), ct);
            return history.TryGetValue(out var view)
                ? new SeriesPoints(InfoOf(Actual, SeriesKinds.NetWorth, $"currency:{view.Currency}"),
                    SeriesWindow.Within(view.Points.Where(p => p.Value is not null).Select(p => new SeriesPoint(p.On, Amount(p.Value!.Amount))), from, to))
                : null;
        }

        if (key != Projected) return null;
        var projection = await invoker.InvokeAsync(new GetProjectionQuery(scope, ProjectionHorizon.Default), ct);
        return projection.TryGetValue(out var plan)
            ? new SeriesPoints(InfoOf(Projected, SeriesKinds.NetWorthProjected, $"currency:{plan.Currency}"),
                SeriesWindow.Within(plan.Points.Select(p => new SeriesPoint(p.On, Amount(p.Nominal))), from, to))
            : null;
    }

    private static SeriesInfo InfoOf(string key, string kind, string unit) => new(key, kind, null, unit, SeriesFrequencies.Monthly, null, null, null);

    private static decimal Amount(string amount) => decimal.Parse(amount, CultureInfo.InvariantCulture);
}
