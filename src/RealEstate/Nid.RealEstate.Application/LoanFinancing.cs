using Nid.Core.Contracts;
using Nid.RealEstate.Contracts;

namespace Nid.RealEstate.Application;

/// <summary>#510: the loans that finance an envelope, read by Investments without RealEstate's store.</summary>
public sealed class LoanFinancing(ILoanStore loans) : ILoanFinancing
{
    public async Task<bool> IsFinancedAsync(ItemId envelope, CancellationToken ct)
        => (await loans.FinancingAsync(envelope, ct)).Count > 0;
}
