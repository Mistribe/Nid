using Nid.Childcare.Domain;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using NodaTime;
using UnambitiousFx.Functional;

namespace Nid.Childcare.Application;

/// <summary>
/// Childcare decision 18: for each of the reader's own arrangements, the last month gone by (the current one from its last
/// day) while it is not validated. Older months stay in the Relevés tab. No amount travels.
/// </summary>
public sealed class ChildcareNotificationSource(IVisibleItems visible, IChildcareStore store) : INotificationSource
{
    public const string StatementDue = "childcare_statement_due";

    public string Name => "childcare";

    public IReadOnlyList<NotificationKind> Kinds { get; } = [new(StatementDue, "due_date", NotificationLevel.Action, "childcare", 0)];

    public async Task<Result<IReadOnlyList<Signal>>> SignalsAsync(PersonId reader, LocalDate today, LocalDate horizon, CancellationToken ct)
    {
        var due = today.PlusDays(1).Month != today.Month ? today.ToYearMonth() : today.ToYearMonth().PlusMonths(-1);
        var lastDay = due.OnDayOfMonth(due.Calendar.GetDaysInMonth(due.Year, due.Month));
        // Closed ones too: the month an arrangement ends in still has to be validated.
        var ids = (await visible.ForAsync(reader, VisibilityScope.Me, includeArchived: true, ct)).ToList();
        var found = await store.FindManyAsync(ids, ct);
        var terms = (await store.TermsOfAsync(found.Select(a => a.ItemId).ToList(), ct)).ToLookup(t => t.ItemId);
        var signals = new List<Signal>();
        foreach (var arrangement in found)
        {
            var revisions = terms[arrangement.ItemId].ToList();
            if (revisions.Count == 0 || !StatementContext.Months(revisions[0].From, revisions[^1].To, today).Contains(due))
                continue;
            if ((await store.StatementsOfAsync(arrangement.ItemId, ct)).Any(s => s.Month == due && s.Validated))
                continue;
            signals.Add(new Signal(SignalKeys.Monthly(StatementDue, arrangement.ItemId, lastDay), StatementDue, [arrangement.ItemId], lastDay,
                new Dictionary<string, string> { ["month"] = StatementAssembly.Format(due) }));
        }
        return Result.Success<IReadOnlyList<Signal>>(signals);
    }
}
