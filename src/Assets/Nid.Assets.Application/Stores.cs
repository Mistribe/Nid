using Nid.Assets.Domain;
using Nid.Core.Contracts;

namespace Nid.Assets.Application;

public sealed record AssetRecord(ItemId ItemId, AssetDetails Details, uint Version);

/// <summary>One page of the candidates' assets; <paramref name="Kinds"/> are the kinds present among all candidates, before the kind filter.</summary>
public sealed record AssetPage(IReadOnlyList<AssetRecord> Rows, int Total, IReadOnlyList<string> Kinds);

public interface IAssetRecordStore
{
    Task AddAsync(ItemId id, AssetDetails details, CancellationToken ct);

    Task<AssetRecord?> FindAsync(ItemId id, CancellationToken ct);

    Task<IReadOnlyList<AssetRecord>> FindManyAsync(IReadOnlyCollection<ItemId> ids, CancellationToken ct);

    /// <summary>Replaces every field; false when the asset changed since <paramref name="version"/> was read (or is gone).</summary>
    Task<bool> UpdateAsync(ItemId id, AssetDetails details, uint version, CancellationToken ct);

    /// <summary>Filters by kind and pages in SQL over the candidate ids (name, owners and archive state were narrowed in Core). Newest purchase first.</summary>
    Task<AssetPage> ListAsync(IReadOnlyCollection<ItemId> candidates, string? kind, int skip, int take, CancellationToken ct);
}
