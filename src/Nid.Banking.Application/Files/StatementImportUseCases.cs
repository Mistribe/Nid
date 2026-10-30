using System.Globalization;
using System.Text.Json.Serialization;
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
    : ICommand<StatementImportView>, IBrowserOnly, IRecordsSecurityEvent
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
    IClock clock)
    : IRequestHandler<ImportStatementCommand, StatementImportView>
{
    public async ValueTask<Result<StatementImportView>> HandleAsync(ImportStatementCommand command, CancellationToken ct = default)
    {
        // A file needs no Enable Banking application: banking on is enough (ruling R-d).
        var enabled = await access.EnabledAsync(ct);
        if (!enabled.IsSuccess)
            return BankingAccess.Failed<StatementImportView>(enabled);
        var latest = (await calendar.TodayAsync(ct)).PlusDays(StatementReader.AheadDays);
        StatementFile file;
        try
        {
            file = StatementReader.Read(command.File, latest);
        }
        catch (StatementFileException e)
        {
            return Result.Failure<StatementImportView>(new Failure(e.Code, e.Message, e.Line is { } line ? new Dictionary<string, object?> { ["line"] = line } : null));
        }
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
            .Select(l => new NewBankTransaction(l.ProviderRef, l.BookedOn, l.ValueOn, l.Amount, currency, l.Label, l.Counterparty, l.Remittance, "file"))
            .ToList();
        var probable = await ProbablyKnownAsync(account.Id, lines, ct);
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

    /// <summary>
    /// D23, on an account the API reads: a line whose fingerprint and ref are unknown, held back by a line from the API of the same amount
    /// booked within a day that no line of the file is exactly. One to one: an API line holds back one file line at most, the same day's first.
    /// </summary>
    private async Task<bool[]> ProbablyKnownAsync(Guid account, IReadOnlyList<NewBankTransaction> lines, CancellationToken ct)
    {
        var probable = new bool[lines.Count];
        if (!await transactions.HasFetchedViaAsync(account, "api", ct))
            return probable;

        var near = await transactions.NearAsync(account, lines.Select(l => (l.BookedOn, l.Amount)).ToList(), ct);
        var fingerprints = TransactionFingerprint.ForBatch(lines.Select(l => (l.BookedOn, l.Amount, TransactionLabel.Normalize(l.Label))).ToList());
        var fileFingerprints = fingerprints.ToHashSet();
        var fileRefs = lines.Select(l => l.ProviderRef).OfType<string>().ToHashSet();
        var storedFingerprints = near.Select(t => t.Fingerprint).ToHashSet();
        var storedRefs = near.Select(t => t.ProviderRef).ToHashSet();
        var twins = near.Where(t => t.FetchedVia == "api" && !fileFingerprints.Contains(t.Fingerprint) && !fileRefs.Contains(t.ProviderRef)).ToList();
        // The file's unknown lines by (amount, day), in the file's order.
        var open = Enumerable.Range(0, lines.Count)
            .Where(i => !storedFingerprints.Contains(fingerprints[i]) && !(lines[i].ProviderRef is { } r && storedRefs.Contains(r)))
            .GroupBy(i => (lines[i].Amount, lines[i].BookedOn))
            .ToDictionary(g => g.Key, g => new Queue<int>(g));
        var unmatched = twins.Where(t => !Take(t.Amount, t.BookedOn)).ToList();
        foreach (var t in unmatched)
            _ = Take(t.Amount, t.BookedOn.PlusDays(-1)) || Take(t.Amount, t.BookedOn.PlusDays(1));
        return probable;

        bool Take(decimal amount, LocalDate on)
        {
            if (!open.TryGetValue((amount, on), out var waiting) || !waiting.TryDequeue(out var i))
                return false;
            probable[i] = true;
            return true;
        }
    }
}
