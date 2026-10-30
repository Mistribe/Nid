using NodaTime;
using UnambitiousFx.Functional;

namespace Nid.Core.Contracts;

public enum ChargeKind { Subscription, LoanInstalment, PropertyCost, Rent }

/// <summary>
/// 5C spec §6.1: a debit (a credit for a rent) a module expects on an item. <c>Key</c> is stable and unique per item and kind;
/// <c>LabelHints</c> are the words a bank line of it may carry.
/// </summary>
public sealed record ExpectedCharge(
    string Module, ItemId Item, string ItemName, ChargeKind Kind, string Key, LocalDate DueOn,
    Money Amount, bool Credit, int ToleranceDays, IReadOnlyList<string> LabelHints);

public interface IExpectedCharges
{
    /// <summary>Charges due in [<paramref name="from"/>, <paramref name="to"/>] on the items the reader reads (<see cref="VisibilityScope.Me"/>), past and projected.</summary>
    Task<Result<IReadOnlyList<ExpectedCharge>>> ChargesAsync(PersonId reader, LocalDate from, LocalDate to, CancellationToken ct);
}
