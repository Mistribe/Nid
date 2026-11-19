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

    /// <summary>A bank account's own name is 1 to 100 characters, and not only whitespace.</summary>
    public const string InvalidName = "banking.invalid_name";

    /// <summary>Only a card is attached to an account, and only to a checking one (D10).</summary>
    public const string CardParent = "banking.card_parent";

    /// <summary>The journal's failure code of a run still open an hour after it started.</summary>
    public const string RunAbandoned = "banking.sync_run_abandoned";

    /// <summary>The journal's failure code of a run cut short by the host shutting down.</summary>
    public const string RunInterrupted = "banking.sync_run_interrupted";

    /// <summary>The journal's failure code of a connection whose reading threw: the run went on with the next one.</summary>
    public const string RunCrashed = "banking.sync_run_crashed";

    /// <summary>A transactions read hit the page cap: the run is journaled failed, the connection stays active.</summary>
    public const string TransactionsTruncated = "banking.transactions_truncated";

    /// <summary>
    /// #355: an account the bank sent in <c>XXX</c> (no currency) whose balances did not name a single one: skipped, journaled with this
    /// code; it takes no envelope until a read learns its currency.
    /// </summary>
    public const string CurrencyUnknown = "banking.currency_unknown";

    /// <summary>#362: a minimum usage on an account that is not a card, or outside 1 to 31 payments a month.</summary>
    public const string CardRule = "banking.card_rule";

    /// <summary>A statement file that is neither OFX nor CAMT.053.</summary>
    public const string FileFormat = "banking.file_format";

    /// <summary>A statement file refused whole: broken syntax, a missing field, an entity, or more than 20 000 lines.</summary>
    public const string FileMalformed = "banking.file_malformed";

    /// <summary>A statement file over 5 MiB.</summary>
    public const string FileTooLarge = "banking.file_too_large";

    /// <summary>
    /// A statement file of an account the importer cannot write, or dropped on an envelope whose account is another; the account is never
    /// named.
    /// </summary>
    public const string FileAccountMismatch = "banking.file_account_mismatch";

    /// <summary>A statement file holding statements of more than one account (or currency).</summary>
    public const string FileSeveralAccounts = "banking.file_several_accounts";

    /// <summary>Linking or ignoring a line that is not <c>unlinked</c> (409).</summary>
    public const string TransactionLinked = "banking.transaction_linked";

    /// <summary>A link to a module other than <c>subscriptions</c> or <c>realestate</c>, or to an item of another module.</summary>
    public const string LinkTarget = "banking.link_target";

    /// <summary>A bank proposal approved with an edited payload: reject it instead.</summary>
    public const string ProposalNotEditable = "banking.proposal_not_editable";

    /// <summary>A categorise proposal names 1 to 100 lines, each once.</summary>
    public const string ProposalLines = "banking.proposal_lines";

    /// <summary>A category's name is 1 to 60 characters, and unique among its siblings whatever the case (archived ones included).</summary>
    public const string CategoryName = "banking.category_name";

    /// <summary>Categories are two levels deep: a child has no children, and a category with children stays top-level.</summary>
    public const string CategoryDepth = "banking.category_depth";

    /// <summary>A move or a merge between categories of different kinds (expense, income, transfer).</summary>
    public const string CategoryKind = "banking.category_kind";

    /// <summary>A new or moved child under an archived parent, or a merge into an archived category.</summary>
    public const string CategoryArchived = "banking.category_archived";

    /// <summary>A rule's pattern is 2 to 60 characters once normalised.</summary>
    public const string RulePattern = "banking.rule_pattern";

    /// <summary>A rule's amount bounds are not negative, and the minimum is not above the maximum.</summary>
    public const string RuleAmounts = "banking.rule_amounts";
}
