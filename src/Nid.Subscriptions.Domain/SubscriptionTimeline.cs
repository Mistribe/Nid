using NodaTime;
using Nid.Core.Contracts;
using Nid.Subscriptions.Contracts;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;

namespace Nid.Subscriptions.Domain;

/// <summary>
/// Builds the coverage periods and charges of one subscription
/// (<c>subscription-data-model.md</c> §"Timeline algorithm"). Pure: no clock, no I/O.
/// </summary>
public static class SubscriptionTimeline
{
    /// <summary>Guard against a malformed phase producing an unbounded walk: 100 years of monthly renewals.</summary>
    public const int MaxCyclesPerPhase = 1200;

    public static Result<Timeline> Build(SubscriptionInput input, LocalDate today, LocalDate horizon)
    {
        var validation = InputValidation.Validate(input);
        if (!validation.TryGetValue(out var validationWarnings))
            return validation.TryGetFailure(out var failure)
                ? Result.Failure<Timeline>(failure!)
                : Result.Failure<Timeline>(
                    new Failure("subscriptions.unknown", "The input was refused.", null));

        var warnings = new List<Finding>(validationWarnings!);
        var periods = new List<CoveragePeriod>();
        var charges = new List<Charge>();

        // Grants of this subscription, in purchase-date order; ties broken by id then index so the walk is stable.
        var grants = input.Purchases
            .SelectMany(p => p.Grants.Select((g, index) => (Purchase: p, Grant: g, Index: index)))
            .Where(x => x.Grant.SubscriptionId == input.Subscription.Id)
            .OrderBy(x => x.Purchase.Date)
            .ThenBy(x => x.Purchase.Id.Value)
            .ThenBy(x => x.Index)
            .ToList();

        var states = input.Phases
            .OrderBy(p => p.StartsOn)
            .Select(p => new PhaseState(p))
            .ToList();

        LocalDate? coverageEnd = null;
        var next = 0;

        while (true)
        {
            var candidate = NextCharge(input, states, coverageEnd, horizon);
            var grantDate = next < grants.Count ? grants[next].Purchase.Date : (LocalDate?)null;

            // A grant is applied before a charge falling on the same day: it may push that charge back.
            var takeGrant = grantDate is { } g && (candidate is not { } c || g <= c.Date);

            if (takeGrant)
            {
                var (purchase, grant, index) = grants[next++];
                var start = grant.StartsOn ?? Later(purchase.Date, coverageEnd);
                var end = grant.Duration.AddTo(start, 1);

                if (grant.StartsOn is not null && periods.Any(p => start < p.EndBefore && p.Start < end))
                    warnings.Add(new Finding(
                        "subscriptions.grant_overlap",
                        "A grant with an explicit start overlaps existing coverage; it may be a duplicate entry.",
                        purchase.Id.ToString()));

                var period = new CoveragePeriod(start, end, grant.Source,
                                                new CoverageOrigin.FromGrant(purchase.Id, index),
                                                IsProjected: purchase.Date > today);
                periods.Add(period);
                coverageEnd = Later(end, coverageEnd);

                // A purchase becomes a charge once, with the first of its grants.
                if (index == 0)
                    charges.Add(new Charge(purchase.Date, purchase.Price, purchase.Payer,
                                           new ChargeOrigin.FromPurchase(purchase.Id),
                                           CoverageOf(purchase, periods),
                                           IsProjected: purchase.Date > today,
                                           Allocation: ZeroAllocation(purchase.Price)));
                continue;
            }

            if (candidate is not { } charge)
                break;

            var state = charge.State;
            if (state.Cycles >= MaxCyclesPerPhase)
            {
                warnings.Add(new Finding("subscriptions.cycle_cap",
                                         "A phase reached the renewal cap; the timeline stops there.",
                                         state.Phase.Id.ToString()));
                break;
            }

            // Re-anchor when a stacked grant pushed this charge past its natural occurrence, so that
            // end-of-month clamping restarts from the date money actually moves.
            if (charge.Date != charge.Natural)
            {
                state.Anchor = charge.Date;
                state.Cycles = 0;
            }

            var coverStart = charge.Date;
            var coverEnd = state.Phase.CoverageEvery.AddTo(state.Anchor, state.Cycles + 1);
            if (coverEnd <= coverStart)
                coverEnd = state.Phase.CoverageEvery.AddTo(coverStart, 1);

            var covered = new CoveragePeriod(coverStart, coverEnd, CoverageSource.Paid,
                                             new CoverageOrigin.FromPhase(state.Phase.Id, state.Cycles),
                                             IsProjected: charge.Date > today);
            periods.Add(covered);
            charges.Add(new Charge(charge.Date, state.Phase.Price, state.Phase.Payer,
                                   new ChargeOrigin.FromPhase(state.Phase.Id, state.Cycles),
                                   [covered],
                                   IsProjected: charge.Date > today,
                                   Allocation: ZeroAllocation(state.Phase.Price)));

            state.Cycles++;
            coverageEnd = Later(coverEnd, coverageEnd);
        }

        periods.Sort((a, b) => a.Start.CompareTo(b.Start));
        charges.Sort((a, b) => a.Date.CompareTo(b.Date));
        return Result.Success(new Timeline(periods, charges, warnings));
    }

    private static Candidate? NextCharge(
        SubscriptionInput input,
        List<PhaseState> states,
        LocalDate? coverageEnd,
        LocalDate horizon)
    {
        if (!input.Subscription.AutoRenew)
            return null;

        Candidate? best = null;

        foreach (var state in states)
        {
            var phase = state.Phase;
            if (phase.MaxCycles is { } max && state.Cycles >= max)
                continue;

            var natural = state.Cycles == 0
                ? phase.StartsOn
                : phase.Every.AddTo(state.Anchor, state.Cycles);

            // Coverage must have run out before the phase charges again.
            var date = coverageEnd is { } end && end > natural ? end : natural;
            if (date < phase.StartsOn)
                date = phase.StartsOn;

            if (phase.EndsOn is { } endsOn && date >= endsOn)
                continue;

            if (input.Subscription.CancelledAt is { } cancelled && date >= cancelled)
                continue;

            if (date >= horizon)
                continue;

            if (best is null || date < best.Date)
                best = new Candidate(state, date, natural);
        }

        return best;
    }

    private static IReadOnlyList<CoveragePeriod> CoverageOf(Purchase purchase, List<CoveragePeriod> periods)
        => periods
            .Where(p => p.Origin is CoverageOrigin.FromGrant grant && grant.PurchaseId == purchase.Id)
            .OrderBy(p => p.Start)
            .ToList();

    private static CostAllocation ZeroAllocation(Money price) => new(price, 0, 0m, 0);

    private static LocalDate Later(LocalDate date, LocalDate? other)
        => other is { } value && value > date ? value : date;

    private sealed class PhaseState(BillingPhase phase)
    {
        public BillingPhase Phase { get; } = phase;

        public LocalDate Anchor { get; set; } = phase.StartsOn;

        public int Cycles { get; set; }
    }

    private sealed record Candidate(PhaseState State, LocalDate Date, LocalDate Natural);
}
