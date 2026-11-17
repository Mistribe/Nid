using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Nid.Core.Application.Tax;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;

namespace Nid.Core.Application.Dashboard;

/// <summary>
/// Notifications design §2.3: what the dashboard says about the household as a whole, as signals: a currency without a rate, and
/// the month's routine when a value is to update. Neither is about one item, so neither carries an item id or an amount. Issue #81:
/// the net worth milestone, opt-in, whose figures only the centre shows.
/// </summary>
public sealed class CoreNotificationSource(
    DashboardReader dashboard, MonthChangesReader changes, MonthChangeWords monthWords, IPersonalAccessTokens tokens,
    IHouseholdCalendar calendar, IClock clock, TaxCalendarEvents taxCalendar, ISignalSettings settings,
    IEnumerable<INetWorthContribution> netWorth, IHouseholdSettings household, IFxRates fx, ILogger<CoreNotificationSource> logger) : INotificationSource
{
    public const string FxRateMissing = "fx_rate_missing";
    public const string RoutineDue = "routine_due";
    public const string TokenExpiring = "token_expiring";
    public const string MonthChanges = "month_changes";
    public const string NetWorthMilestone = "net_worth_milestone";

    private static readonly IReadOnlyDictionary<string, string> NoParams = new Dictionary<string, string>();
    private static readonly HashSet<string> TaxKinds = [UpcomingKinds.TaxFilingDeadline, UpcomingKinds.TaxNewRate, UpcomingKinds.TaxBalanceDue];

    public string Name => "core";

    public IReadOnlyList<NotificationKind> Kinds { get; } =
    [
        new(FxRateMissing, "finding", NotificationLevel.Info, "core", null),
        new(RoutineDue, "finding", NotificationLevel.Action, "core", null),
        new(TokenExpiring, NotificationFamilies.Security, NotificationLevel.Action, "core", 7),
        new(MonthChanges, "finding", NotificationLevel.Info, "core", null),
        // Issue #409: the household's tax dates, the reader's own (Moi) notice giving the balance.
        new(UpcomingKinds.TaxFilingDeadline, "due_date", NotificationLevel.Action, "core", 14),
        new(UpcomingKinds.TaxNewRate, "due_date", NotificationLevel.Info, "core", 7),
        new(UpcomingKinds.TaxBalanceDue, "due_date", NotificationLevel.Info, "core", 7),
        // Issue #81: the step, in the household currency.
        new(NetWorthMilestone, "finding", NotificationLevel.Info, "core", null, new SignalThreshold(50_000m, 1_000m, 10_000_000m, "eur")),
    ];

    public async Task<Result<IReadOnlyList<Signal>>> SignalsAsync(PersonId reader, LocalDate today, LocalDate horizon, CancellationToken ct)
    {
        var read = await dashboard.ReadAsync(reader, VisibilityScope.Me, today, ct);
        if (!read.TryGetValue(out var view))
            return Result.Failure<IReadOnlyList<Signal>>(read.TryGetFailure(out var failure) ? failure! : new Failure("core.unknown", "The dashboard could not be read.", null));

        var signals = new List<Signal>();
        if (view!.PeopleMissingCurrency is { Length: > 0 } currency)
            signals.Add(new Signal(SignalKeys.Household(FxRateMissing, currency), FxRateMissing, [], null, new Dictionary<string, string> { ["currency"] = currency }));
        // The routine is the web's: only the values to update count (web/src/features/dashboard/todo.ts), not the income gap.
        if (view.Todo.Any(t => t.Kind is "stale_value" or "no_value"))
            signals.Add(new Signal(SignalKeys.Household(RoutineDue, $"{today:yyyy-MM}"), RoutineDue, [], null, NoParams));

        // Issue #415: « Depuis le mois dernier » once a month, in the digest; nothing in the first month or when nothing moved.
        if (await MonthWordsAsync(reader, today, ct) is { Count: > 0 } words)
            signals.Add(new Signal(SignalKeys.Household(MonthChanges, $"{today:yyyy-MM}"), MonthChanges, [], null, words));

        if ((await settings.ForAsync(reader, ct)).TryGetValue(NetWorthMilestone, out var step)
            && await MilestoneAsync(reader, view, today, step, ct) is { } milestone)
            signals.Add(milestone);

        // The expiry day is the household's, like today and the horizon: the same day in the key and the title.
        var now = clock.GetCurrentInstant();
        var zone = await calendar.ZoneAsync(ct);
        foreach (var t in await tokens.ListAsync(reader, ct))
        {
            var on = t.ExpiresAt.InZone(zone).Date;
            if (t.RevokedAt is null && t.ExpiresAt > now && on <= horizon)
                signals.Add(new Signal($"{TokenExpiring}:{t.Id:D}:{on:yyyy-MM-dd}", TokenExpiring, [], on, NoParams));
        }
        // Issue #409: the dashboard read has the tax dates of its window; only a lead time beyond it reads them again.
        var taxDates = horizon <= today.PlusDays(DashboardMath.UpcomingDays)
            ? view.Upcoming.Where(e => e.Id is null && e.On <= horizon && TaxKinds.Contains(e.Kind)).ToList()
            : await taxCalendar.EventsAsync(reader, VisibilityScope.Me, today, horizon, ct);
        foreach (var e in taxDates)
            signals.Add(new Signal(SignalKeys.Household(e.Kind, $"{e.On:yyyy-MM-dd}"), e.Kind, [], e.On,
                e.Label is { } note ? new Dictionary<string, string> { ["note"] = note } : NoParams));
        return Result.Success<IReadOnlyList<Signal>>(signals);
    }

    /// <summary>
    /// Issue #81: today's net worth against the previous month-end's, on the curve's figures; the key is the step crossed, so the
    /// signal holds for the month after the crossing and resolves once that month-end is above it. A failed read is logged, none raised.
    /// </summary>
    private async Task<Signal?> MilestoneAsync(PersonId reader, DashboardView view, LocalDate today, decimal step, CancellationToken ct)
    {
        decimal? now = view.Envelopes > 0 && view.NetWorth.Total is { } total ? decimal.Parse(total.Amount, CultureInfo.InvariantCulture) : null;
        var monthEnd = new LocalDate(today.Year, today.Month, 1).PlusDays(-1);
        var read = await Contributions.CollectAsync(netWorth, c => c.NetWorthHistoryAsync(reader, VisibilityScope.Me, [monthEnd], ct));
        if (!read.TryGetValue(out var parts))
        {
            logger.LogWarning("The net worth history of {Person} could not be read: {Code}", reader.Value, read.TryGetFailure(out var f) ? f!.Code : "core.unknown");
            return null;
        }
        var points = parts!.SelectMany(p => p).ToList();
        var reference = await household.ReferenceCurrencyAsync(ct);
        var foreign = points.SelectMany(p => p.Values.Keys).Where(c => c != reference).Distinct().ToList();
        // The reference's own quote too: rates are per USD.
        var rates = foreign.Count == 0 ? RateTable.Empty(today, 0) : await fx.LoadAsync(foreign.Append(reference).ToList(), monthEnd, today, ct);
        var sum = DashboardMath.Sum.Of(DashboardMath.Merge(points.Select(p => p.Values)), reference, rates, monthEnd);
        decimal? before = points.Sum(p => p.Envelopes) > 0 && sum.Missing is null ? sum.Total : null;

        if (Dashboard.NetWorthMilestone.Crossed(before, now, step) is not { } crossed)
            return null;
        var figures = new Dictionary<string, string>
        {
            ["net_worth_amount"] = now!.Value.ToString(CultureInfo.InvariantCulture),
            ["step_amount"] = crossed.ToString(CultureInfo.InvariantCulture),
            ["currency"] = reference.Code,
        };
        return new Signal(SignalKeys.Household(NetWorthMilestone, crossed.ToString("0", CultureInfo.InvariantCulture)), NetWorthMilestone, [], null, figures);
    }

    /// <summary>
    /// Issue #415: the month's words, compared once the month has some (its key is monthly). A failed read drops this signal only,
    /// never the others: logged, empty.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, string>> MonthWordsAsync(PersonId reader, LocalDate today, CancellationToken ct)
    {
        var month = today.ToYearMonth();
        if (monthWords.For(reader, month) is { } kept)
            return kept;
        var read = await changes.ReadAsync(reader, VisibilityScope.Me, today, ct);
        if (!read.TryGetValue(out var moved))
        {
            logger.LogWarning("The month's changes of {Person} could not be read: {Code}", reader.Value, read.TryGetFailure(out var f) ? f!.Code : "core.unknown");
            return NoParams;
        }
        var words = MonthChangeParams(moved!);
        if (words.Count > 0)
            monthWords.Keep(reader, month, words);
        return words;
    }

    /// <summary>
    /// Issue #415: each figure that moved as « up » or « down », then its causes as « key+ » or « key- », joined by « | ». Codes only:
    /// no amount leaves the application (notifications design N6), the screen has the figures.
    /// </summary>
    public static Dictionary<string, string> MonthChangeParams(MonthChangesView view)
    {
        var words = new Dictionary<string, string>();
        foreach (var (name, figure) in new[] { ("net_worth", view.NetWorth), ("commitments", view.Commitments), ("income", view.Income) })
        {
            if (figure is null || Sign(figure.Change) == 0) continue;
            words[name] = string.Join('|', figure.Causes.Select(c => c.Key + (Sign(c.Change) > 0 ? "+" : "-"))
                .Prepend(Sign(figure.Change) > 0 ? "up" : "down"));
        }
        return words;
    }

    private static int Sign(DashboardMoneyView money) => Math.Sign(decimal.Parse(money.Amount, System.Globalization.CultureInfo.InvariantCulture));
}

