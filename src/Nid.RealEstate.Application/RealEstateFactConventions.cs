using Nid.Core.Contracts;
using Nid.RealEstate.Contracts;

namespace Nid.RealEstate.Application;

/// <summary>
/// 3F1 spec §3: a property's flows are an expense or an income of README §4's categories, a category required — what the
/// property's screens decide (<c>PropertyRules.Category</c>, which keeps its own check).
/// </summary>
public sealed class RealEstateFactConventions : IFactConventions
{
    public IReadOnlyList<FactConvention> Conventions { get; } =
    [
        new("realestate", "property", "properties", PropertyCodes.InvalidCategory, CategoryRequired: true,
            new Dictionary<FlowKind, IReadOnlyList<string>> { [FlowKind.Expense] = PropertyCategories.Expense, [FlowKind.Income] = PropertyCategories.Income }),
    ];
}
