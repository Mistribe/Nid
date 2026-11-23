using Nid.Childcare.Domain;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using NodaTime;
using UnambitiousFx.Functional;

namespace Nid.Childcare.Application;

/// <summary>
/// Childcare decision 18: for each of the reader's own arrangements, the last month gone by (the current one from its last
/// day) while it is not validated. Older months stay in the Relevés tab. And, for an arrangement not ended, the terms in force under a
/// legal minimum in force today (<see cref="LegalMinimums"/>), keyed by the day that minimum took effect: each revaluation notifies once. No amount travels.
/// </summary>
public sealed class ChildcareNotificationSource(IVisibleItems visible, IChildcareStore store) : INotificationSource
{
    public const string StatementDue = "childcare_statement_due";
    public const string BelowMinimum = "childcare_below_minimum";

    public string Name => "childcare";

    public IReadOnlyList<NotificationKind> Kinds { get; } = [
        new(StatementDue, "due_date", NotificationLevel.Action, "childcare", 0)
        {
            Template = new("Un relevé de garde est à valider")
            {
                Suffix = f => f.KeyDate("yyyy-MM") is { } month ? $" — relevé de {month.ToString("MMMM yyyy", NotificationFacts.French)}" : f.OnDueDate,
                Link = Page,
            },
        },
        new(BelowMinimum, "finding", NotificationLevel.Action, "childcare", null)
        {
            Template = new("Un contrat de garde est sous un minimum légal")
            {
                Suffix = f => (f.DueOn ?? f.KeyDate("yyyy-MM-dd")) is { } on ? $" — minimum en vigueur depuis le {NotificationFacts.Day(on)}" : "",
                Link = Page,
            },
        },
    ];

    /// <summary>The arrangement, or its statement of the month in <c>Params["month"]</c>.</summary>
    private static string Page(NotificationFacts f)
        => f.Item is not null && f.Params.TryGetValue("month", out var month) ? $"{f.ItemPage("childcare")}/statements/{month}" : f.ItemPage("childcare");

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
            if (revisions.Count == 0)
                continue;
            if (revisions[^1].To is not { } end || end >= today)
            {
                var inForce = revisions.LastOrDefault(r => r.From <= today) ?? revisions[0];
                if (LegalMinimums.Check(inForce.NetHourlyRate, inForce.GrossHourlyRate, inForce.UpkeepPer9h, today) is [_, ..] breaches)
                {
                    var since = breaches.Max(b => b.Since);
                    signals.Add(new Signal(SignalKeys.Dated(BelowMinimum, arrangement.ItemId, since), BelowMinimum, [arrangement.ItemId], since,
                        new Dictionary<string, string>()));
                }
            }
            if (!StatementContext.Months(revisions[0].From, revisions[^1].To, today).Contains(due))
                continue;
            if ((await store.StatementsOfAsync(arrangement.ItemId, ct)).Any(s => s.Month == due && s.Validated))
                continue;
            signals.Add(new Signal(SignalKeys.Monthly(StatementDue, arrangement.ItemId, lastDay), StatementDue, [arrangement.ItemId], lastDay,
                new Dictionary<string, string> { ["month"] = StatementAssembly.Format(due) }));
        }
        return Result.Success<IReadOnlyList<Signal>>(signals);
    }
}
