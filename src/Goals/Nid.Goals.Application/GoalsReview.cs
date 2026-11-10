using System.Globalization;
using Nid.Core.Contracts;
using Nid.Goals.Contracts;
using Nid.Goals.Domain;
using Nid.Investments.Contracts;
using NodaTime;
using UnambitiousFx.Functional;

namespace Nid.Goals.Application;

/// <summary>
/// Issue #12: the yearly review's goals, each read on the period's start and its last counted day as <see cref="GoalReader"/> reads
/// it today: a free goal's latest entry, a funded goal's envelopes (the ones the reader sees, D1) with today's shares. The target is
/// today's (goals keep no target history).
/// </summary>
public sealed class GoalsReview(
    IVisibleItems visible, IItemRegistry items, IGoalStore goals, IEnvelopeFunding envelopes, GoalReader goalReader,
    IHouseholdCalendar calendar, IHouseholdSettings household, IFxRates fx) : IGoalsReview
{
    private static readonly StringComparer French = StringComparer.Create(new CultureInfo("fr-FR"), true);

    public async Task<Result<GoalsReviewPart>> ReviewAsync(PersonId reader, VisibilityScope scope, LocalDate start, LocalDate end, CancellationToken ct)
    {
        var zone = await calendar.ZoneAsync(ct);
        var found = await goals.FindManyAsync((await visible.ForAsync(reader, scope, includeArchived: true, ct)).ToList(), ct);
        var accesses = await items.FindManyAsync(found.Select(g => g.ItemId).ToList(), ct);
        var read = new List<(Goal Goal, ItemAccess Item, bool Started, LocalDate? ArchivedOn, LocalDate Last)>();
        foreach (var goal in found)
        {
            if (!accesses.TryGetValue(goal.ItemId, out var item)) continue;
            var created = item.CreatedAt.InZone(zone).Date;
            // An archived goal counts strictly before its archive day; archived on or before Start, it is left out.
            var archivedOn = item.ArchivedAt?.InZone(zone).Date is { } day && day <= end ? day : (LocalDate?)null;
            var last = archivedOn?.PlusDays(-1) ?? end;
            if (created > end || last < start) continue;
            read.Add((goal, item, created <= start, archivedOn, last));
        }

        var ids = read.Select(r => r.Goal.ItemId).ToList();
        var links = (ids.Count == 0 ? [] : await goals.FundingOfAsync(ids, ct)).ToLookup(f => f.Goal);
        var free = ids.Where(id => !links[id].Any()).ToList();
        var entries = (free.Count == 0 ? [] : await goals.EntriesOfAsync(free, ct)).ToLookup(e => e.Goal);

        // The funded goals' envelopes, read once per date they are needed on.
        var linked = links.SelectMany(l => l).Select(f => f.Envelope).Distinct().ToList();
        var dates = read.Where(r => links[r.Goal.ItemId].Any()).SelectMany(r => r.Started ? [start, r.Last] : new[] { r.Last }).Distinct();
        var seen = new Dictionary<LocalDate, Dictionary<ItemId, EnvelopeFundingFigures>>();
        foreach (var date in dates)
        {
            var figures = await envelopes.ReadAsync(reader, scope, linked, date, ct);
            if (!figures.TryGetValue(out var value))
                return Result.Failure<GoalsReviewPart>(figures.TryGetFailure(out var failure) ? failure! : throw new InvalidOperationException());
            seen[date] = value!.ToDictionary(f => f.Envelope);
        }

        var reference = await household.ReferenceCurrencyAsync(ct);
        var missing = new SortedSet<string>(StringComparer.Ordinal);
        IReadOnlyList<(GoalCommitmentView View, decimal Monthly)> sources = [];
        if (read.Any(r => r.Goal.Kind == GoalKinds.EmergencyFund))
        {
            var commitments = await goalReader.SourcesAsync(reader, scope, reference, await calendar.TodayAsync(ct), ct);
            if (!commitments.TryGetValue(out var value))
                return Result.Failure<GoalsReviewPart>(commitments.TryGetFailure(out var failure) ? failure! : throw new InvalidOperationException());
            sources = value!;
            missing.UnionWith(sources.Select(s => s.View.MissingCurrency).OfType<string>());
        }

        // Each date's values convert at that date's rate. The reference's own quote is needed too: rates are per USD.
        var foreign = seen.Values.SelectMany(d => d.Values).Select(f => f.Value?.Currency).OfType<Currency>().Where(c => c != reference).Distinct().ToList();
        var rates = foreign.Count == 0 ? RateTable.Empty(end, 0) : await fx.LoadAsync([.. foreign, reference], start, end, ct);

        decimal? Reached(Goal goal, LocalDate on)
        {
            var mine = links[goal.ItemId].ToList();
            if (mine.Count == 0)
                return GoalMath.ReachedFromEntries(entries[goal.ItemId].Where(e => e.On <= on).Select(e => new ProgressEntry(e.On, e.Amount)));
            var inputs = new List<FundingInput>();
            var unconverted = false;
            foreach (var link in mine)
            {
                if (!seen[on].TryGetValue(link.Envelope, out var envelope)) continue; // D1: an envelope the reader does not see
                var value = GoalReader.Convert(envelope.Value, reference, rates, on);
                if (value.Missing is { } code)
                {
                    missing.Add(code);
                    unconverted = true;
                }
                else
                    inputs.Add(new FundingInput(value.Amount, 0m, link.ShareBp, envelope.Closed));
            }
            return unconverted ? null : GoalMath.ReachedFromEnvelopes(inputs);
        }

        var rows = new List<GoalReviewRow>();
        foreach (var (goal, item, started, archivedOn, last) in read.OrderBy(r => r.Item.DisplayName, French).ThenBy(r => r.Item.Id.Value))
        {
            var target = GoalReader.TargetOf(goal, sources);
            var startReached = started ? Reached(goal, start) : null;
            var endReached = Reached(goal, last);
            GoalProgress? Progress(decimal? reached, LocalDate on) => reached is { } r ? GoalMath.Evaluate(target ?? 0m, r, null, null, on) : null;
            var a = Progress(startReached, start);
            var b = Progress(endReached, last);
            static decimal? R2(decimal? v) => v is { } x ? Math.Round(x, 2, MidpointRounding.AwayFromZero) : null;
            rows.Add(new GoalReviewRow(item.Id, item.DisplayName, goal.Kind, target, R2(startReached), R2(endReached), a?.ProgressPct, b?.ProgressPct,
                b?.Status == GoalStatuses.Reached && a?.Status != GoalStatuses.Reached, archivedOn));
        }
        return Result.Success(new GoalsReviewPart(rows, missing.ToList()));
    }
}
