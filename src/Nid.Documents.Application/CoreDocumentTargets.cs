using Nid.Core.Contracts;
using Nid.Documents.Contracts;

namespace Nid.Documents.Application;

/// <summary>Design §6: the three Core targets. An item anchors its own link; a person and the household anchor none.</summary>
public sealed class CoreDocumentTargets(IItemRegistry items, IPersonRegistry persons) : IDocumentTargets
{
    private const string HouseholdLabel = "Foyer";

    public IReadOnlySet<string> Kinds { get; } = new HashSet<string> { TargetKinds.CoreItem, TargetKinds.CorePerson, TargetKinds.CoreHousehold };

    public async Task<IReadOnlyDictionary<Guid, ResolvedTarget>> ResolveAsync(string kind, IReadOnlyCollection<Guid> ids, CancellationToken ct)
        => kind switch
        {
            TargetKinds.CoreItem => (await items.FindManyAsync(ids.Select(i => new ItemId(i)).ToList(), ct))
                .ToDictionary(p => p.Key.Value, p => new ResolvedTarget(p.Key.Value, p.Value.DisplayName)),
            TargetKinds.CorePerson => (await persons.ListAsync(ct)).Where(p => ids.Contains(p.Id.Value))
                .ToDictionary(p => p.Id.Value, p => new ResolvedTarget(null, p.DisplayName)),
            TargetKinds.CoreHousehold when ids.Contains(TargetKinds.Household)
                => new Dictionary<Guid, ResolvedTarget> { [TargetKinds.Household] = new(null, HouseholdLabel) },
            _ => new Dictionary<Guid, ResolvedTarget>(),
        };
}
