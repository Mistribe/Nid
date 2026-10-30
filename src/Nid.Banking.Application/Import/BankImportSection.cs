using System.Text.Json;
using Nid.Banking.Contracts;
using Nid.Banking.Domain;
using Nid.Banking.Domain.Files;
using Nid.Core.Contracts;
using Nid.Investments.Contracts;
using Nid.Platform.Contracts;
using NodaTime;
using NodaTime.Text;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;

namespace Nid.Banking.Application.Import;

/// <summary>
/// The sections <c>bank_accounts</c> and <c>bank_transactions</c> (bank sync D24): the accounts a person reads and their booked lines,
/// never a connection, a session or a balance. On import an account is the one its ref names (<c>acc-&lt;id&gt;</c> of this
/// installation, else an account imported before under that ref) when the importer may write it, left as it is; else a new one under
/// the importer's <c>file</c> connection, associated by 5A's rules (<see cref="BankAccountRules"/>). Lines dedupe as a statement's do
/// (<see cref="ITransactionStore.AddAsync"/>). Runs after the investments sections: an envelope id resolves through <see cref="ImportContext.Items"/>.
/// </summary>
public sealed class BankImportSection(
    BankingAccess access, IAccountStore accounts, IConnectionStore connections, ITransactionStore transactions, IEnvelopeStore envelopes,
    IAccessPolicy policy, IHouseholdCalendar calendar) : IImportSection
{
    public const string AccountsSection = "bank_accounts";
    public const string TransactionsSection = "bank_transactions";
    private const string Envelopes = "envelopes";
    private const string RefPrefix = "acc-";

    private sealed class Plan(BankAccountRecord record, BankAccountKind kind, Currency currency, string path)
    {
        public BankAccountRecord Record { get; } = record;
        public BankAccountKind Kind { get; } = kind;
        public Currency Currency { get; } = currency;
        public string Path { get; } = path;

        /// <summary>An account of this installation the ref names: left as it is, its lines added.</summary>
        public BankAccount? Found { get; set; }

        public Guid Id { get; set; }
        public List<NewBankTransaction> Lines { get; } = [];
    }

    private readonly Dictionary<string, Plan> plans = new(StringComparer.Ordinal);
    private IReadOnlyDictionary<string, SectionCounts> counts = new Dictionary<string, SectionCounts>();

    public string Module => "banking";
    public IReadOnlyList<string> Sections => [AccountsSection, TransactionsSection];
    public int Order => 110;

    public IReadOnlyList<ImportIssue> ValidateBatch(string section, JsonElement entities, int firstIndex)
    {
        var issues = new List<ImportIssue>();
        var standIn = ImportSectionKit.StandIns();
        var position = 0;
        foreach (var entity in entities.EnumerateArray())
        {
            var path = NidDataJson.Path(section, firstIndex + position++);
            if (section == AccountsSection)
            {
                if (NidDataJson.TryRead(entity, BankFileJson.Records.BankAccountRecord, path, issues, out var account))
                    TryAccount(account, path, r => standIn(r), issues, out _, out _);
            }
            else if (NidDataJson.TryRead(entity, BankFileJson.Records.BankTransactionRecord, path, issues, out var line))
                TryLine(line, path, issues, out _);
        }
        return issues;
    }

    public async Task<SectionOutcome> ValidateAllAsync(ImportContext context, IStagedBatches staged, CancellationToken ct)
    {
        plans.Clear();
        var issues = new List<ImportIssue>();
        var latest = (await calendar.TodayAsync(ct)).PlusDays(StatementReader.AheadDays);
        var failed = new HashSet<string>(StringComparer.Ordinal); // refused accounts, whose cards and lines are not reported again
        var taken = new HashSet<ItemId>();                       // envelopes a new account of this file goes with

        foreach (var entity in staged.Section(AccountsSection))
        {
            var path = NidDataJson.Path(AccountsSection, entity.Index);
            if (!NidDataJson.TryRead(entity.Value, BankFileJson.Records.BankAccountRecord, path, issues, out var record))
            {
                if (entity.Value.ValueKind == JsonValueKind.Object && entity.Value.TryGetProperty("ref", out var raw) && raw.ValueKind == JsonValueKind.String)
                    failed.Add(raw.GetString()!);
                continue;
            }
            if (plans.ContainsKey(record.Ref) || failed.Contains(record.Ref))
            {
                issues.Add(ImportIssue.Error(ImportCodes.DuplicateId, $"'{record.Ref}' appears twice in {AccountsSection}.", path + ".ref"));
                continue;
            }
            if (!TryAccount(record, path, r => context.Persons.GetValueOrDefault(r), issues, out var kind, out var currency)
                || await PlanAsync(new Plan(record, kind, currency, path), context, staged, taken, issues, ct) is not { } plan)
            {
                failed.Add(record.Ref);
                continue;
            }
            plans[record.Ref] = plan;
        }

        // A new card goes on an account of this file, by 5A's rule.
        foreach (var plan in plans.Values.Where(p => p.Found is null && p.Record.CardOfRef is not null).ToList())
        {
            var parentRef = plan.Record.CardOfRef!;
            if (!failed.Contains(parentRef)) // else reported with its account
            {
                var refusal = !plans.TryGetValue(parentRef, out var parent)
                    ? ImportIssue.Error(ImportCodes.UnresolvedReference, $"'{parentRef}' names no entity of {AccountsSection}.", plan.Path + ".card_of_ref")
                    : BankAccountRules.CardParent(plan.Kind, parent.Found?.Kind ?? parent.Kind) is { } failure
                        ? ImportIssue.Error(failure.Code, failure.Message, plan.Path + ".card_of_ref")
                        : null;
                if (refusal is null)
                    continue;
                issues.Add(refusal);
            }
            plans.Remove(plan.Record.Ref);
            failed.Add(plan.Record.Ref);
        }

        foreach (var entity in staged.Section(TransactionsSection))
        {
            var path = NidDataJson.Path(TransactionsSection, entity.Index);
            if (!NidDataJson.TryRead(entity.Value, BankFileJson.Records.BankTransactionRecord, path, issues, out var record)
                || !TryLine(record, path, issues, out var line, latest) || failed.Contains(record.AccountRef))
                continue;
            if (!plans.TryGetValue(record.AccountRef, out var plan))
                issues.Add(ImportIssue.Error(ImportCodes.UnresolvedReference, $"'{record.AccountRef}' names no entity of {AccountsSection}.", path + ".account_ref"));
            else if (line.Currency != plan.Currency)
                issues.Add(ImportIssue.Error(BankingCodes.CurrencyMismatch, "A line is in its bank account's currency.", path + ".currency"));
            else
                plan.Lines.Add(line);
        }

        var known = 0;
        foreach (var plan in plans.Values.Where(p => p.Found is not null))
            known += await KnownAsync(plan.Id, plan.Lines, ct);
        var lines = plans.Values.Sum(p => p.Lines.Count);
        var existing = plans.Values.Count(p => p.Found is not null);
        counts = new Dictionary<string, SectionCounts>
        {
            [AccountsSection] = new(plans.Count - existing, existing, 0),
            [TransactionsSection] = new(lines - known, known, 0),
        };
        return new SectionOutcome(issues, counts);
    }

    public async Task<Result<IReadOnlyDictionary<string, SectionCounts>>> ApplyAsync(ImportContext context, IStagedBatches staged, CancellationToken ct)
    {
        var created = plans.Values.Where(p => p.Found is null).ToList();
        // A file connection only for what it will hold.
        var fileConnection = created.Count == 0 ? Guid.Empty : await connections.FileConnectionAsync(context.Actor, ct);
        foreach (var plan in created)
        {
            var made = await accounts.UpsertFromFileAsync(
                fileConnection, null, Key(context.Actor, plan.Record.Ref), plan.Currency.Code, plan.Record.Name, ct, plan.Kind, plan.Record.IbanLast4);
            // Another person's import committed it since the validation: theirs, which this one was not checked against.
            if (made.CreatedBy != context.Actor)
                return Result.Failure<IReadOnlyDictionary<string, SectionCounts>>(new Failure(BankingCodes.FileAccountMismatch,
                    $"'{plan.Record.Ref}' became another person's bank account during the import.", null));
            plan.Id = made.Id;
        }

        var today = await calendar.TodayAsync(ct);
        foreach (var plan in created)
        {
            // The envelope's real id: one created by this import was planned at validation.
            ItemId? envelope = plan.Record.EnvelopeRef is { } envelopeRef ? context.Items[(Envelopes, envelopeRef)] : null;
            var account = (await accounts.FindAsync(plan.Id, ct))! with
            {
                Envelope = envelope, AssociatedOn = envelope is null ? null : today, AssociatedBy = envelope is null ? null : context.Actor,
                CardOf = plan.Record.CardOfRef is { } parent ? plans[parent].Id : null,
                AutoApplyBalance = plan.Record.AutoApplyBalance, Ignored = plan.Record.Ignored,
            };
            var updated = await accounts.UpdateAsync(account, ct);
            if (!updated.IsSuccess)
                return ImportSectionKit.FailureOf(updated);
        }

        var (added, known) = (0, 0);
        foreach (var plan in plans.Values)
        {
            var written = await transactions.AddAsync(plan.Id, plan.Lines, ct);
            (added, known) = (added + written.Added, known + written.Known);
        }
        return Result.Success<IReadOnlyDictionary<string, SectionCounts>>(new Dictionary<string, SectionCounts>
        {
            [AccountsSection] = counts[AccountsSection],
            [TransactionsSection] = new(added, known, 0),
        });
    }

    /// <summary>
    /// Every account the reader reads (<see cref="BankingAccess"/>), by name then ref, then their lines by account, date and arrival.
    /// An envelope or a card's account the export does not hold is left out, never named.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, IReadOnlyList<JsonElement>>> ExportAsync(ExportContext context, CancellationToken ct)
    {
        var readable = (await access.ReadableAsync(await accounts.AllAsync(ct), ct)).Select(r => r.Account).ToList();
        var refs = readable.ToDictionary(a => a.Id, a => RefPrefix + a.Id.ToString("D"));
        var ordered = readable.OrderBy(a => a.Name, StringComparer.Ordinal).ThenBy(a => refs[a.Id], StringComparer.Ordinal).ToList();
        var position = ordered.Select((a, i) => (a.Id, i)).ToDictionary(p => p.Id, p => p.i);

        var accountRecords = ordered.Select(a => new BankAccountRecord
        {
            Ref = refs[a.Id],
            Name = a.Name,
            IbanLast4 = a.IbanMasked,
            Kind = a.Kind.ToString().ToLowerInvariant(),
            Currency = a.Currency.Code,
            EnvelopeRef = a.Envelope is { } envelope && context.ItemFileIds.TryGetValue(envelope, out var envelopeRef) ? envelopeRef : null,
            CardOfRef = a.CardOf is { } parent && refs.TryGetValue(parent, out var parentRef) ? parentRef : null,
            AutoApplyBalance = a.AutoApplyBalance,
            Ignored = a.Ignored,
            OwnerRef = context.PersonRef(a.CreatedBy),
        });
        var lines = readable.Count == 0 ? [] : await transactions.ListAsync(readable.Select(a => a.Id).ToList(), null, null, 0, int.MaxValue, ct);
        var lineRecords = lines.OrderBy(t => position[t.Account]).ThenBy(t => t.BookedOn).ThenBy(t => t.Id).Select(t => new BankTransactionRecord
        {
            AccountRef = refs[t.Account],
            ProviderRef = t.ProviderRef,
            Fingerprint = t.Fingerprint,
            BookedOn = LocalDatePattern.Iso.Format(t.BookedOn),
            ValueOn = t.ValueOn is { } valueOn ? LocalDatePattern.Iso.Format(valueOn) : null,
            Amount = t.Amount,
            Currency = t.Currency.Code,
            Label = t.Label,
            Counterparty = t.Counterparty,
            Remittance = t.Remittance,
            FetchedVia = t.FetchedVia,
        });
        return new Dictionary<string, IReadOnlyList<JsonElement>>
        {
            [AccountsSection] = accountRecords.Select(r => JsonSerializer.SerializeToElement(r, BankFileJson.Records.BankAccountRecord)).ToList(),
            [TransactionsSection] = lineRecords.Select(r => JsonSerializer.SerializeToElement(r, BankFileJson.Records.BankTransactionRecord)).ToList(),
        };
    }

    /// <summary>
    /// The account the ref names, checked: one found must be the importer's to write (refused without being named, as a statement's),
    /// in the file's currency; a new one's envelope must take it. Null when refused.
    /// </summary>
    private async Task<Plan?> PlanAsync(
        Plan plan, ImportContext context, IStagedBatches staged, HashSet<ItemId> taken, List<ImportIssue> issues, CancellationToken ct)
    {
        var found = (Exported(plan.Record.Ref) is { } id ? await accounts.FindAsync(id, ct) : null)
            ?? await accounts.FindFromFileAsync(null, Key(context.Actor, plan.Record.Ref), ct);
        if (found is not null)
        {
            if (!(await access.AccountAsync(found.Id, AccessKind.Write, ct)).IsSuccess)
                return Refused(BankingCodes.FileAccountMismatch, "This ref names a bank account you cannot change: give the account another ref.", ".ref");
            if (found.Currency != plan.Currency)
                return Refused(BankingCodes.CurrencyMismatch, $"This bank account is in {found.Currency.Code}.", ".currency");
            plan.Found = found;
            plan.Id = found.Id;
            return plan;
        }
        if (plan.Record.EnvelopeRef is not { } envelopeRef)
            return plan;

        if (context.FailedItems.Contains((Envelopes, envelopeRef)))
            return null; // reported with its envelope
        if (!context.Items.TryGetValue((Envelopes, envelopeRef), out var envelope))
            return Refused(ImportCodes.UnresolvedReference, $"'{envelopeRef}' names no entity of envelopes.", ".envelope_ref");
        // An envelope this import creates is the importer's, new, and of the file's kind.
        var created = context.NewItems.Contains(envelope);
        if (!created && !await ImportSectionKit.CanAsync(policy, context.Actor, envelope, AccessKind.Write, ct))
            return Refused(ImportCodes.NotPermitted, "A bank account goes with an envelope you can change.", ".envelope_ref");
        var kind = created ? StagedKind(staged, envelopeRef) : (await envelopes.FindAsync(envelope, ct))?.Kind;
        if (BankAccountRules.Association(plan.Kind, plan.Currency, kind, context.ItemCurrencies.GetValueOrDefault(envelope)?.Code) is { } refused)
            return Refused(refused.Code, refused.Message, ".envelope_ref");
        if (!taken.Add(envelope) || (!created && await accounts.ForEnvelopeAsync(envelope, ct) is not null))
            return Refused(BankingCodes.EnvelopeTaken, "Another bank account is associated with this envelope.", ".envelope_ref");
        return plan;

        Plan? Refused(string code, string message, string member)
        {
            issues.Add(ImportIssue.Error(code, message, plan.Path + member));
            return null;
        }
    }

    /// <summary>The kind of an envelope this file creates, as its staged record says (validated by its own section).</summary>
    private static EnvelopeKind? StagedKind(IStagedBatches staged, string envelopeRef)
        => staged.Section(Envelopes).Select(e => e.Value).FirstOrDefault(v =>
                v.ValueKind == JsonValueKind.Object && v.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String && id.GetString() == envelopeRef)
            is { ValueKind: JsonValueKind.Object } record && record.TryGetProperty("kind", out var raw) && raw.ValueKind == JsonValueKind.String
            && EnvelopeWire.TryKind(raw.GetString(), out var kind)
                ? kind
                : null;

    /// <summary>The account id in a ref an export wrote (<c>acc-&lt;uuid&gt;</c>), else null.</summary>
    private static Guid? Exported(string fileRef)
        => fileRef.StartsWith(RefPrefix, StringComparison.Ordinal) && Guid.TryParse(fileRef[RefPrefix.Length..], out var id) ? id : null;

    /// <summary>
    /// What keys an imported account's file source, hashed as a statement's account id is (never a bank's number): an exported ref names
    /// one account wherever it went, so two adults moving their exports find the same one; a hand-written ref is its importer's alone.
    /// </summary>
    private static string Key(PersonId actor, string fileRef) => Exported(fileRef) is null ? $"nid.data/{actor.Value}/{fileRef}" : "nid.data/" + fileRef;

    /// <summary>How many of the lines the account holds already, by ref or fingerprint: what the import leaves unchanged.</summary>
    private async Task<int> KnownAsync(Guid account, IReadOnlyList<NewBankTransaction> lines, CancellationToken ct)
    {
        var near = await transactions.NearAsync(account, lines.Select(l => (l.BookedOn, l.Amount)).ToList(), ct);
        var refs = near.Select(t => t.ProviderRef).ToHashSet(StringComparer.Ordinal);
        var fingerprints = near.Select(t => t.Fingerprint).ToHashSet(StringComparer.Ordinal);
        var computed = TransactionFingerprint.ForBatch(lines.Select(l => (l.BookedOn, l.Amount, TransactionLabel.Normalize(l.Label))).ToList());
        return lines.Where((l, i) => refs.Contains(l.ProviderRef!) || fingerprints.Contains(computed[i])).Count();
    }

    /// <summary>The rules local to an account record; <paramref name="person"/> resolves its owner's ref.</summary>
    private static bool TryAccount(
        BankAccountRecord r, string path, Func<string, ResolvedPerson?> person, List<ImportIssue> issues, out BankAccountKind kind, out Currency currency)
    {
        var ok = true;
        if (!NidDataJson.IsFileId(r.Ref))
            ok = Error(issues, ImportCodes.InvalidId, "A ref is lowercase letters, digits and dashes.", path + ".ref");
        if (string.IsNullOrWhiteSpace(r.Name))
            ok = Error(issues, ImportCodes.InvalidValue, "A bank account has a name.", path + ".name");
        if (r.IbanLast4 is { } last4 && (last4.Length is 0 or > 4 || !last4.All(char.IsAsciiLetterOrDigit)))
            ok = Error(issues, ImportCodes.InvalidValue, "At most the IBAN's last four characters.", path + ".iban_last4");
        if (!TryKind(r.Kind, out kind))
            ok = Error(issues, ImportCodes.InvalidValue, "A bank account is checking, savings, card or other.", path + ".kind");
        if (!Currency.TryCreate(r.Currency, out currency))
            ok = Error(issues, ImportCodes.InvalidValue, "A currency is an ISO 4217 code in capitals.", path + ".currency");
        if (person(r.OwnerRef) is null)
            ok = Error(issues, ImportCodes.UnresolvedReference, $"'{r.OwnerRef}' is not in persons.", path + ".owner_ref");
        return ok;
    }

    /// <summary>
    /// The rules local to a line; its fingerprint is the store's to compute again. Booked from 1900 on, and up to <paramref name="latest"/>
    /// once today is known (the whole validation): as a statement's, a booking far ahead would move every later read of the bank.
    /// </summary>
    private static bool TryLine(BankTransactionRecord r, string path, List<ImportIssue> issues, out NewBankTransaction line, LocalDate? latest = null)
    {
        line = null!;
        var ok = true;
        if (string.IsNullOrWhiteSpace(r.ProviderRef))
            ok = Error(issues, ImportCodes.InvalidValue, "A line has its source's ref.", path + ".provider_ref");
        var booked = LocalDatePattern.Iso.Parse(r.BookedOn);
        if (!booked.Success)
            ok = Error(issues, ImportCodes.InvalidValue, "A date is written YYYY-MM-DD.", path + ".booked_on");
        else if (booked.Value < StatementReader.Earliest || booked.Value > latest)
            ok = Error(issues, ImportCodes.InvalidValue, "A line is booked from 1900 on, at most a week ahead.", path + ".booked_on");
        LocalDate? valueOn = null;
        if (r.ValueOn is not null)
        {
            var parsed = LocalDatePattern.Iso.Parse(r.ValueOn);
            if (parsed.Success)
                valueOn = parsed.Value;
            else
                ok = Error(issues, ImportCodes.InvalidValue, "A date is written YYYY-MM-DD.", path + ".value_on");
        }
        // numeric(19,4), as the bank's own lines.
        if (decimal.Round(r.Amount, 4) != r.Amount || Math.Abs(r.Amount) >= 1_000_000_000_000_000m)
            ok = Error(issues, ImportCodes.InvalidValue, "An amount has at most 15 digits before the point and 4 after.", path + ".amount");
        if (!Currency.TryCreate(r.Currency, out var currency))
            ok = Error(issues, ImportCodes.InvalidValue, "A currency is an ISO 4217 code in capitals.", path + ".currency");
        if (string.IsNullOrWhiteSpace(r.Label))
            ok = Error(issues, ImportCodes.InvalidValue, "A line has a label.", path + ".label");
        if (r.FetchedVia is not ("api" or "file"))
            ok = Error(issues, ImportCodes.InvalidValue, "A line was fetched via api or file.", path + ".fetched_via");
        if (ok)
            line = new NewBankTransaction(r.ProviderRef, booked.Value, valueOn, r.Amount, currency, r.Label, r.Counterparty, r.Remittance, r.FetchedVia);
        return ok;
    }

    private static bool TryKind(string wire, out BankAccountKind kind)
    {
        foreach (var k in Enum.GetValues<BankAccountKind>())
            if (k.ToString().ToLowerInvariant() == wire)
            {
                kind = k;
                return true;
            }
        kind = default;
        return false;
    }

    private static bool Error(List<ImportIssue> issues, string code, string message, string path)
    {
        issues.Add(ImportIssue.Error(code, message, path));
        return false;
    }
}
