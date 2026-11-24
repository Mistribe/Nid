using NodaTime;

namespace Nid.Banking.Contracts;

/// <summary>An Enable Banking application: the id the JWT names and the PEM that signs it. <see cref="ToString"/> keeps the key out of any log.</summary>
public sealed record EnableBankingApp(string Id, string PrivateKeyPem)
{
    public override string ToString() => $"EnableBankingApp {{ Id = {Id} }}";
}

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
    string? EntryReference, LocalDate BookedOn, LocalDate? ValueOn, decimal Amount, string Currency, string Label, string? Counterparty, string? Remittance, string? BankCode = null);

/// <summary>The lines read; <paramref name="Truncated"/> when the page cap stopped the read while the bank had more.</summary>
public sealed record ProviderTransactions(IReadOnlyList<ProviderTransaction> Lines, bool Truncated);

/// <summary>The browser's address and agent, forwarded on a manual sync (banks may require them).</summary>
public sealed record PsuHeaders(string? IpAddress, string? UserAgent);

/// <summary>What Enable Banking says of the application: whether it is active, and the redirect URLs it accepts.</summary>
public sealed record EnableBankingApplication(bool Active, IReadOnlyList<string> RedirectUrls);
