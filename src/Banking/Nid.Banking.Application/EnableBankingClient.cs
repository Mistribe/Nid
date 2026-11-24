using Nid.Banking.Contracts;
using NodaTime;
using UnambitiousFx.Functional;

namespace Nid.Banking.Application;

/// <summary>
/// Read-only access to Enable Banking: no payment call exists. A failure's code is <c>enablebanking.&lt;API error name&gt;</c> when the API
/// named one, <see cref="BankingCodes.ProviderError"/> when it could not be reached or understood.
/// </summary>
public interface IEnableBankingClient
{
    Task<Result<IReadOnlyList<Aspsp>>> AspspsAsync(EnableBankingApp app, string country, CancellationToken ct);

    Task<Result<AuthStart>> StartAuthAsync(EnableBankingApp app, Aspsp aspsp, Instant validUntil, string state, string redirectUrl, CancellationToken ct);

    Task<Result<AuthorizedSession>> CreateSessionAsync(EnableBankingApp app, string code, CancellationToken ct);

    Task<Result<IReadOnlyList<ProviderBalance>>> BalancesAsync(EnableBankingApp app, string sessionId, string accountUid, PsuHeaders? psu, CancellationToken ct);

    /// <summary>The booked lines since <paramref name="dateFrom"/> (all the bank gives when <c>null</c>), every page followed.</summary>
    Task<Result<ProviderTransactions>> TransactionsAsync(
        EnableBankingApp app, string sessionId, string accountUid, LocalDate? dateFrom, PsuHeaders? psu, CancellationToken ct);

    Task<Result> DeleteSessionAsync(EnableBankingApp app, string sessionId, CancellationToken ct);

    /// <summary>Issue #343: the application the id and the key sign for (<c>GET /application</c>), the one call that reads no bank.</summary>
    Task<Result<EnableBankingApplication>> ApplicationAsync(EnableBankingApp app, CancellationToken ct);
}
