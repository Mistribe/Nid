using Nid.Assets.Contracts;
using Nid.Core.Contracts;

namespace Nid.Assets.Application;

/// <summary>
/// An asset's flows are its running costs (#649): an expense of <see cref="AssetCostCategories"/>, a category required. That only a
/// vehicle takes them is the cost commands' check (a convention knows the kind of item, not the kind of asset).
/// </summary>
public sealed class AssetsFactConventions : IFactConventions
{
    public IReadOnlyList<FactConvention> Conventions { get; } =
    [
        new("assets", "asset", "assets", AssetsCodes.InvalidCategory, CategoryRequired: true,
            new Dictionary<FlowKind, IReadOnlyList<string>> { [FlowKind.Expense] = AssetCostCategories.All }),
    ];
}
