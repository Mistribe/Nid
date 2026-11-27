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

    /// <summary>An Enable Banking application that connections still use cannot be removed or replaced.</summary>
    public const string ApplicationInUse = "banking.application_in_use";

    /// <summary>#414: the installation-wide application can no longer be set, nor replaced by another id; only kept or cleared.</summary>
    public const string InstallationDeprecated = "banking.installation_application_deprecated";

    /// <summary>Bank transactions are kept 1 to 600 months (D3). Its value is Core's, where the setting lived before #513.</summary>
    public const string InvalidBankRetention = "core.invalid_bank_retention";

    /// <summary>
    /// #414: the last error of a connection the transfer of the installation application ended (status <c>revoked</c>): it was made with
    /// an application that is now another adult's, so its creator must declare their own and link the bank again.
    /// </summary>
    public const string ApplicationTransferred = "banking.application_transferred";

    /// <summary>An application id that is empty or longer than 100 characters.</summary>
    public const string ApplicationIdInvalid = "banking.application_id_invalid";

    /// <summary>An application declared without its private key.</summary>
    public const string KeyRequired = "banking.key_required";

    /// <summary>The adult's application changed at the same time: nothing was saved, try again.</summary>
    public const string ApplicationChanged = "banking.application_changed";

    /// <summary>A private key that is not an RSA private key in PEM.</summary>
    public const string KeyInvalid = "banking.key_invalid";

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

    /// <summary>#85: a spending period names both its months, the first not after the last, 24 months at most.</summary>
    public const string SpendingPeriod = "banking.spending_period";

    /// <summary>#85: the lines of a spending figure name a category, or « Non classées », not both nor neither.</summary>
    public const string SpendingTarget = "banking.spending_target";

    /// <summary>#85: a budget's amount is a number of zero or more, under 10^15.</summary>
    public const string BudgetAmount = "banking.budget_amount";

    /// <summary>#85: a budget on a category and one on its parent or a child: one per branch.</summary>
    public const string BudgetBranch = "banking.budget_branch";

    /// <summary>#427: budgets created at once never replace one: the category has its budget already (or is asked twice).</summary>
    public const string BudgetExists = "banking.budget_exists";

    /// <summary>#85: a merge that would leave two budgets on one category or on one branch.</summary>
    public const string BudgetConflict = "banking.budget_conflict";

    /// <summary>#84: a split's parts do not add up to the line's amount.</summary>
    public const string SplitSum = "banking.split_sum";

    /// <summary>#84: a split has 2 to 20 parts, each non-zero, of the line's sign, to 4 decimals, with a note of 500 characters at most.</summary>
    public const string SplitPart = "banking.split_part";

    /// <summary>#84: a split line has no category of its own: its parts do (un-split it first).</summary>
    public const string LineSplit = "banking.line_split";

    /// <summary>#426: an ignored line counts nowhere, so it takes no category (un-ignore it first).</summary>
    public const string LineIgnored = "banking.line_ignored";

    /// <summary>#426: several lines categorised at once name 1 to 200 of them.</summary>
    public const string CategoriseLines = "banking.categorise_lines";

    /// <summary>#450: a calibration that threw (logged): it is not run again until an admin asks.</summary>
    public const string CalibrationFailed = "banking.calibration_failed";
}
