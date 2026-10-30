using Nid.Banking.Contracts;
using Nid.Banking.Domain;

namespace Nid.Banking.Tests;

public class AccountKindsTests
{
    [Theory]
    [InlineData("CACC", BankAccountKind.Checking)]
    [InlineData("SVGS", BankAccountKind.Savings)]
    [InlineData("CARD", BankAccountKind.Card)]
    [InlineData("LOAN", BankAccountKind.Other)]
    [InlineData("OTHR", BankAccountKind.Other)]
    [InlineData("XYZ", BankAccountKind.Other)]
    [InlineData("", BankAccountKind.Other)]
    public void Maps_the_cash_account_type(string cashAccountType, BankAccountKind expected)
        => Assert.Equal(expected, AccountKinds.From(cashAccountType));
}
