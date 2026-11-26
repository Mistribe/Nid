using NodaTime;
using UnambitiousFx.Functional;

namespace Nid.Core.Contracts;

/// <summary>
/// Issue #514: a kind of expected charge, declared by the module that expects it and carried by each of its charges: <c>Name</c> as the
/// wire names it, <c>Page</c> the calendar's module (<see cref="CalendarModules"/>) and the item page a charge opens.
/// </summary>
public sealed record ChargeKind(string Name, string Page);

/// <summary>
/// The names of the existing modules' charge kinds, which the bank reconciliation reads (a rent is a credit, a subscription's price may
/// change, each its category). A new module declares its own <see cref="ChargeKind"/>; its lines are linked, left uncategorised.
/// </summary>
public static class ChargeKinds
{
    public const string Subscription = "subscription";
    public const string LoanInstalment = "loan_instalment";
    public const string PropertyCost = "property_cost";
    public const string Rent = "rent";
    public const string Childcare = "childcare";
    public const string Income = "income";
}

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

/// <summary>
/// #714: money the calendar expects that the bank reconciliation does not link: an income source's payments, a childcare month not
/// validated yet. Registered under this interface only, so the readers of <see cref="IExpectedCharges"/> (the reconciliation) never see them.
/// </summary>
public interface IProjectedCharges : IExpectedCharges;
