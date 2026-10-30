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
/// The sections <c>bank_accounts</c>, <c>bank_transactions</c>, <c>bank_links</c> and <c>bank_rules</c> (bank sync D24, 5C): the accounts a
/// person reads and their booked lines, never a connection, a session or a balance; then what links those lines to the household's items, and
/// the exporter's own label rules. On import an account is the one its ref names (<c>acc-&lt;id&gt;</c> of this
/// installation, else an account imported before under that ref) when the importer may write it, left as it is; else a new one under
/// the importer's <c>file</c> connection, associated by 5A's rules (<see cref="BankAccountRules"/>). Lines dedupe as a statement's do
/// (<see cref="ITransactionStore.AddAsync"/>). A link or a rule whose line, target or account the importer cannot use is skipped, counted as
/// unchanged; the others are the importer's decisions. Runs after the investments sections: an envelope id resolves through <see cref="ImportContext.Items"/>.
/// </summary>
public sealed class BankImportSection(
    BankingAccess access, IAccountStore accounts, IConnectionStore connections, ITransactionStore transactions, IEnvelopeStore envelopes,
    IAccessPolicy policy, IHouseholdCalendar calendar, ILinkStore links, IRuleStore rules) : IImportSection
{
    public const string AccountsSection = "bank_accounts";
    public const string TransactionsSection = "bank_transactions";
    public const string LinksSection = "bank_links";
    public const string RulesSection = "bank_rules";
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

    /// <summary>A link or rule of the file that the importer may apply: its account, the item or counterpart it names, as the file says.</summary>
    private sealed record LinkPlan(BankLinkRecord Record, Plan Account, Plan? Counterpart, LinkOutcome Outcome, Money? Expected);

    private sealed record RulePlan(BankRuleRecord Record, Plan Account);

    /// <summary>The sections that hold an item a line can pay, in the order a ref is looked up (an exported ref names one of them: <c>sub-…</c>, a property's, a loan's).</summary>
    private static readonly (string Section, string Module)[] Targets = [("subscriptions", "subscriptions"), ("properties", "realestate"), ("loans", "realestate")];

    private int unwritableLinks, unwritableRules; // those whose target the importer cannot change, counted as skipped
    private readonly List<LinkPlan> linkPlans = [];
    private readonly List<RulePlan> rulePlans = [];
    private readonly Dictionary<string, Plan> plans = new(StringComparer.Ordinal);
    private IReadOnlyDictionary<string, SectionCounts> counts = new Dictionary<string, SectionCounts>();

    public string Module => "banking";
    public IReadOnlyList<string> Sections => [AccountsSection, TransactionsSection, LinksSection, RulesSection];
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
            else if (section == LinksSection)
            {
                if (NidDataJson.TryRead(entity, BankFileJson.Records.BankLinkRecord, path, issues, out var link))
                    TryLink(link, path, issues, out _, out _);
            }
            else if (section == RulesSection)
            {
                if (NidDataJson.TryRead(entity, BankFileJson.Records.BankRuleRecord, path, issues, out var rule))
                    TryRule(rule, path, issues);
            }
            else if (NidDataJson.TryRead(entity, BankFileJson.Records.BankTransactionRecord, path, issues, out var line))
                TryLine(line, path, issues, out _);
        }
        return issues;
    }

    public async Task<SectionOutcome> ValidateAllAsync(ImportContext context, IStagedBatches staged, CancellationToken ct)
    {
        plans.Clear();
        linkPlans.Clear();
        unwritableLinks = unwritableRules = 0;
        rulePlans.Clear();
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

        await PlanLinksAsync(context, staged, plans, failed, issues, ct);

        var known = 0;
        foreach (var plan in plans.Values.Where(p => p.Found is not null))
            known += await KnownAsync(plan.Id, plan.Lines, ct);
        var lines = plans.Values.Sum(p => p.Lines.Count);
        var existing = plans.Values.Count(p => p.Found is not null);
        counts = new Dictionary<string, SectionCounts>
        {
            [AccountsSection] = new(plans.Count - existing, existing, 0),
            [TransactionsSection] = new(lines - known, known, 0),
            [LinksSection] = SectionCounts.Zero,
            [RulesSection] = SectionCounts.Zero,
        };
        var (applicable, skipped) = await LinksAsync(true, ct);
        var (rulesCreated, rulesKnown) = await RuleCountsAsync(context, ct);
        counts = new Dictionary<string, SectionCounts>(counts)
        {
            [LinksSection] = new(applicable.Count, skipped, 0),
            [RulesSection] = new(rulesCreated, rulesKnown, 0),
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
            var written = await transactions.AddAsync(plan.Id, plan.Lines, ct, reconciled: true); // D24: an archive's lines ask nobody
            (added, known) = (added + written.Added, known + written.Known);
        }

        var (toLink, notLinked) = await LinksAsync(false, ct);
        foreach (var (plan, line) in toLink)
        {
            // The item's real id: one created by this import was planned at validation.
            (string, ItemId)? target = null;
            if (plan.Record.TargetRef is { } targetRef)
            {
                if (!Target(context, targetRef, "", [], out var resolved))
                    return Moved(plan.Record.TargetRef, LinksSection);
                target = resolved;
            }
            // A rent's flow:{id} key names a flow of the source instance, which has another id here: left out.
            var key = plan.Record.TargetKey?.StartsWith("flow:", StringComparison.Ordinal) == true ? null : plan.Record.TargetKey;
            var link = new BankLink(
                line!.Id, target?.Item1, target?.Item2, key, plan.Counterpart?.Id, plan.Outcome, plan.Expected,
                $"person:{context.Actor.Value:D}", InstantPattern.ExtendedIso.Parse(plan.Record.DecidedAt).Value);
            await links.SetAsync(link, plan.Counterpart is null ? TransactionLinkState.Linked : TransactionLinkState.Internal, ct);
        }
        var (rulesCreated, rulesKnown) = await RuleCountsAsync(context, ct);
        foreach (var rule in rulePlans)
        {
            if (!Target(context, rule.Record.TargetRef, "", [], out var target))
                return Moved(rule.Record.TargetRef, RulesSection);
            await rules.UpsertAsync(rule.Account.Id, rule.Record.LabelStem, target.Module, target.Item, context.Actor, ct);
        }
        return Result.Success<IReadOnlyDictionary<string, SectionCounts>>(new Dictionary<string, SectionCounts>
        {
            [AccountsSection] = counts[AccountsSection],
            [TransactionsSection] = new(added, known, 0),
            [LinksSection] = new(toLink.Count, notLinked, 0),
            [RulesSection] = new(rulesCreated, rulesKnown, 0),
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
        var (linkRecords, ruleRecords) = await LinkExportAsync(context, lines, refs, position, ct);
        return new Dictionary<string, IReadOnlyList<JsonElement>>
        {
            [AccountsSection] = accountRecords.Select(r => JsonSerializer.SerializeToElement(r, BankFileJson.Records.BankAccountRecord)).ToList(),
            [TransactionsSection] = lineRecords.Select(r => JsonSerializer.SerializeToElement(r, BankFileJson.Records.BankTransactionRecord)).ToList(),
            [LinksSection] = linkRecords.Select(r => JsonSerializer.SerializeToElement(r, BankFileJson.Records.BankLinkRecord)).ToList(),
            [RulesSection] = ruleRecords.Select(r => JsonSerializer.SerializeToElement(r, BankFileJson.Records.BankRuleRecord)).ToList(),
        };
    }

    /// <summary>
    /// The exporter's links: those of a line of an account they read, to an item the earlier sections exported (so they read it) or to a
    /// counterpart account they read; then the rules they created on such accounts and items. Else the link or rule is left out, never named.
    /// </summary>
    private async Task<(List<BankLinkRecord> Links, List<BankRuleRecord> Rules)> LinkExportAsync(
        ExportContext context, IReadOnlyList<BankTransaction> lines, Dictionary<Guid, string> refs, Dictionary<Guid, int> position, CancellationToken ct)
    {
        IReadOnlyDictionary<Guid, BankLink> linked = lines.Count == 0 ? new Dictionary<Guid, BankLink>() : await links.ForTransactionsAsync(lines.Select(l => l.Id).ToList(), ct);
        var linkRecords = new List<BankLinkRecord>();
        foreach (var line in lines.OrderBy(t => position[t.Account]).ThenBy(t => t.BookedOn).ThenBy(t => t.Id))
        {
            if (!linked.TryGetValue(line.Id, out var link))
                continue;
            string? target = null, counterpart = null;
            if (link.TargetItem is { } item ? !context.ItemFileIds.TryGetValue(item, out target) : link.CounterpartAccount is not { } other || !refs.TryGetValue(other, out counterpart))
                continue;
            linkRecords.Add(new BankLinkRecord
            {
                AccountRef = refs[line.Account], ProviderRef = line.ProviderRef, Outcome = BankLines.Wire(link.Outcome), TargetRef = target, TargetKey = link.TargetKey,
                CounterpartRef = counterpart, ExpectedAmount = link.Expected?.Amount, ExpectedCurrency = link.Expected?.Currency.Code,
                DecidedAt = InstantPattern.ExtendedIso.Format(link.DecidedAt),
            });
        }
        var ruleRecords = (await rules.ByAsync(context.Reader, ct))
            .Where(r => refs.ContainsKey(r.Account) && context.ItemFileIds.ContainsKey(r.TargetItem))
            .OrderBy(r => position[r.Account]).ThenBy(r => r.LabelStem, StringComparer.Ordinal)
            .Select(r => new BankRuleRecord
            {
                AccountRef = refs[r.Account], LabelStem = r.LabelStem, TargetRef = context.ItemFileIds[r.TargetItem], CreatedAt = InstantPattern.ExtendedIso.Format(r.CreatedAt),
            }).ToList();
        return (linkRecords, ruleRecords);
    }

    /// <summary>The links and rules of the file the importer may apply, as <see cref="linkPlans"/> and <see cref="rulePlans"/>; the others are reported or skipped.</summary>
    private async Task PlanLinksAsync(
        ImportContext context, IStagedBatches staged, Dictionary<string, Plan> accountPlans, HashSet<string> failed, List<ImportIssue> issues, CancellationToken ct)
    {
        var lines = new HashSet<(string, string)>();
        foreach (var entity in staged.Section(LinksSection))
        {
            var path = NidDataJson.Path(LinksSection, entity.Index);
            if (!NidDataJson.TryRead(entity.Value, BankFileJson.Records.BankLinkRecord, path, issues, out var record)
                || !TryLink(record, path, issues, out var outcome, out var expected) || failed.Contains(record.AccountRef))
                continue;
            if (!lines.Add((record.AccountRef, record.ProviderRef)))
            {
                issues.Add(ImportIssue.Error(ImportCodes.DuplicateId, $"A link of '{record.ProviderRef}' appears twice in {LinksSection}.", path + ".provider_ref"));
                continue;
            }
            if (!Account(accountPlans, record.AccountRef, failed, path + ".account_ref", issues, out var account))
                continue;
            Plan? counterpart = null;
            if (record.CounterpartRef is { } counterpartRef && !Account(accountPlans, counterpartRef, failed, path + ".counterpart_ref", issues, out counterpart))
                continue;
            if (record.TargetRef is { } targetRef)
            {
                if (!Target(context, targetRef, path + ".target_ref", issues, out var resolved))
                    continue;
                if (!await Writable(context, resolved.Item, ct))
                {
                    unwritableLinks++;
                    continue;
                }
            }
            linkPlans.Add(new LinkPlan(record, account!, counterpart, outcome, expected));
        }

        var stems = new HashSet<(string, string)>();
        foreach (var entity in staged.Section(RulesSection))
        {
            var path = NidDataJson.Path(RulesSection, entity.Index);
            if (!NidDataJson.TryRead(entity.Value, BankFileJson.Records.BankRuleRecord, path, issues, out var record)
                || !TryRule(record, path, issues) || failed.Contains(record.AccountRef))
                continue;
            if (!stems.Add((record.AccountRef, record.LabelStem)))
            {
                issues.Add(ImportIssue.Error(ImportCodes.DuplicateId, $"A rule of '{record.LabelStem}' appears twice in {RulesSection}.", path + ".label_stem"));
                continue;
            }
            if (!Account(accountPlans, record.AccountRef, failed, path + ".account_ref", issues, out var account)
                || !Target(context, record.TargetRef, path + ".target_ref", issues, out var resolved))
                continue;
            if (await Writable(context, resolved.Item, ct))
                rulePlans.Add(new RulePlan(record, account!));
            else
                unwritableRules++;
        }
    }

    private static Result<IReadOnlyDictionary<string, SectionCounts>> Moved(string fileRef, string section)
        => ImportSectionKit.Drift([ImportIssue.Error(ImportCodes.UnresolvedReference, $"'{fileRef}' no longer names one item.", section)]);

    private static bool Account(Dictionary<string, Plan> accountPlans, string fileRef, HashSet<string> failed, string path, List<ImportIssue> issues, out Plan? plan)
    {
        if (accountPlans.TryGetValue(fileRef, out plan))
            return true;
        if (!failed.Contains(fileRef)) // else reported with its account
            issues.Add(ImportIssue.Error(ImportCodes.UnresolvedReference, $"'{fileRef}' names no entity of {AccountsSection}.", path));
        return false;
    }

    /// <summary>
    /// The one item a file id names among the sections an earlier module imported (ids are unique per section only, so two matches are refused);
    /// false when refused there (not reported again), unknown or ambiguous (reported).
    /// </summary>
    private static bool Target(ImportContext context, string fileRef, string path, List<ImportIssue> issues, out (string Module, ItemId Item) target)
    {
        var matches = Targets.Where(t => context.Items.ContainsKey((t.Section, fileRef))).ToList();
        if (matches.Count == 1)
        {
            target = (matches[0].Module, context.Items[(matches[0].Section, fileRef)]);
            return true;
        }
        target = default;
        if (matches.Count > 1)
            issues.Add(ImportIssue.Error(ImportCodes.UnresolvedReference, $"'{fileRef}' names two entities of subscriptions, properties and loans.", path));
        else if (!Targets.Any(t => context.FailedItems.Contains((t.Section, fileRef))))
            issues.Add(ImportIssue.Error(ImportCodes.UnresolvedReference, $"'{fileRef}' names no subscription, property or loan of this file.", path));
        return false;
    }

    /// <summary>Whether the importer may change the item (one this import creates is theirs); else the link or rule is skipped and counted.</summary>
    private async Task<bool> Writable(ImportContext context, ItemId item, CancellationToken ct)
        => context.NewItems.Contains(item) || await ImportSectionKit.CanAsync(policy, context.Actor, item, AccessKind.Write, ct);

    /// <summary>
    /// The planned links whose line is there and unlinked (a line of this file counts as there while validating, its row not being written yet),
    /// with that row once the lines are written; and how many links were skipped, the unwritable ones included.
    /// </summary>
    private async Task<(List<(LinkPlan Plan, BankTransaction? Line)> Ready, int Skipped)> LinksAsync(bool validating, CancellationToken ct)
    {
        var stored = new Dictionary<Plan, Dictionary<string, BankTransaction>>();
        var ready = new List<(LinkPlan, BankTransaction?)>();
        var skipped = unwritableLinks;
        foreach (var plan in linkPlans)
        {
            if (!stored.TryGetValue(plan.Account, out var held))
                stored[plan.Account] = held = plan.Account.Id == Guid.Empty ? []
                    : (await transactions.ListAsync([plan.Account.Id], null, null, 0, int.MaxValue, ct)).ToDictionary(t => t.ProviderRef, StringComparer.Ordinal);
            var line = held.GetValueOrDefault(plan.Record.ProviderRef);
            if (line is not null ? line.LinkState == TransactionLinkState.Unlinked : validating && plan.Account.Lines.Any(l => l.ProviderRef == plan.Record.ProviderRef))
                ready.Add((plan, line));
            else
                skipped++;
        }
        return (ready, skipped);
    }

    /// <summary>The planned rules the importer has not got already (same account, stem and target), and those it has.</summary>
    private async Task<(int Created, int Known)> RuleCountsAsync(ImportContext context, CancellationToken ct)
    {
        IReadOnlyList<BankRule> own = rulePlans.Count == 0 ? [] : await rules.ByAsync(context.Actor, ct);
        var known = rulePlans.Count(p => Target(context, p.Record.TargetRef, "", [], out var target)
            && own.Any(r => r.Account == p.Account.Id && r.LabelStem == p.Record.LabelStem && r.TargetItem == target.Item));
        return (rulePlans.Count - known, known + unwritableRules);
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

    /// <summary>The rules local to a link record: its outcome, what it points at (an item or a counterpart account), its expected amount and its date.</summary>
    private static bool TryLink(BankLinkRecord r, string path, List<ImportIssue> issues, out LinkOutcome outcome, out Money? expected)
    {
        var ok = true;
        expected = null;
        var found = Enum.GetValues<LinkOutcome>().Select(o => (LinkOutcome?)o).FirstOrDefault(o => BankLines.Wire(o!.Value) == r.Outcome);
        outcome = found ?? default;
        if (found is null)
            ok = Error(issues, ImportCodes.InvalidValue, "An outcome is as_expected, price_differs, income, contribution, internal_transfer or card_settlement.", path + ".outcome");
        if (string.IsNullOrWhiteSpace(r.ProviderRef))
            ok = Error(issues, ImportCodes.InvalidValue, "A link names its line by the source's ref.", path + ".provider_ref");
        var internalOutcome = outcome is LinkOutcome.InternalTransfer or LinkOutcome.CardSettlement;
        if (internalOutcome ? r.CounterpartRef is null || r.TargetRef is not null : r.TargetRef is null || r.CounterpartRef is not null)
            ok = Error(issues, ImportCodes.InvalidValue, internalOutcome ? "A transfer or card settlement names its counterpart account, not an item." : "A link names its item, not a counterpart account.",
                path + (internalOutcome ? ".counterpart_ref" : ".target_ref"));
        if (r.TargetKey is { Length: 0 or > 200 })
            ok = Error(issues, ImportCodes.InvalidValue, "A charge's key has 1 to 200 characters.", path + ".target_key");
        if (r.ExpectedAmount is { } amount)
        {
            if (!Currency.TryCreate(r.ExpectedCurrency, out var currency) || !Money.Create(amount, currency).TryGetValue(out var money))
                ok = Error(issues, ImportCodes.InvalidValue, "An expected amount has an ISO 4217 currency and at most 4 decimals.", path + ".expected_amount");
            else
                expected = money;
        }
        else if (r.ExpectedCurrency is not null)
            ok = Error(issues, ImportCodes.InvalidValue, "An expected currency goes with an expected amount.", path + ".expected_currency");
        if (!InstantPattern.ExtendedIso.Parse(r.DecidedAt).Success)
            ok = Error(issues, ImportCodes.InvalidValue, "A date and time are written YYYY-MM-DDThh:mm:ssZ.", path + ".decided_at");
        return ok;
    }

    private static bool TryRule(BankRuleRecord r, string path, List<ImportIssue> issues)
        => !string.IsNullOrWhiteSpace(r.LabelStem) || Error(issues, ImportCodes.InvalidValue, "A rule has a label stem.", path + ".label_stem");

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
