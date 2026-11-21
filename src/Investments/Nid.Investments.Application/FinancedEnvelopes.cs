using Nid.Core.Contracts;
using Nid.Investments.Contracts;
using Nid.RealEstate.Contracts;

namespace Nid.Investments.Application;

/// <summary>#180: an SCPI envelope is what a loan may finance besides a property.</summary>
public sealed class FinancedEnvelopes(IEnvelopeStore envelopes) : IFinancedEnvelopes
{
    public async Task<bool> IsScpiAsync(ItemId item, CancellationToken ct)
        => await envelopes.ReadAllAsync([item], ct) is [{ Envelope.Kind: EnvelopeKind.Scpi }];
}
