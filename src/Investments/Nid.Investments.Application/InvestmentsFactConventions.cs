using Nid.Core.Contracts;
using Nid.Investments.Contracts;

namespace Nid.Investments.Application;

/// <summary>
/// Passive-income spec C2: an envelope's income category (dividend, interest, distribution) sits on a withdrawal or an
/// income only, an employer's share (#183) on a contribution only; every other category stays free. "Not on a savings account"
/// and "only on an employee plan" are not expressed here.
/// </summary>
public sealed class InvestmentsFactConventions : IFactConventions
{
    public IReadOnlyList<FactConvention> Conventions { get; } =
    [
        new("investments", "envelope", "envelopes", InvestmentsCodes.InvalidIncomeCategory, CategoryRequired: false,
            new Dictionary<FlowKind, IReadOnlyList<string>> { [FlowKind.Withdrawal] = FlowCategories.Income, [FlowKind.Income] = FlowCategories.Income,
                [FlowKind.Contribution] = FlowCategories.Employer },
            OpenCategories: true),
    ];
}
