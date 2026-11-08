using NodaTime;
using UnambitiousFx.Functional;

namespace Nid.Banking.Contracts;

/// <summary>Banking settings (stage 5A): bound from configuration section <c>EnableBanking</c>; the PEM arrives from the Docker secret file.</summary>
public sealed class BankingOptions
{
    public string? PrivateKey { get; set; }

    public string NightlyCron { get; set; } = "0 30 4 * * ?";

    /// <summary>How long the <c>state</c> of a started authorisation stays valid.</summary>
    public int StateMinutes { get; set; } = 15;
}

public sealed record Aspsp(string Name, string Country, string? Logo, int? MaximumConsentSeconds);

public sealed record AuthStart(string Url);

public sealed record AuthorizedSession(string SessionId, Instant ValidUntil, IReadOnlyList<SessionAccount> Accounts);

public sealed record ProviderBalance(string Type, decimal Amount, string Currency, LocalDate? ReferenceDate);

/// <summary>A booked line as the bank reports it; <paramref name="Amount"/> is signed (debit negative).</summary>
public sealed record ProviderTransaction(
    string? EntryReference, LocalDate BookedOn, LocalDate? ValueOn, decimal Amount, string Currency, string Label, string? Counterparty, string? Remittance);

/// <summary>The lines read; <paramref name="Truncated"/> when the page cap stopped the read while the bank had more.</summary>
public sealed record ProviderTransactions(IReadOnlyList<ProviderTransaction> Lines, bool Truncated);

/// <summary>The browser's address and agent, forwarded on a manual sync (banks may require them).</summary>
public sealed record PsuHeaders(string? IpAddress, string? UserAgent);

/// <summary>
/// Read-only access to Enable Banking: no payment call exists. A failure's code is <c>enablebanking.&lt;API error name&gt;</c> when the API
/// named one, <see cref="BankingCodes.ProviderError"/> when it could not be reached or understood.
/// </summary>
public interface IEnableBankingClient
{
    Task<Result<IReadOnlyList<Aspsp>>> AspspsAsync(string appId, string country, CancellationToken ct);

    Task<Result<AuthStart>> StartAuthAsync(string appId, Aspsp aspsp, Instant validUntil, string state, string redirectUrl, CancellationToken ct);

    Task<Result<AuthorizedSession>> CreateSessionAsync(string appId, string code, CancellationToken ct);

    Task<Result<IReadOnlyList<ProviderBalance>>> BalancesAsync(string appId, string sessionId, string accountUid, PsuHeaders? psu, CancellationToken ct);

    /// <summary>The booked lines since <paramref name="dateFrom"/> (all the bank gives when <c>null</c>), every page followed.</summary>
    Task<Result<ProviderTransactions>> TransactionsAsync(
        string appId, string sessionId, string accountUid, LocalDate? dateFrom, PsuHeaders? psu, CancellationToken ct);

    Task<Result> DeleteSessionAsync(string appId, string sessionId, CancellationToken ct);
}
