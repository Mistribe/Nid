namespace Nid.Banking.Contracts;

public static class BankingCodes
{
    public const string Disabled = "banking.disabled";
    public const string NotConfigured = "banking.not_configured";
    public const string UnknownAspsp = "banking.unknown_aspsp";
    public const string StateInvalid = "banking.state_invalid";
    public const string AuthRefused = "banking.auth_refused";
    public const string ConnectionNotFound = "banking.connection_not_found";
    public const string AccountNotFound = "banking.account_not_found";
    public const string EnvelopeKind = "banking.envelope_kind";
    public const string EnvelopeTaken = "banking.envelope_taken";
    public const string SyncRunning = "banking.sync_running";
    public const string ProviderError = "banking.provider_error";

    /// <summary>An account associated with an envelope in another currency: its balance would be written as the wrong money.</summary>
    public const string CurrencyMismatch = "banking.currency_mismatch";

    /// <summary>Only a card is attached to an account, and only to a checking one (D10).</summary>
    public const string CardParent = "banking.card_parent";

    /// <summary>The journal's failure code of a run still open an hour after it started.</summary>
    public const string RunAbandoned = "banking.sync_run_abandoned";

    /// <summary>The journal's failure code of a run cut short by the host shutting down.</summary>
    public const string RunInterrupted = "banking.sync_run_interrupted";

    /// <summary>The journal's failure code of a connection whose reading threw: the run went on with the next one.</summary>
    public const string RunCrashed = "banking.sync_run_crashed";
}
