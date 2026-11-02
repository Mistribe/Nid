using Nid.Core.Contracts;
using Nid.Investments.Contracts;

namespace Nid.Investments.Tests;

public class FlowCategoriesTests
{
    [Theory]
    [InlineData(EnvelopeKind.Pea, FlowKind.Withdrawal, "dividend", true)]
    [InlineData(EnvelopeKind.Brokerage, FlowKind.Income, "interest", true)]
    [InlineData(EnvelopeKind.Pea, FlowKind.Contribution, "dividend", false)]
    [InlineData(EnvelopeKind.Pea, FlowKind.Opening, "interest", false)]
    [InlineData(EnvelopeKind.Pea, FlowKind.Expense, "distribution", false)]
    [InlineData(EnvelopeKind.Savings, FlowKind.Withdrawal, "interest", false)]
    [InlineData(EnvelopeKind.Savings, FlowKind.Income, "dividend", false)]
    [InlineData(EnvelopeKind.Pea, FlowKind.Contribution, "import", true)]
    [InlineData(EnvelopeKind.Savings, FlowKind.Withdrawal, "transfer", true)]
    [InlineData(EnvelopeKind.Pea, FlowKind.Withdrawal, null, true)]
    public void An_income_category_is_allowed_on_a_withdrawal_or_income_off_a_savings_account(
        EnvelopeKind envelope, FlowKind kind, string? category, bool allowed)
        => Assert.Equal(allowed, FlowCategories.Refusal(envelope, kind, category) is null);
}
