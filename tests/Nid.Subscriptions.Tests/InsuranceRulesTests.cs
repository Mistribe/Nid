using Nid.Subscriptions.Contracts;
using Nid.Subscriptions.Domain;

namespace Nid.Subscriptions.Tests;

public class InsuranceRulesTests
{
    private static InsuranceDetails With(string? policy = null, params InsuranceGuarantee[] guarantees)
        => new(InsuranceKind.Home, null, null, policy, null, guarantees);

    [Fact]
    public void A_policy_within_the_limits_is_accepted()
        => Assert.Null(InsuranceRules.Validate(With("POL", new InsuranceGuarantee("Fire", new string('x', 200), null))));

    [Fact]
    public void A_text_over_200_characters_is_refused_at_its_member()
    {
        var policy = InsuranceRules.Validate(With(new string('x', 201)))!;
        Assert.Equal(("subscriptions.insurance_text_length", InsuranceField.PolicyNumber, null), (policy.Code, policy.Field, policy.Index));

        var deductible = InsuranceRules.Validate(With(null,
            new InsuranceGuarantee("Fire", null, null), new InsuranceGuarantee("Theft", null, new string('x', 201))))!;
        Assert.Equal(("subscriptions.insurance_text_length", InsuranceField.GuaranteeDeductible, 1), (deductible.Code, deductible.Field, deductible.Index));
    }

    [Fact]
    public void A_guarantee_needs_a_label_and_the_issue_names_its_position()
    {
        var issue = InsuranceRules.Validate(With(null, new InsuranceGuarantee("Fire", null, null), new InsuranceGuarantee(" ", null, null)))!;
        Assert.Equal(("subscriptions.guarantee_label", InsuranceField.GuaranteeLabel, 1), (issue.Code, issue.Field, issue.Index));
    }

    [Fact]
    public void More_than_50_guarantees_are_refused()
    {
        var issue = InsuranceRules.Validate(With(null, Enumerable.Range(0, 51).Select(i => new InsuranceGuarantee($"G{i}", null, null)).ToArray()))!;
        Assert.Equal(("subscriptions.too_many_guarantees", InsuranceField.Guarantees), (issue.Code, issue.Field));
    }
}
