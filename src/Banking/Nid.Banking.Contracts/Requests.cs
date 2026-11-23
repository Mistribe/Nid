using Nid.Core.Contracts;

namespace Nid.Banking.Contracts;

/// <summary>
/// #516: runs only with bank sync on for the household (D7): the banking behavior answers <see cref="BankingCodes.Disabled"/> before
/// the handler, after the item checks.
/// </summary>
public interface IRequiresBanking;

/// <summary>
/// #516: aimed at one bank account. The banking behavior answers <see cref="BankingCodes.AccountNotFound"/> for one the actor does not
/// read (its existence is not disclosed), forbidden for one they read when <see cref="Access"/> is <c>Write</c>, and hands the account
/// to the handler.
/// </summary>
public interface IBankAccountScoped : IRequiresBanking
{
    Guid BankAccountId { get; }

    AccessKind Access { get; }
}

/// <summary>#516: aimed at one bank line, which follows its account's rights: « not found » for one the actor does not read.</summary>
public interface IBankLineScoped : IRequiresBanking
{
    Guid TransactionId { get; }

    AccessKind Access { get; }
}
