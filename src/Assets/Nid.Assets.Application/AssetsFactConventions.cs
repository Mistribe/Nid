using Nid.Assets.Contracts;
using Nid.Core.Contracts;

namespace Nid.Assets.Application;

/// <summary>An asset takes valuations only: no flow kind has categories here, so the file's flows on an asset are refused.</summary>
public sealed class AssetsFactConventions : IFactConventions
{
    public IReadOnlyList<FactConvention> Conventions { get; } =
    [
        new("assets", "asset", "assets", AssetsCodes.NoFlows, CategoryRequired: false, new Dictionary<FlowKind, IReadOnlyList<string>>()),
    ];
}
