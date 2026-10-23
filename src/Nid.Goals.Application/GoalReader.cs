using System.Globalization;
using Nid.Core.Contracts;
using Nid.Goals.Contracts;
using Nid.Goals.Domain;
using Nid.Investments.Contracts;
using NodaTime;
using UnambitiousFx.Functional;

namespace Nid.Goals.Application;

/// <summary>
/// Spec §3, D1, D5, D6, D10, D13, D14: the computed view of goals as a reader sees them in a scope. One place for the list and the
/// detail, read in a fixed number of queries whatever the number of goals and envelopes.
/// </summary>
public sealed class GoalReader(
    IGoalStore goals, IVisibleItems visible, IEnvelopeFunding envelopes, IEnumerable<ICommitmentsContribution> commitments,
    IHouseholdSettings household, IFxRates fx, IHouseholdCalendar calendar)
{
    private static readonly StringComparer French = StringComparer.Create(new CultureInfo("fr-FR"), true);

    public async Task<Result<IReadOnlyList<GoalDetail>>> ReadAsync(
        PersonId reader, VisibilityScope scope, IReadOnlyList<(Goal Goal, ItemAccess Item)> found, CancellationToken ct)
    {
        var today = await calendar.TodayAsync(ct);
        var ids = found.Select(f => f.Goal.ItemId).ToList();
        var links = (await goals.FundingOfAsync(ids, ct)).ToLookup(f => f.Goal);
        var free = found.Where(f => !links[f.Goal.ItemId].Any()).Select(f => f.Goal.ItemId).ToList();
        var entries = (free.Count == 0 ? [] : await goals.EntriesOfAsync(free, ct)).ToLookup(e => e.Goal);

        // D1: only the envelopes the reader sees in the scope; the others never reach a figure.
        var linked = links.SelectMany(l => l).Select(f => f.Envelope).Distinct().ToList();
        var seen = new Dictionary<ItemId, EnvelopeFundingFigures>();
        if (linked.Count > 0)
        {
            var read = await envelopes.ReadAsync(reader, scope, linked, today, ct);
            if (!read.TryGetValue(out var figures))
                return Result.Failure<IReadOnlyList<GoalDetail>>(read.TryGetFailure(out var failure) ? failure! : throw new InvalidOperationException());
            seen = figures!.ToDictionary(f => f.Envelope);
        }

        // D10: the shares on each envelope of the open goals the reader sees in the scope, and of the open goals read here (her own
        // private goal read in the scope household still holds its share).
        var shares = new Dictionary<ItemId, int>();
        if (seen.Count > 0)
        {
            var openGoals = (await visible.ForAsync(reader, scope, ct)) // active items only: the open goals
                .Concat(found.Where(f => f.Item.ArchivedAt is null).Select(f => f.Goal.ItemId)).ToHashSet();
            shares = (await goals.FundingOnAsync(seen.Keys.ToList(), ct)).Where(f => openGoals.Contains(f.Goal))
                .GroupBy(f => f.Envelope).ToDictionary(g => g.Key, g => g.Sum(f => f.ShareBp));
        }

        // D5: the commitments, read once, only for an emergency fund.
        var parts = new List<CommitmentsPart>();
        if (found.Any(f => f.Goal.Kind == GoalKinds.EmergencyFund))
            foreach (var contribution in commitments)
            {
                var part = await contribution.CommitmentsAsync(reader, scope, today, ct);
                if (part.TryGetFailure(out var failure))
                    return Result.Failure<IReadOnlyList<GoalDetail>>(failure!);
                part.TryGetValue(out var value);
                parts.Add(value!);
            }

        // D6: everything in the reference currency, at today's rate, as the dashboard.
        var reference = await household.ReferenceCurrencyAsync(ct);
        var foreign = seen.Values.SelectMany(f => f.Value is { } v ? [v.Currency, f.NetContributions12Months.Currency] : new[] { f.NetContributions12Months.Currency })
            .Concat(parts.SelectMany(p => p.Amortized.Keys)).Where(c => c != reference).Distinct().ToList();
        // The reference's own quote is needed too: rates are per USD (RateTable converts through it).
        var rates = foreign.Count == 0 ? RateTable.Empty(today, 0) : await fx.LoadAsync(foreign.Append(reference).ToList(), today, today, ct);

        var sources = Sources(parts, reference, rates, today);
        return Result.Success<IReadOnlyList<GoalDetail>>(found
            .Select(f => Detail(f.Goal, f.Item, links[f.Goal.ItemId].ToList(), entries[f.Goal.ItemId].ToList(), seen, shares, sources, reference, rates, today))
            .ToList());
    }

    /// <summary>Spec §5 list order: past due, then behind, then by target date (none last), then by name.</summary>
    public static IReadOnlyList<GoalSummary> Ordered(IEnumerable<GoalSummary> goals)
        => goals.OrderBy(g => g.Status switch { GoalStatuses.PastDue => 0, GoalStatuses.Behind => 1, _ => 2 })
            .ThenBy(g => g.TargetDate is null).ThenBy(g => g.TargetDate)
            .ThenBy(g => g.Name, French).ThenBy(g => g.Id)
            .ToList();

    private static GoalDetail Detail(
        Goal goal, ItemAccess item, IReadOnlyList<Funding> links, IReadOnlyList<GoalEntry> entries, IReadOnlyDictionary<ItemId, EnvelopeFundingFigures> seen,
        IReadOnlyDictionary<ItemId, int> shares, IReadOnlyList<(GoalCommitmentView View, decimal Monthly)> sources, Currency reference, RateTable rates, LocalDate today)
    {
        var flags = new List<string>();
        var lines = new List<GoalFundingView>();
        var inputs = new List<FundingInput>();
        var partial = false;
        foreach (var link in links)
        {
            if (!seen.TryGetValue(link.Envelope, out var envelope))
            {
                partial = true;
                continue;
            }
            var value = Convert(envelope.Value, reference, rates, today);
            var net = Convert(envelope.NetContributions12Months, reference, rates, today);
            var missing = value.Missing ?? net.Missing;
            var lineFlags = new List<string>();
            if (envelope.Value is null) lineFlags.Add(GoalFlags.NoValue);
            if (envelope.Closed) lineFlags.Add(GoalFlags.ClosedEnvelope);
            if (envelope.Stale) lineFlags.Add(GoalFlags.Stale);
            if (missing is not null) lineFlags.Add(GoalFlags.MissingCurrency);
            if (shares.GetValueOrDefault(link.Envelope) > 10_000) lineFlags.Add(GoalFlags.OverAllocated);

            // D6: an envelope without a rate counts as one the reader does not see.
            if (missing is null)
                inputs.Add(new FundingInput(value.Amount, net.Amount ?? 0m, link.ShareBp, envelope.Closed));
            decimal? contribution = missing is not null ? null : envelope.Closed ? 0m : (value.Amount ?? 0m) * link.ShareBp / 10_000m;
            lines.Add(new GoalFundingView(
                link.Envelope.Value, envelope.Name, link.ShareBp,
                envelope.Value is { } v ? GoalMoneyView.Exact(v.Amount, v.Currency) : null,
                contribution is { } c ? GoalMoneyView.Of(c, reference) : null,
                envelope.AsOf, envelope.Stale, envelope.Estimated, lineFlags));
        }
        if (partial) flags.Add(GoalFlags.Partial);
        if (lines.Any(l => l.Flags.Contains(GoalFlags.MissingCurrency))) flags.Add(GoalFlags.MissingCurrency);
        if (lines.Any(l => l.Flags.Contains(GoalFlags.ClosedEnvelope))) flags.Add(GoalFlags.ClosedEnvelope);

        var emergency = goal.Kind == GoalKinds.EmergencyFund;
        decimal? target = goal.TargetAmount;
        if (emergency)
        {
            if (sources.Count == 0)
                flags.Add(GoalFlags.NoCommitments);
            else if (sources.Any(s => s.View.MissingCurrency is not null) && !flags.Contains(GoalFlags.MissingCurrency))
                flags.Add(GoalFlags.MissingCurrency);
            target = sources.Count == 0
                ? null
                : Math.Round(GoalMath.EmergencyTarget(goal.Months ?? GoalCheck.DefaultMonths, sources.Sum(s => s.Monthly)), 2, MidpointRounding.AwayFromZero);
        }

        var progress = entries.Select(e => new ProgressEntry(e.On, e.Amount)).ToList();
        var funded = links.Count > 0;
        var reached = funded ? GoalMath.ReachedFromEnvelopes(inputs) : GoalMath.ReachedFromEntries(progress);
        var pace = funded ? GoalMath.PaceFromEnvelopes(inputs) : GoalMath.PaceFromEntries(progress, today);
        var figures = GoalMath.Evaluate(target ?? 0m, reached, pace, goal.TargetDate, today);

        GoalMoneyView? Money(decimal? amount) => amount is { } a ? GoalMoneyView.Of(a, reference) : null;
        var summary = new GoalSummary(
            goal.ItemId.Value, item.DisplayName, goal.Kind, item.OwnerViews(), item.Visibility == Visibility.Private ? "private" : "shared",
            item.ArchivedAt is not null, goal.TargetDate,
            figures is null ? null : emergency ? Money(figures.Target) : GoalMoneyView.Exact(figures.Target, reference),
            Money(figures?.Reached), figures?.ProgressPct, Money(figures?.Remaining), Money(figures?.MonthlyNeeded), Money(figures?.MonthlyPace),
            figures?.ProjectedOn, figures?.Status, partial, flags);
        return new GoalDetail(
            summary, goal.Months, goal.Note,
            lines.OrderBy(l => l.Name, French).ThenBy(l => l.EnvelopeId).ToList(),
            funded ? [] : progress.OrderBy(e => e.On).Select(e => new GoalEntryView(e.On, GoalMoneyView.Exact(e.Amount, reference))).ToList(),
            emergency ? sources.Select(s => s.View).ToList() : [], goal.Version);
    }

    /// <summary>D5: each source counting at least one item, its amortized month converted; a source with a missing rate is left out.</summary>
    private static IReadOnlyList<(GoalCommitmentView View, decimal Monthly)> Sources(
        IReadOnlyList<CommitmentsPart> parts, Currency reference, RateTable rates, LocalDate today)
        => parts.GroupBy(p => p.Source)
            .Where(g => g.Sum(p => p.Count) > 0)
            .Select(g =>
            {
                var total = 0m;
                string? missing = null;
                // Ordered, so the currency a miss names is stable.
                foreach (var sum in g.SelectMany(p => p.Amortized).GroupBy(a => a.Key, a => a.Value).OrderBy(a => a.Key.Code, StringComparer.Ordinal))
                {
                    var converted = Convert(sum.Sum(), sum.Key, reference, rates, today);
                    if (converted.Missing is not null) { missing = converted.Missing; break; }
                    total += converted.Amount!.Value;
                }
                return (new GoalCommitmentView(g.Key, g.Sum(p => p.Count), missing is null ? GoalMoneyView.Of(total, reference) : null, missing),
                    missing is null ? total : 0m);
            })
            .ToList();

    /// <summary>As <c>DashboardMath.Convert</c>: null without a value, the currency's code when its rate is missing.</summary>
    private static (decimal? Amount, string? Missing) Convert(Money? value, Currency reference, RateTable rates, LocalDate today)
        => value is { } v ? Convert(v.Amount, v.Currency, reference, rates, today) : (null, null);

    private static (decimal? Amount, string? Missing) Convert(decimal amount, Currency currency, Currency reference, RateTable rates, LocalDate today)
    {
        if (amount == 0m || currency == reference) return (amount, null);
        return rates.TryConvert(amount, currency, reference, today, projected: false, out var converted, out _)
            ? (converted, null)
            : (null, currency.Code);
    }
}
