using Nid.Core.Contracts;
using Nid.Income.Domain;
using NodaTime;
using UnambitiousFx.Functional;

namespace Nid.Income.Application;

/// <summary>Issue #12: the yearly review's income, from the same entries as the income overview, every visible item in full.</summary>
public sealed class IncomeReview(IncomeOverviewEntries overviewEntries, IHouseholdCalendar calendar) : IIncomeReview
{
    public async Task<Result<IncomeReviewPart>> ReviewAsync(
        PersonId reader, VisibilityScope scope, LocalDate start, LocalDate end, CancellationToken ct)
    {
        var from = start.PlusDays(1);
        var loaded = await overviewEntries.ReadAsync(reader, scope, null, null, null, from, end, await calendar.TodayAsync(ct), ct);
        if (loaded.TryGetFailure(out var failure)) return Result.Failure<IncomeReviewPart>(failure!);
        loaded.TryGetValue(out var read);
        var rows = OverviewMath.BySource(read!.Entries.Select(e => e.Input).ToList(), from, end);
        // The total as the overview counts it; each row to the cent, as the engagements: the rows may miss the total by a cent.
        decimal? total = rows.Any(r => r.Amount is null) ? null : rows.Sum(r => r.Amount!.Value);
        static decimal? Cents(decimal? amount) => amount is { } a ? Math.Round(a, 2, MidpointRounding.AwayFromZero) : null;
        // Only the dues of the period: the entries also convert today's monthly amounts, which the review does not show.
        var missing = read.Entries
            .Where(e => e.Input.Recurring.Concat(e.Input.OneOffs).Any(d => d.On >= from && d.On <= end && d.Amount is null))
            .Select(e => e.Currency).Distinct().Order(StringComparer.Ordinal).ToList();
        return Result.Success(new IncomeReviewPart(total,
            rows.Select(r => r.Item is { } id
                ? new IncomeReviewSource(id, id.Value.ToString(), r.Name, r.Kind, Cents(r.Amount))
                : new IncomeReviewSource(null, r.Kind, null, null, Cents(r.Amount))).ToList(),
            missing));
    }

    public async Task<LocalDate?> FirstDueAsync(PersonId reader, VisibilityScope scope, CancellationToken ct)
    {
        var today = await calendar.TodayAsync(ct);
        return await overviewEntries.FirstDueAsync(reader, scope, new LocalDate(today.Year - IIncomeReview.MaxYears, 1, 1), today, ct);
    }
}
