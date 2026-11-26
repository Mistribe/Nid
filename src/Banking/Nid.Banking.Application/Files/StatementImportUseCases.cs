using System.Globalization;
using System.Text.Json.Serialization;
using Nid.Banking.Application.Reconciliation;
using Nid.Banking.Application.Sync;
using Nid.Banking.Contracts;
using Nid.Banking.Domain;
using Nid.Banking.Domain.Files;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Banking.Application.Files;

/// <summary>
/// Spec §5.2: a statement file read, its account found or created, then previewed (<paramref name="Confirm"/> false: nothing written) or
/// written. Two calls of the same command, no server state: the browser sends the file again to confirm. Dropped on
/// <paramref name="Envelope"/>, the account must be, or become, that envelope's. <paramref name="KeepProbablyKnown"/> writes the lines D23
/// sets aside.
/// </summary>
public sealed record ImportStatementCommand(byte[] File, ItemId? Envelope, bool Confirm, bool KeepProbablyKnown)
    : ICommand<StatementImportView>, IBrowserOnly, IRecordsSecurityEvent, IRequiresBanking
{
    /// <summary>Spec §10: a written import, under the account's envelope when it has one; counts only, never a label, an amount or an IBAN.</summary>
    public SecurityEvent? EventFor(object response)
        => response is StatementImportView { Written: true, AccountId: { } account } view
            ? new SecurityEvent(SecurityEventNames.BankStatementImported, new PersonId(view.ImporterId), view.EnvelopeId is { } envelope ? [new ItemId(envelope)] : [],
                new Dictionary<string, string>
                {
                    ["account"] = account.ToString(), ["source"] = "file", ["new"] = Count(view.NewCount), ["known"] = Count(view.KnownCount),
                    ["probably_known"] = Count(view.ProbablyKnownCount), ["kept"] = KeepProbablyKnown ? "true" : "false",
                })
            : null;

    private static string Count(int count) => count.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// What an import finds: <paramref name="NewCount"/> lines written (or that would be), <paramref name="KnownCount"/> already stored,
/// <paramref name="ProbablyKnownCount"/> held for lines the API gave (D23), written, and counted new, only when kept.
/// <paramref name="AccountId"/> is <c>null</c> in the preview of a new account; <paramref name="ClosingBalance"/> a decimal string in the file's
/// <paramref name="Currency"/>.
/// </summary>
public sealed record StatementImportView(
    string Format, Guid? AccountId, bool NewAccount, string AccountName, string? IbanMasked, LocalDate? From, LocalDate? To, int NewCount,
    int KnownCount, int ProbablyKnownCount, string Currency, string? ClosingBalance, LocalDate? ClosingBalanceOn, bool Written,
    [property: JsonIgnore] Guid ImporterId = default, [property: JsonIgnore] Guid? EnvelopeId = null);

/// <summary>
/// A preview runs the import as a confirmation would, in one unit of work it never commits: its counts are the write's. An account the
/// importer cannot write is refused without being named; a new one hangs off their <c>file</c> connection. The closing balance is stored as
/// the day's and valued (spec §4) like the API's, never as a « click ».
/// </summary>
[RequestHandler<ImportStatementCommand, StatementImportView>]
public sealed class ImportStatementHandler(
    BankingAccess access, IConnectionStore connections, IAccountStore accounts, ITransactionStore transactions, IBalanceStore balances,
    IAccessPolicy policy, BalanceValuation valuation, IHouseholdCalendar calendar, IInvoker invoker, IUnitOfWork unitOfWork, IServiceProvider services,
    IClock clock, BankReconciliation reconciliation)
    : IRequestHandler<ImportStatementCommand, StatementImportView>
{
    public async ValueTask<Result<StatementImportView>> HandleAsync(ImportStatementCommand command, CancellationToken ct = default)
    {
        var imported = await ImportAsync(command, ct);
        // Once the import's unit of work is over: its lines stay stored whatever the pass does.
        if (imported.TryGetValue(out var view) && view.Written && view.AccountId is { } account)
            await reconciliation.RunAsync([account], await calendar.TodayAsync(ct), ct);
        return imported;
    }

    private async Task<Result<StatementImportView>> ImportAsync(ImportStatementCommand command, CancellationToken ct)
    {
        // A file needs no Enable Banking application: banking on is enough (ruling R-d).
        var latest = (await calendar.TodayAsync(ct)).PlusDays(StatementReader.AheadDays);
        var read = StatementReader.Read(command.File, latest);
        if (!read.TryGetValue(out var file))
            return BankingAccess.Failed<StatementImportView, StatementFile>(read);
        if (!Currency.TryCreate(file.Currency.ToUpperInvariant(), out var currency))
            return BankingAccess.Fail<StatementImportView>(BankingCodes.FileMalformed, "The statement's currency is no ISO code.");
        // Whether the envelope has an account is told only to whoever writes it.
        if (command.Envelope is { } envelope && (await policy.CheckAsync(access.Person, envelope, AccessKind.Write, ct)).TryGetFailure(out var denied))
            return Result.Failure<StatementImportView>(denied!);

        await using var transaction = await unitOfWork.BeginAsync(ct);
        // First, for its lock: two imports of one person run one after the other.
        var fileConnection = await connections.FileConnectionAsync(access.Person, ct);
        var found = await accounts.FindFromFileAsync(file.Iban, file.AccountId, ct);
        if (found is not null && !(await access.AccountAsync(found.Id, AccessKind.Write, ct)).IsSuccess)
            return Mismatch();
        if (found is not null && found.Currency != currency)
            return BankingAccess.Fail<StatementImportView>(BankingCodes.CurrencyMismatch, "The statement is in another currency than its bank account.");
        var key = file.Iban ?? file.AccountId;
        var account = found ?? await accounts.UpsertFromFileAsync(
            fileConnection, file.Iban, file.AccountId, currency.Code, "Relevé ·" + key[^Math.Min(4, key.Length)..], ct,
            file.Card ? BankAccountKind.Card : BankAccountKind.Checking);
        // Another person's import committed since the lookup: their account, which this one has not been allowed to write.
        if (account.CreatedBy != access.Person && found is null)
            return Mismatch();

        if (command.Envelope is { } target && account.Envelope != target)
        {
            if (await accounts.ForEnvelopeAsync(target, ct) is not null)
                return Mismatch();
            // 5A's rules: the envelope's kind and currency, one account per envelope, moved by its owner only.
            var associated = await invoker.InvokeAsync(new UpdateBankAccountCommand(account.Id, target.Value, false, null, false, null, null), ct);
            if (associated.TryGetFailure(out var failure))
                return Result.Failure<StatementImportView>(failure!);
            account = (await accounts.FindAsync(account.Id, ct))!;
        }

        var lines = file.Lines
            .Select(l => new NewBankTransaction(l.ProviderRef, l.BookedOn, l.ValueOn, l.Amount, currency, l.Label, l.Counterparty, l.Remittance, "file", l.BankCode))
            .ToList();
        var probable = await ProbablyKnown.ForAsync(transactions, account.Id, lines, "api", ct);
        var written = await transactions.AddAsync(account.Id, command.KeepProbablyKnown ? lines : lines.Where((_, i) => !probable[i]).ToList(), ct);
        var view = new StatementImportView(
            file.Format, found is null && !command.Confirm ? null : account.Id, found is null, account.Name, account.IbanMasked, file.From, file.To,
            written.Added, written.Known, probable.Count(p => p), currency.Code, file.ClosingBalance?.ToString("0.00", CultureInfo.InvariantCulture), file.ClosingBalanceOn,
            command.Confirm, access.Person.Value, account.Envelope?.Value);
        if (!command.Confirm)
            return Result.Success(view); // rolled back

        if (file.ClosingBalance is { } closing && file.ClosingBalanceOn is { } on)
            await balances.UpsertAsync(new BankBalance(account.Id, on, closing, "CLBD", clock.GetCurrentInstant()), ct);
        if (account.Envelope is not null)
            await valuation.ValueAsync(services, access.Person, account, await calendar.TodayAsync(ct), manual: false, ct);
        await transaction.CommitAsync(ct);
        return Result.Success(view);
    }

    private static Result<StatementImportView> Mismatch()
        => BankingAccess.Fail<StatementImportView>(BankingCodes.FileAccountMismatch, "This statement is of another bank account.");
}
