using Nid.Core.Contracts;
using Nid.Tax.Contracts;
using Nid.Tax.Domain;
using Nid.Platform.Contracts;
using UnambitiousFx.Functional;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Tax.Application;

/// <summary>
/// The dashboard's « Impôts »: a twelfth of this year's estimated total tax, in euros (the estimate reads euro lines only), as a
/// commitment so the month's rest is what is left after it. A refund costs nothing a month: no source then.
/// </summary>
public sealed class TaxCommitments(TaxEstimates estimates) : IEstimatedCommitments
{
    public async Task<Result<CommitmentsPart>> CommitmentsAsync(PersonId reader, VisibilityScope scope, NodaTime.LocalDate today, CancellationToken ct, bool includeArchived = false)
    {
        // A year before the rules (the annual review's past months) has no estimate: nothing to commit, never a throw.
        if (today.Year < TaxRules.FirstYear)
            return Result.Success(new CommitmentsPart(CommitmentSources.Tax, 0, new Dictionary<Currency, decimal>(), new Dictionary<Currency, decimal>()));
        var result = await estimates.ComputeAsync(today.Year, scope, today, ct);
        if (result.TryGetFailure(out var failure)) return Result.Failure<CommitmentsPart>(failure!);
        result.TryGetValue(out var r);
        var monthly = Math.Max(0m, r!.TotalTax) / 12;
        // Nothing to pay puts no euro entry, which would ask for a rate in a household of another currency.
        var amortized = monthly > 0m ? new Dictionary<Currency, decimal> { [Currency.Eur] = monthly } : new Dictionary<Currency, decimal>();
        return Result.Success(new CommitmentsPart(CommitmentSources.Tax, amortized.Count, amortized, new Dictionary<Currency, decimal>()));
    }
}
