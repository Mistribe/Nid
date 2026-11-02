using Nid.Core.Contracts;
using Nid.Investments.Contracts;

namespace Nid.Investments.Application;

/// <summary>
/// Passive-income spec C2: an envelope's income category (dividend, interest, distribution) sits on a withdrawal or an
/// income only; every other category stays free. "Not on a savings account" is not expressed here.
/// </summary>
public sealed class InvestmentsFactConventions : IFactConventions
{
    public IReadOnlyList<FactConvention> Conventions { get; } =
    [
        new("investments", "envelope", "envelopes", InvestmentsCodes.InvalidIncomeCategory, CategoryRequired: false,
            new Dictionary<FlowKind, IReadOnlyList<string>> { [FlowKind.Withdrawal] = FlowCategories.Income, [FlowKind.Income] = FlowCategories.Income },
            OpenCategories: true),
    ];
}
