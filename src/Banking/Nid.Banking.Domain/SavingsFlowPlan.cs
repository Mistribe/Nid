using Nid.Core.Contracts;
using NodaTime;

namespace Nid.Banking.Domain;

/// <summary>A bank line of a savings account: <see cref="Amount"/> signed, <see cref="Interest"/> the part of it that is interest.</summary>
public sealed record SavingsLine(Guid Id, LocalDate BookedOn, decimal Amount, string Currency, string Label, decimal Interest);

/// <summary>An active flow of the envelope not recorded from the bank: <see cref="Amount"/> positive, the direction is in <see cref="Kind"/>.</summary>
public sealed record TypedFlow(Guid Id, LocalDate OccurredOn, FlowKind Kind, decimal Amount);

/// <summary>A flow the envelope should hold, identified by its <see cref="Ref"/>.</summary>
public sealed record WantedFlow(string Ref, LocalDate OccurredOn, FlowKind Kind, decimal Amount, string? Category, string Note);

/// <summary>Which flows the bank lines of a savings account give the account's envelope.</summary>
public static class SavingsFlowPlan
{
    public const string RefPrefix = "bank:";

    // Banking.Domain does not reference Core.Domain: this mirrors Nid.Core.Domain.FactRules.NoteMax.
    private const int NoteMax = 2000;

    // Copied from FlowCategories.Interest: Banking does not reference the Investments contracts.
    private const string InterestCategory = "interest";

    public static string MovementRef(Guid line) => $"{RefPrefix}{line:D}";

    public static string InterestRef(Guid line) => $"{MovementRef(line)}:interest";

    private static int Gap(LocalDate a, LocalDate b) => Math.Abs(Period.Between(a, b, PeriodUnits.Days).Days);

    private static decimal Movement(SavingsLine line) => Math.Round(line.Amount - line.Interest, 2);

    public static IReadOnlyList<WantedFlow> Plan(IReadOnlyList<SavingsLine> lines, IReadOnlyList<TypedFlow> typed, LocalDate associatedOn, string envelopeCurrency)
    {
        var since = associatedOn.With(DateAdjusters.StartOfMonth);
        var counted = lines
            .Where(l => l.BookedOn >= since && string.Equals(l.Currency, envelopeCurrency, StringComparison.Ordinal))
            .OrderBy(l => l.BookedOn).ThenBy(l => l.Id)
            .ToList();

        // A typed flow the household already entered stands for the bank line of the same movement: pair them, nearest first.
        var candidates = (
            from t in typed
            where t.Kind is FlowKind.Contribution or FlowKind.Withdrawal
            from l in counted
            where Movement(l) != 0
                && (Movement(l) > 0 ? FlowKind.Contribution : FlowKind.Withdrawal) == t.Kind
                && Math.Abs(Movement(l)) == Math.Round(t.Amount, 2)
                && Gap(t.OccurredOn, l.BookedOn) <= InternalTransfers.Days
            select (Typed: t, Line: l, Days: Gap(t.OccurredOn, l.BookedOn)))
            .OrderBy(p => p.Days) // stable: ties keep the line booking order
            .ToList();

        var usedTyped = new HashSet<Guid>();
        var absorbed = new HashSet<Guid>();
        foreach (var (t, l, _) in candidates)
            if (!usedTyped.Contains(t.Id) && !absorbed.Contains(l.Id))
            {
                usedTyped.Add(t.Id);
                absorbed.Add(l.Id);
            }

        var wanted = new List<WantedFlow>();
        foreach (var l in counted)
        {
            var note = l.Label.Length > NoteMax ? l.Label[..NoteMax] : l.Label;
            var movement = Movement(l);
            if (movement != 0 && !absorbed.Contains(l.Id))
                wanted.Add(new(MovementRef(l.Id), l.BookedOn, movement > 0 ? FlowKind.Contribution : FlowKind.Withdrawal, Math.Abs(movement), null, note));
            if (l.Interest != 0)
                wanted.Add(new(InterestRef(l.Id), l.BookedOn, FlowKind.Income, l.Interest, InterestCategory, note));
        }
        return wanted;
    }
}
