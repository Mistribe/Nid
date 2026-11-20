using Nid.Banking.Application;
using Nid.Core.Contracts;

namespace Nid.Banking.Infrastructure.Persistence;

/// <summary>Answers Core's <see cref="IBankingApplicationUse"/> from the connections.</summary>
public sealed class BankingApplicationUse(IConnectionStore connections) : IBankingApplicationUse
{
    public Task<bool> InUseAsync(string applicationId, CancellationToken ct) => connections.InUseAsync(applicationId, null, ct);
}
