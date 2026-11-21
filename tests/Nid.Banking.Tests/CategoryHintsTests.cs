using Nid.Banking.Domain;

namespace Nid.Banking.Tests;

public class CategoryHintsTests
{
    [Theory]
    [InlineData("PMNT/CCRD/CWDL", -20, "internal.cash")]
    [InlineData("ATM", -20, "internal.cash")]
    [InlineData("ACMT/MDOP/CHRG", -2, "bank")]
    [InlineData("PMNT/ICDT/CHRG", -2, "bank")]
    [InlineData("FEE", -2, "bank")]
    [InlineData("SRVCHG", -2, "bank")]
    [InlineData("PMNT/RCDT/SALA", 100, "income.salary")]
    [InlineData("PMNT/CCRD/CWDL/ATMW", -20, "internal.cash")]
    // Enable Banking sends the family and sub-family, without the domain.
    [InlineData("CCRD/CWDL", -20, "internal.cash")]
    [InlineData("MDOP/CHRG", -2, "bank")]
    [InlineData("ICDT/CHRG", -2, "bank")]
    [InlineData("RCDT/SALA", 100, "income.salary")]
    [InlineData("ACMT/MCOP/INTR", 5, "income.interest")]
    [InlineData("MCOP/INTR", 5, "income.interest")]
    [InlineData("INT", 5, "income.interest")]
    public void A_structured_code_gives_its_seed(string code, int amount, string key) => Assert.Equal(key, CategoryHints.KeyFor(code, amount));

    [Theory]
    [InlineData("PMNT/RCDT/SALA", -100)]
    [InlineData("PMNT/RCDT/SALA", 0)]
    [InlineData("PMNT/RCDT/ESCT", 100)]
    [InlineData("PMNT/CCRD/CWDLX", -20)]
    [InlineData("atm", -20)]
    [InlineData("DEBIT", -20)]
    [InlineData("Retrait DAB", -20)]
    [InlineData("PMNT/CCRD/CWDL extra", -20)]
    [InlineData("PMNT/CCRD/CWDL", 20)]
    [InlineData("ATM", 20)]
    [InlineData("ACMT/MDOP/CHRG", 2)]
    [InlineData("PMNT/ICDT/CHRG", 2)]
    [InlineData("FEE", 2)]
    [InlineData("SRVCHG", 2)]
    [InlineData("CCRD/CWDL", 20)]
    [InlineData("MDOP/CHRG", 2)]
    [InlineData("RCDT/SALA", -100)]
    [InlineData("RCDT/ESCT", 100)]
    [InlineData("ICDT/ESCT", -20)]
    [InlineData("CWDL", -20)]
    [InlineData("ACMT/MCOP/INTR", -5)]
    [InlineData("MCOP/INTR", -5)]
    [InlineData("INT", -5)]
    [InlineData("12/32", -20)]
    [InlineData("12/32", 20)]
    [InlineData("", -20)]
    [InlineData(null, -20)]
    public void Anything_else_gives_nothing(string? code, int amount) => Assert.Null(CategoryHints.KeyFor(code, amount));

    [Theory]
    [InlineData("INTERETS 2026", 5, true, "income.interest")]
    [InlineData("Intérêts créditeurs", 5, true, "income.interest")]
    [InlineData("Intérêts créditeurs", 5, false, null)]
    [InlineData("Intérêts créditeurs", -5, true, null)]
    [InlineData("VIREMENT", 5, true, null)]
    public void An_interest_label_counts_on_a_savings_account_only(string label, int amount, bool savings, string? key)
        => Assert.Equal(key, CategoryHints.KeyFor(null, amount, label, savings));
}
