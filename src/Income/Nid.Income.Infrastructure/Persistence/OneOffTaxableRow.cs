namespace Nid.Income.Infrastructure.Persistence;

/// <summary>
/// The net taxable income of a one-off, which is a Core flow and so has no column for it: one row per flow, keyed by the
/// flow's id. A correction supersedes the flow under a new id, so the store moves the row with it.
/// </summary>
public sealed class OneOffTaxableRow
{
    public Guid FlowId { get; set; }
    public Guid SourceId { get; set; }
    public decimal TaxableAmount { get; set; }
}
