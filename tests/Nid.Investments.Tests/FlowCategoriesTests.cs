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

    [Theory]
    [InlineData(EnvelopeKind.EmployeeSavings, FlowKind.Contribution, "employer_match", true)]
    [InlineData(EnvelopeKind.CollectivePer, FlowKind.Contribution, "profit_sharing", true)]
    [InlineData(EnvelopeKind.EmployeeSavings, FlowKind.Contribution, "participation", true)]
    [InlineData(EnvelopeKind.EmployeeSavings, FlowKind.Contribution, null, true)]
    [InlineData(EnvelopeKind.EmployeeSavings, FlowKind.Withdrawal, "employer_match", false)]
    [InlineData(EnvelopeKind.CollectivePer, FlowKind.Opening, "participation", false)]
    [InlineData(EnvelopeKind.Pea, FlowKind.Contribution, "employer_match", false)]
    [InlineData(EnvelopeKind.Per, FlowKind.Contribution, "profit_sharing", false)]
    public void An_employer_share_is_allowed_on_an_employee_plan_contribution_only(
        EnvelopeKind envelope, FlowKind kind, string? category, bool allowed)
        => Assert.Equal(allowed, FlowCategories.Refusal(envelope, kind, category) is null);
}
