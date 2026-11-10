using System.Globalization;
using Nid.Core.Contracts;
using Nid.Investments.Contracts;
using Nid.Investments.Domain;
using NodaTime;
using UnambitiousFx.Functional.Failures;

namespace Nid.Investments.Application;

/// <summary>Issue #399: a regulated livret's ceiling, the balance it is read against and what can still be paid in (0 once reached).</summary>
public sealed record CeilingView(AmountView Ceiling, AmountView Room, bool Reached, LocalDate Since, string Source);

/// <summary>Issue #401: the year's interest at the regulated rates, an estimate: never written as a value.</summary>
public sealed record InterestEstimateView(int Year, AmountView ToDate, AmountView Expected, decimal RatePct, string Source);

/// <summary>Issue #400: the envelope's next tax date (<c>life_insurance_8y</c>, <c>pea_5y</c>, <c>per_retirement</c>).</summary>
public sealed record TaxMilestoneView(string Code, LocalDate On);

/// <summary>Issues #399–#401 over one envelope's facts, shared by the detail, the overview, « À venir » and the routine.</summary>
internal static class EnvelopeInsights
{
    /// <summary>As « À venir », further ahead: a tax date is planned for.</summary>
    public const int MilestoneDays = 90;

    public static CeilingView? Ceiling(EnvelopePerformance.Facts f, LocalDate on)
        => Room(f, on) is var (rule, room)
            ? new CeilingView(Amount(rule.Value, f.Currency), Amount(room, f.Currency), room == 0m, rule.From, rule.Source)
            : null;

    /// <summary>The ceiling in force and what can still be paid in against the balance on the date, never below zero.</summary>
    public static (DatedRule Rule, decimal Room)? Room(EnvelopePerformance.Facts f, LocalDate on)
        => f.Envelope.Regulated is { } kind && RegulatedSavings.Ceiling(kind, on) is { } rule
            ? (rule, Math.Max(0m, rule.Value - (RegulatedSavings.BalanceAt(f.Values, f.Flows, on) ?? 0m)))
            : null;

    /// <summary>
    /// Issue #399: a livret takes no deposit beyond its ceiling (interest may have taken it over: then none at all), whichever path
    /// records it; a withdrawal is never refused.
    /// </summary>
    public static Failure? AboveCeiling(EnvelopePerformance.Facts f, LocalDate on, decimal paid, string field)
        => Room(f, on) is var (ceiling, room) && paid > room
            ? Routine.Field(InvestmentsCodes.AboveCeiling, string.Create(CultureInfo.InvariantCulture,
                $"This livret takes at most {room:0.00} {f.Currency.Code} more: its ceiling is {ceiling.Value:0}."), field)
            : null;

    public static InterestEstimateView? Interest(EnvelopePerformance.Facts f, LocalDate today)
        => f.Envelope.Regulated is { } kind && RegulatedSavings.Interest(kind, f.Values, f.Flows, today) is { } e
            ? new InterestEstimateView(e.Year, Amount(e.ToDate, f.Currency), Amount(e.Expected, f.Currency), e.RatePct, e.Source)
            : null;

    /// <summary>A PER's date is its main owner's retirement: their birthday at their retirement age plus the months, when the birth date is known.</summary>
    public static TaxMilestone? Milestone(EnvelopePerformance.Facts f, IReadOnlyList<PersonInfo> persons, LocalDate today)
    {
        var owner = f.Item.Owners.OrderByDescending(o => o.ShareBp).Select(o => persons.FirstOrDefault(p => p.Id == o.PersonId)).FirstOrDefault();
        var retirement = owner?.BirthDate is { } born ? born.PlusYears(owner.RetirementAge).PlusMonths(owner.RetirementMonths) : (LocalDate?)null;
        return TaxMilestones.Next(f.Envelope.Kind, f.Envelope.OpenedOn, f.Flows, retirement, today);
    }

    public static bool Soon(TaxMilestone m, LocalDate today) => m.On <= today.PlusDays(MilestoneDays);

    private static AmountView Amount(decimal amount, Currency currency) => AmountView.From(Money.Create(amount, currency).TryGetValue(out var m) ? m : throw new InvalidOperationException());
}
