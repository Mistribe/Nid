using NodaTime;
using UnambitiousFx.Functional;

namespace Nid.Core.Contracts;

public enum ChargeKind { Subscription, LoanInstalment, PropertyCost, Rent, Childcare }

/// <summary>
/// 5C spec §6.1: a debit (a credit for a rent) a module expects on an item. <c>Key</c> is stable and unique per item and kind;
/// <c>LabelHints</c> are the words a bank line of it may carry.
/// </summary>
public sealed record ExpectedCharge(
    string Module, ItemId Item, string ItemName, ChargeKind Kind, string Key, LocalDate DueOn,
    Money Amount, bool Credit, int ToleranceDays, IReadOnlyList<string> LabelHints);

public interface IExpectedCharges
{
    /// <summary>
    /// Charges due in [<paramref name="from"/>, <paramref name="to"/>] on the items the reader sees in <paramref name="scope"/>, past and
    /// projected: the bank reconciliation reads <see cref="VisibilityScope.Me"/>, the calendar's amounts (#241) the page's scope.
    /// </summary>
    Task<Result<IReadOnlyList<ExpectedCharge>>> ChargesAsync(PersonId reader, VisibilityScope scope, LocalDate from, LocalDate to, CancellationToken ct);
}

public static class ChargeKinds
{
    /// <summary>The page a charge belongs to, as the calendar's modules (<see cref="CalendarModules"/>) and the notification templates name it.</summary>
    public static string ModuleOf(ChargeKind kind) => kind switch
    {
        ChargeKind.Subscription => CalendarModules.Subscriptions,
        ChargeKind.LoanInstalment => CalendarModules.Loans,
        ChargeKind.PropertyCost or ChargeKind.Rent => CalendarModules.Properties,
        ChargeKind.Childcare => CalendarModules.Childcare,
        _ => throw new InvalidOperationException($"No page for the charge kind {kind}."),
    };

    /// <summary>A kind as the wire names it.</summary>
    public static string Wire(ChargeKind kind) => kind switch
    {
        ChargeKind.Subscription => "subscription",
        ChargeKind.LoanInstalment => "loan_instalment",
        ChargeKind.PropertyCost => "property_cost",
        ChargeKind.Rent => "rent",
        ChargeKind.Childcare => "childcare",
        _ => throw new InvalidOperationException($"No wire name for the charge kind {kind}."),
    };
}

/// <summary>Issue #81: when the reader's next income is expected, for the shortfall ahead.</summary>
public interface IExpectedIncome
{
    /// <summary>The earliest next payment date, after <paramref name="today"/>, of the active income sources the reader sees (<see cref="VisibilityScope.Me"/>); null when none pays.</summary>
    Task<LocalDate?> NextAsync(PersonId reader, LocalDate today, CancellationToken ct);
}
