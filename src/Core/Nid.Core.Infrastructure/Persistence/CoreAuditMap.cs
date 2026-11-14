using Nid.Platform.Infrastructure.Audit;

namespace Nid.Core.Infrastructure.Persistence;

/// <summary>1F3a spec §4.4 for the Core tables.</summary>
public sealed class CoreAuditMap : IAuditMap
{
    public IReadOnlyDictionary<Type, AuditRule> Audited { get; } = new Dictionary<Type, AuditRule>
    {
        [typeof(ItemRow)] = new(e => AuditItems.Of(e, nameof(ItemRow.Id))),
        [typeof(OwnershipRow)] = new(e => AuditItems.Of(e, nameof(OwnershipRow.ItemId))),
        [typeof(PersonRow)] = AuditRule.NoItem,
        [typeof(HouseholdSettingsRow)] = AuditRule.NoItem,
        [typeof(TaxProfileRow)] = AuditRule.NoItem,
        [typeof(TaxAssessmentRow)] = AuditRule.NoItem,
        [typeof(TaxAssessmentDeclarantRow)] = AuditRule.NoItem,
        // A scenario's document names its funding envelopes: never shown in an item's history, where a reader who no longer sees
        // an envelope would find its id (property purchase spec §5).
        [typeof(ScenarioRow)] = AuditRule.NoItem,
        [typeof(ValuationRow)] = new(e => AuditItems.Of(e, nameof(ValuationRow.ItemId))),
        [typeof(FlowRow)] = new(e => AuditItems.Of(e, nameof(FlowRow.ItemId))),
        [typeof(ProposalRow)] = new(e => AuditItems.Of(e, nameof(ProposalRow.TargetItemId))),
    };

    // tokens: their life is told by the token_* security events (spec 4A §2). Local accounts and their links (issue 215):
    // a password hash or a link's hash never reaches the audit; the login_*, password_* and credential_link_* events tell their life.
    // fx_rate: one bulk upsert the interceptor never sees, and collections have their journal (U8).
    public IReadOnlySet<Type> Excluded { get; } = new HashSet<Type> { typeof(FxRateRow), typeof(FxSyncRunRow), typeof(CpiRow), typeof(CpiRunRow), typeof(AuditEntryRow), typeof(PersonalAccessTokenRow),
        typeof(LocalCredentialRow), typeof(CredentialLinkRow) };
}
