using Nid.Core.Contracts;
using Nid.Income.Domain;
using NodaTime;
using UnambitiousFx.Functional;

namespace Nid.Income.Application;

/// <summary>Issue #12: the yearly review's income, from the same entries as the income overview, every visible item in full.</summary>
public sealed class IncomeReview(GetIncomeOverviewHandler overview, IHouseholdCalendar calendar) : IIncomeReview
{
    public async Task<Result<IncomeReviewPart>> ReviewAsync(
        PersonId reader, VisibilityScope scope, LocalDate start, LocalDate end, CancellationToken ct)
    {
        var from = start.PlusDays(1);
        var loaded = await overview.EntriesAsync(reader, scope, null, null, null, from, end, await calendar.TodayAsync(ct), ct);
        if (loaded.TryGetFailure(out var failure)) return Result.Failure<IncomeReviewPart>(failure!);
        loaded.TryGetValue(out var read);
        var rows = OverviewMath.BySource(read!.Entries.Select(e => e.Input).ToList(), from, end);
        decimal? total = rows.Any(r => r.Amount is null) ? null : rows.Sum(r => r.Amount!.Value);
        return Result.Success(new IncomeReviewPart(total,
            rows.Select(r => r.Item is { } id
                ? new IncomeReviewSource(id, id.Value.ToString(), r.Name, r.Kind, r.Amount)
                : new IncomeReviewSource(null, r.Kind, null, null, r.Amount)).ToList(),
            // Missing also lists rates read outside the period (today's monthly amounts): only a period without a total names them.
            total is null ? read.Missing : []));
    }
}
