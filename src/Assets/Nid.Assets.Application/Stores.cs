using Nid.Assets.Domain;
using Nid.Core.Contracts;

namespace Nid.Assets.Application;

public sealed record AssetRecord(ItemId ItemId, AssetDetails Details, uint Version);

/// <summary>One page of the candidates' assets; <paramref name="Kinds"/> are the kinds present among all candidates, before the kind filter.</summary>
public sealed record AssetPage(IReadOnlyList<AssetRecord> Rows, int Total, IReadOnlyList<string> Kinds);

/// <summary>An asset imported under a file id: its item, the hash of the entity that wrote it and who imported it.</summary>
public sealed record ImportedAsset(ItemId Item, string Hash, PersonId ImportedBy);

public interface IAssetRecordStore
{
    Task AddAsync(ItemId id, AssetDetails details, CancellationToken ct, ImportMark? import = null);

    /// <summary>Every importer's assets under these file ids.</summary>
    Task<IReadOnlyDictionary<string, IReadOnlyList<ImportedAsset>>> FindImportedAsync(IReadOnlyCollection<string> refs, CancellationToken ct);

    /// <summary>The import marks of the assets among <paramref name="ids"/> that were imported, by item id.</summary>
    Task<IReadOnlyDictionary<Guid, ImportMark>> MarksAsync(IReadOnlyCollection<ItemId> ids, CancellationToken ct);

    /// <summary>An import replaces every field and re-marks the row; no version is read first (the import's own transaction holds the lock).</summary>
    Task ReplaceAsync(ItemId id, AssetDetails details, ImportMark import, CancellationToken ct);

    /// <summary>Locks the asset row for the caller's transaction: writes that count an asset's valuations take it first, so none slips in between.</summary>
    Task LockAsync(ItemId id, CancellationToken ct);

    Task<AssetRecord?> FindAsync(ItemId id, CancellationToken ct);

    Task<IReadOnlyList<AssetRecord>> FindManyAsync(IReadOnlyCollection<ItemId> ids, CancellationToken ct);

    /// <summary>Replaces every field; false when the asset changed since <paramref name="version"/> was read (or is gone).</summary>
    Task<bool> UpdateAsync(ItemId id, AssetDetails details, uint version, CancellationToken ct);

    /// <summary>Filters by kind and pages in SQL over the candidate ids (name, owners and archive state were narrowed in Core). Newest purchase first.</summary>
    Task<AssetPage> ListAsync(IReadOnlyCollection<ItemId> candidates, string? kind, int skip, int take, CancellationToken ct);
}
