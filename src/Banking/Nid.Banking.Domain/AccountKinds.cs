using Nid.Banking.Contracts;

namespace Nid.Banking.Domain;

public static class AccountKinds
{
    /// <summary>The ISO 20022 cash account type a bank gives; a loan or anything unknown is <see cref="BankAccountKind.Other"/>.</summary>
    public static BankAccountKind From(string cashAccountType) => cashAccountType switch
    {
        "CACC" => BankAccountKind.Checking,
        "SVGS" => BankAccountKind.Savings,
        "CARD" => BankAccountKind.Card,
        _ => BankAccountKind.Other,
    };
}
