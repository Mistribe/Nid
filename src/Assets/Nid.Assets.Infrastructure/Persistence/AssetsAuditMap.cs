using Nid.Platform.Infrastructure.Audit;

namespace Nid.Assets.Infrastructure.Persistence;

/// <summary>An asset row is logged against its own item.</summary>
public sealed class AssetsAuditMap : IAuditMap
{
    public IReadOnlyDictionary<Type, AuditRule> Audited { get; } = new Dictionary<Type, AuditRule>
    {
        [typeof(AssetRow)] = new(e => AuditItems.Of(e, nameof(AssetRow.ItemId))),
        [typeof(MileageReadingRow)] = new(e => AuditItems.Of(e, nameof(MileageReadingRow.ItemId))),
    };

    public IReadOnlySet<Type> Excluded { get; } = new HashSet<Type> { typeof(AuditEntryRow) };
}
