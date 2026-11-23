using Nid.Documents.Contracts;
using Nid.Platform.Infrastructure.Audit;

namespace Nid.Documents.Infrastructure.Persistence;

/// <summary>A document belongs to no item; a link is logged against the item that carries its target, if any.</summary>
public sealed class DocumentsAuditMap : IAuditMap
{
    public IReadOnlyDictionary<Type, AuditRule> Audited { get; } = new Dictionary<Type, AuditRule>
    {
        [typeof(DocumentRow)] = AuditRule.NoItem,
        [typeof(DocumentLinkRow)] = new(e => AuditItems.Of(e, nameof(DocumentLinkRow.AnchorItemId))),
        [typeof(ExpectedDismissalRow)] = new(e => (string)e.Property(nameof(ExpectedDismissalRow.TargetKind)).CurrentValue! == TargetKinds.CoreItem
            ? AuditItems.Of(e, nameof(ExpectedDismissalRow.TargetId))
            : []),
    };

    /// <summary>#167: a document's text and its job are derived from the file, the upload being audited.</summary>
    public IReadOnlySet<Type> Excluded { get; } = new HashSet<Type>
    {
        typeof(AuditEntryRow), typeof(UploadTicketRow), typeof(DocumentTextRow), typeof(DocumentTextJobRow),
    };
}