/// <summary>
/// Issue #415: a reader's month-change words, kept for the month once there are some, so the comparison is not redone at each
/// evaluation. One entry per reader, replaced the next month; a restart only recomputes.
/// </summary>
public sealed class MonthChangeWords
{
    private readonly ConcurrentDictionary<PersonId, (YearMonth Month, IReadOnlyDictionary<string, string> Words)> kept = new();

    public IReadOnlyDictionary<string, string>? For(PersonId reader, YearMonth month)
        => kept.TryGetValue(reader, out var k) && k.Month == month ? k.Words : null;

    public void Keep(PersonId reader, YearMonth month, IReadOnlyDictionary<string, string> words) => kept[reader] = (month, words);
}

/// <summary>Issue #81: the net worth milestone, a multiple of the step reached since the previous month-end.</summary>
public static class NetWorthMilestone
{
    /// <summary>
    /// The highest multiple of <paramref name="step"/> that <paramref name="now"/> reached and <paramref name="before"/> was below;
    /// null when there is none, or either figure is missing.
    /// </summary>
    public static decimal? Crossed(decimal? before, decimal? now, decimal step)
    {
        if (before is not { } b || now is not { } n || step <= 0m)
            return null;
        var reached = Math.Floor(n / step) * step;
        return reached >= step && b < reached ? reached : null;
    }
}
