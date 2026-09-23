using System.Diagnostics.CodeAnalysis;
using NodaTime;

namespace Nid.Core.Contracts;

/// <summary>The rate a conversion needed and did not find: <paramref name="Currency"/> for 1 USD on <paramref name="Date"/>.</summary>
public sealed record FxMiss(Currency Currency, LocalDate Date);

/// <summary>
/// The rates one read needs, loaded once (design §5.2). Pure: conversion never touches the database. A → B is
/// <c>amount × rate_B / rate_A</c> with USD = 1 (<c>core-data-model.md</c> §8.2), in full precision; rounding is
/// the caller's, once, on the total.
/// </summary>
public sealed class RateTable
{
    private readonly Dictionary<Currency, (LocalDate[] Dates, decimal[] Rates)> series;

    public RateTable(IEnumerable<FxRate> rates, LocalDate today, int maxGapDays)
    {
        Today = today;
        MaxGapDays = maxGapDays;
        series = rates
            .GroupBy(r => r.Quote)
            .ToDictionary(g => g.Key, g =>
            {
                var sorted = g.OrderBy(r => r.OnDate).ToArray();
                return (sorted.Select(r => r.OnDate).ToArray(), sorted.Select(r => r.Rate).ToArray());
            });
    }

    public static RateTable Empty(LocalDate today, int maxGapDays) => new([], today, maxGapDays);

    public LocalDate Today { get; }

    public int MaxGapDays { get; }

    /// <summary>
    /// Converts at the latest rate on or before <paramref name="date"/>, within <see cref="MaxGapDays"/>. A projected
    /// charge (a date after today) converts at the latest rate on or before today: an estimate the caller labels.
    /// </summary>
    public bool TryConvert(decimal amount, Currency from, Currency to, LocalDate date, bool projected,
        out decimal converted, [NotNullWhen(false)] out FxMiss? miss)
    {
        converted = 0m;
        miss = null;
        if (from == to)
        {
            converted = amount;
            return true;
        }

        var at = projected && date > Today ? Today : date;
        if (!TryRate(from, at, out var rateFrom))
        {
            miss = new FxMiss(from, at);
            return false;
        }

        if (!TryRate(to, at, out var rateTo))
        {
            miss = new FxMiss(to, at);
            return false;
        }

        converted = amount * rateTo / rateFrom;
        return true;
    }

    private bool TryRate(Currency currency, LocalDate at, out decimal rate)
    {
        rate = 1m;
        if (currency == Currency.Usd)
            return true;

        if (!series.TryGetValue(currency, out var quote))
            return false;

        var index = Array.BinarySearch(quote.Dates, at);
        if (index < 0)
            index = ~index - 1; // the last date before `at`
        if (index < 0 || Period.DaysBetween(quote.Dates[index], at) > MaxGapDays)
            return false;

        rate = quote.Rates[index];
        return true;
    }
}
