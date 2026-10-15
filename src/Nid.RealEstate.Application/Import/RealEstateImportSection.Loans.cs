using System.Globalization;
using System.Text.Json;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.RealEstate.Contracts;
using Nid.RealEstate.Domain;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;

namespace Nid.RealEstate.Application.Import;

public sealed partial class RealEstateImportSection
{
    public const string LoansSection = "loans";
    public const string PhasesSection = "loan_phases";
    public const string InsurancesSection = "loan_insurances";
    public const string FeesSection = "loan_fees";
    public const string EventsSection = "loan_events";
    public const string BankScheduleSection = "loan_bank_schedule";

    /// <summary>A loan of the file; <see cref="Contract"/> is what validation built and the engine accepted, written at apply.</summary>
    private sealed class LoanPlan(StagedEntity entity, LoanRecord record, ParsedLoan parsed, string path, PropertyPlan? property)
        : ItemPlan(entity, record.Id, path)
    {
        public LoanRecord Record { get; } = record;
        public ParsedLoan Parsed { get; } = parsed;
        public PropertyPlan? Property { get; } = property;
        public StoredLoan? Stored { get; set; }
        public LoanContract? Contract { get; set; }

        /// <summary>The phases and insurances are written again: the loan is new or replaced, or the file writes one of them.</summary>
        public bool RewritesContract { get; set; }

        /// <summary>Spec 3F2 §3: the bank's table as it will be stored, when the file writes one of its rows; null otherwise.</summary>
        public IReadOnlyList<StoredBankRow>? Bank { get; set; }
    }

    private readonly List<LoanPlan> loanPlans = [];
    private readonly List<RowPlan<LoanPhaseEntry>> phasePlans = [];
    private readonly List<RowPlan<LoanInsuranceEntry>> insurancePlans = [];
    private readonly List<RowPlan<StoredFee>> feePlans = [];
    private readonly List<RowPlan<StoredRepayment>> eventPlans = [];
    private readonly List<RowPlan<StoredBankRow>> bankPlans = [];

    private async Task ValidateLoansAsync(IStagedBatches staged, List<ImportIssue> issues, CancellationToken ct)
    {
        loanPlans.Clear();
        phasePlans.Clear();
        insurancePlans.Clear();
        feePlans.Clear();
        eventPlans.Clear();
        bankPlans.Clear();

        // Loans: each with its property (one of this file's, validated before), resolved and refused or planned before any part.
        var propertyByFileId = propertyPlans.ToDictionary(p => p.FileId, StringComparer.Ordinal);
        var records = ImportSectionKit.ReadAll<LoanRecord>(staged, LoansSection, r => r.Id, issues,
            (r, path, found) => NoNullOwners(r.Ownership, path, found), unread: id => context.FailedItems.Add((LoansSection, id)));
        var known = await imports.FindAsync(EstateTable.Loan, records.Select(r => r.Record.Id).ToList(), ct);
        foreach (var (entity, record) in records)
        {
            var path = NidDataJson.Path(LoansSection, entity.Index);
            PropertyPlan? property = null;
            if ((record.Property is { } propertyId && (property = Parent(propertyByFileId, PropertiesSection, propertyId, path + ".property", issues)) is null)
                || !EstateParsing.TryLoan(record, path, Person, issues, out var parsed) || !Owners(parsed.Visibility, parsed.Owners, path, issues))
            {
                context.FailedItems.Add((LoansSection, record.Id)); // its parts and facts are not reported again
                continue;
            }
            var plan = new LoanPlan(entity, record, parsed, path, property);
            if (await ResolveItemAsync(plan, LoansSection, "loan", known.GetValueOrDefault(record.Id), parsed.Visibility, parsed.Owners, parsed.Currency, issues, ct))
                loanPlans.Add(plan);
        }
        // Locked in id order before they are read, as the screens lock one (LoanCheck.LockAsync): at commit, no contract PUT, fee or
        // repayment lands between the whole-loan check and the apply. A plain validate has no transaction: each lock ends with its statement.
        var existing = loanPlans.Where(l => l.Mode != Mode.Created).Select(l => l.Id).OrderBy(i => i.Value).ToList();
        foreach (var id in existing)
            await loans.LockAsync(id, ct);
        var stored = await loans.FindManyAsync(existing, ct);
        foreach (var plan in loanPlans.Where(l => l.Mode != Mode.Created))
            plan.Stored = stored.Single(s => s.ItemId == plan.Id);
        var byFileId = loanPlans.ToDictionary(l => l.FileId, StringComparer.Ordinal);

        var phaseRecords = ImportSectionKit.ReadAll<LoanPhaseRecord>(staged, PhasesSection, r => r.Id, issues);
        var knownPhases = await imports.FindAsync(EstateTable.Phase, phaseRecords.Select(r => r.Record.Id).ToList(), ct);
        foreach (var (entity, record) in phaseRecords)
        {
            var path = NidDataJson.Path(PhasesSection, entity.Index);
            if (Parent(byFileId, LoansSection, record.Loan, path + ".loan", issues) is not { } loan
                || !EstateParsing.TryPhase(record, path, loan.Currency, issues, out var phase))
                continue;
            if (await ResolveRowAsync(PhasesSection, "phase", entity, record.Id, path, loan, "loan", knownPhases,
                    (loan.Stored?.Contract.Phases ?? []).Select(p => p.Id), issues, ct) is { } row)
                phasePlans.Add(new RowPlan<LoanPhaseEntry>(entity, record.Id, path, loan, row.Mode, row.Id, row.ImportedBy,
                    phase with { Import = new ImportMark(record.Id, entity.Hash, row.ImportedBy), Id = row.Id }));
        }

        var insuranceRecords = ImportSectionKit.ReadAll<LoanInsuranceRecord>(staged, InsurancesSection, r => r.Id, issues);
        var knownInsurances = await imports.FindAsync(EstateTable.Insurance, insuranceRecords.Select(r => r.Record.Id).ToList(), ct);
        foreach (var (entity, record) in insuranceRecords)
        {
            var path = NidDataJson.Path(InsurancesSection, entity.Index);
            if (Parent(byFileId, LoansSection, record.Loan, path + ".loan", issues) is not { } loan
                || !EstateParsing.TryInsurance(record, path, loan.Currency, issues, out var insurance))
                continue;
            if (await ResolveRowAsync(InsurancesSection, "insurance", entity, record.Id, path, loan, "loan", knownInsurances,
                    (loan.Stored?.Contract.Insurances ?? []).Select(i => i.Id), issues, ct) is { } row)
                insurancePlans.Add(new RowPlan<LoanInsuranceEntry>(entity, record.Id, path, loan, row.Mode, row.Id, row.ImportedBy,
                    insurance with { Import = new ImportMark(record.Id, entity.Hash, row.ImportedBy), Id = row.Id }));
        }

        var feeRecords = ImportSectionKit.ReadAll<LoanFeeRecord>(staged, FeesSection, r => r.Id, issues);
        var knownFees = await imports.FindAsync(EstateTable.Fee, feeRecords.Select(r => r.Record.Id).ToList(), ct);
        foreach (var (entity, record) in feeRecords)
        {
            var path = NidDataJson.Path(FeesSection, entity.Index);
            if (Parent(byFileId, LoansSection, record.Loan, path + ".loan", issues) is not { } loan
                || !EstateParsing.TryFee(record, path, loan.Currency, issues, out var fee))
                continue;
            if (await ResolveRowAsync(FeesSection, "fee", entity, record.Id, path, loan, "loan", knownFees,
                    (loan.Stored?.Fees ?? []).Select(f => f.Id), issues, ct) is { } row)
                feePlans.Add(new RowPlan<StoredFee>(entity, record.Id, path, loan, row.Mode, row.Id, row.ImportedBy, fee with { Id = row.Id }));
        }

        var eventRecords = ImportSectionKit.ReadAll<LoanEventRecord>(staged, EventsSection, r => r.Id, issues);
        var knownEvents = await imports.FindAsync(EstateTable.Event, eventRecords.Select(r => r.Record.Id).ToList(), ct);
        foreach (var (entity, record) in eventRecords)
        {
            var path = NidDataJson.Path(EventsSection, entity.Index);
            if (Parent(byFileId, LoansSection, record.Loan, path + ".loan", issues) is not { } loan
                || !EstateParsing.TryEvent(record, path, loan.Currency, issues, out var repayment))
                continue;
            if (await ResolveRowAsync(EventsSection, "early repayment", entity, record.Id, path, loan, "loan", knownEvents,
                    (loan.Stored?.Repayments ?? []).Select(r => r.Id), issues, ct) is { } row)
            {
                // A repayment replaced keeps the penalty the app gave it (precision 8).
                var penalty = loan.Stored?.Repayments.FirstOrDefault(r => r.Id == row.Id)?.FeeId;
                eventPlans.Add(new RowPlan<StoredRepayment>(entity, record.Id, path, loan, row.Mode, row.Id, row.ImportedBy,
                    repayment with { Id = row.Id, FeeId = penalty }));
            }
        }

        var bankRecords = ImportSectionKit.ReadAll<LoanBankScheduleRecord>(staged, BankScheduleSection, r => r.Id, issues);
        var knownBank = await imports.FindAsync(EstateTable.BankRow, bankRecords.Select(r => r.Record.Id).ToList(), ct);
        foreach (var (entity, record) in bankRecords)
        {
            var path = NidDataJson.Path(BankScheduleSection, entity.Index);
            if (Parent(byFileId, LoansSection, record.Loan, path + ".loan", issues) is not { } loan
                || !EstateParsing.TryBankRow(record, path, issues, out var row))
                continue;
            if (await ResolveRowAsync(BankScheduleSection, "bank row", entity, record.Id, path, loan, "loan", knownBank,
                    (loan.Stored?.Bank ?? []).Select(b => b.Id), issues, ct) is { } resolved)
                bankPlans.Add(new RowPlan<StoredBankRow>(entity, record.Id, path, loan, resolved.Mode, resolved.Id, resolved.ImportedBy,
                    new StoredBankRow(resolved.Id, row) { Import = new ImportMark(record.Id, entity.Hash, resolved.ImportedBy) }));
        }

        // Spec 3F1 §2, the screens' rules on each loan as it will be once applied: its terms, phases and insurances through
        // LoanRules.Contract, then the whole schedule through the engine with its fees and early repayments, then (spec 3F2 §3) its
        // bank table against it — the file's rows, or the stored ones it does not name, so a stored table always fits its loan
        // (precision 4) — and the reference it names. A loan nothing writes computes as stored.
        foreach (var loan in loanPlans.ToList())
        {
            var parts = phasePlans.Any(p => p.Parent == loan && p.Writes) || insurancePlans.Any(p => p.Parent == loan && p.Writes);
            var table = bankPlans.Any(p => p.Parent == loan && p.Writes);
            if (loan.Mode == Mode.Unchanged && !parts && !table
                && !feePlans.Any(p => p.Parent == loan && p.Writes) && !eventPlans.Any(p => p.Parent == loan && p.Writes))
                continue;
            var current = loan.Stored?.Contract;
            var phases = Resulting(current?.Phases ?? [], p => p.Id, phasePlans.Where(p => p.Parent == loan));
            var insurances = Resulting(current?.Insurances ?? [], i => i.Id, insurancePlans.Where(p => p.Parent == loan));
            var fees = Resulting(loan.Stored?.Fees ?? [], f => f.Id, feePlans.Where(p => p.Parent == loan));
            var events = Resulting(loan.Stored?.Repayments ?? [], r => r.Id, eventPlans.Where(p => p.Parent == loan))
                .OrderBy(e => e.Value.Repayment.OnDate).ThenBy(e => e.Value.Id).ToList(); // the engine's order (LoanReading.Terms)
            var bank = Resulting(loan.Stored?.Bank ?? [], b => b.Id, bankPlans.Where(p => p.Parent == loan))
                .OrderBy(b => b.Value.Row.DueDate).ToList(); // a table is read by date (spec 3F2 §2)
            var terms = loan.Mode == Mode.Unchanged ? current! : loan.Parsed.Terms;
            var contract = terms with { Phases = phases.Select(p => p.Value).ToList(), Insurances = insurances.Select(i => i.Value).ToList() };
            var source = loan.Mode == Mode.Unchanged ? loan.Stored!.Source : loan.Parsed.Source;

            var refused = LoanRules.Contract(contract)
                ?? Computes(LoanReading.Terms(loan.Currency, contract, fees.Select(f => f.Value), events.Select(e => e.Value)),
                    bank.Select(b => b.Value.Row).ToList(), source);
            if (refused is not null)
            {
                issues.Add(ImportIssue.Error(refused.Code, refused.Message, EstateParsing.PathOf(refused, loan.Path,
                    phases.Select(p => p.Path).ToList(), insurances.Select(i => i.Path).ToList(),
                    fees.Select(f => f.Path).ToList(), events.Select(e => e.Path).ToList(), bank.Select(b => b.Path).ToList())));
                FailLoan(loan);
                continue;
            }
            loan.Contract = contract;
            loan.RewritesContract = loan.Mode != Mode.Unchanged || parts;
            loan.Bank = table ? bank.Select(b => b.Value).ToList() : null;
        }

        foreach (var plan in loanPlans)
            Register(plan, LoansSection);
        foreach (var plan in phasePlans)
            Tally(PhasesSection, Of(plan.Mode));
        foreach (var plan in insurancePlans)
            Tally(InsurancesSection, Of(plan.Mode));
        foreach (var plan in feePlans)
            Tally(FeesSection, Of(plan.Mode));
        foreach (var plan in eventPlans)
            Tally(EventsSection, Of(plan.Mode));
        foreach (var plan in bankPlans)
            Tally(BankScheduleSection, Of(plan.Mode));
    }

    /// <summary>
    /// What a loan holds once applied, each part with the file entity that brings or names it (null: stored, not in the file): its
    /// stored parts, each the file replaces as the file has it, then the file's new ones.
    /// </summary>
    private static List<(T Value, string? Path)> Resulting<T>(IEnumerable<T> stored, Func<T, Guid> id, IEnumerable<RowPlan<T>> plans)
    {
        var mine = plans.ToList();
        var named = mine.Where(p => p.Mode != Mode.Created).ToDictionary(p => p.Id);
        var result = stored
            .Select(s => named.TryGetValue(id(s), out var p) ? (p.Mode == Mode.Replaced ? p.Value : s, (string?)p.Path) : (s, (string?)null))
            .ToList();
        result.AddRange(mine.Where(p => p.Mode == Mode.Created).Select(p => (p.Value, (string?)p.Path)));
        return result;
    }

    /// <summary>
    /// The engine on the whole loan, then (spec 3F2 §3) its bank table against the schedule, then the reference the file names:
    /// the first refusal, or null. The bank as the reference needs rows once applied (the file's, or stored ones it does not name).
    /// </summary>
    private static Failure? Computes(LoanTerms terms, IReadOnlyList<BankRow> bank, ScheduleSource source)
    {
        var built = LoanScheduleBuilder.Build(terms);
        if (built.TryGetFailure(out var failure))
            return failure;
        built.TryGetValue(out var computed);
        if (bank.Count > 0 && BankSchedule.Merge(terms, computed!, bank).TryGetFailure(out failure))
            return failure;
        return source == ScheduleSource.Bank && bank.Count == 0
            ? new Failure(LoanCodes.BankScheduleMissing, "The bank is the reference only with its table: bring its loan_bank_schedule rows, or set computed.",
                new Dictionary<string, object?> { ["field"] = "scheduleSource" })
            : null;
    }

    private void FailLoan(LoanPlan loan)
    {
        context.FailedItems.Add((LoansSection, loan.FileId));
        loanPlans.Remove(loan);
        phasePlans.RemoveAll(p => p.Parent == loan);
        insurancePlans.RemoveAll(p => p.Parent == loan);
        feePlans.RemoveAll(p => p.Parent == loan);
        eventPlans.RemoveAll(p => p.Parent == loan);
        bankPlans.RemoveAll(p => p.Parent == loan);
    }

    /// <summary>After the properties: a loan links to its property's real id.</summary>
    private async Task<Result> ApplyLoansAsync(CancellationToken ct)
    {
        foreach (var plan in loanPlans.Where(l => l.Mode == Mode.Created))
        {
            var p = plan.Parsed;
            var made = await items.CreateAsync(new NewItem("realestate", "loan", plan.Record.Name, p.Visibility, p.Currency.Code, OwnerShares(p.Owners)), ct);
            if (made.TryGetFailure(out var failure))
                return Result.Failure(failure!);
            made.TryGetValue(out var id);
            Applied(plan, id, LoansSection);
            await loans.CreateAsync(id, plan.Record.Lender, plan.Record.Note, plan.Property?.Id, plan.Contract!, ct);
            if (p.Source == ScheduleSource.Bank) // its table is written below, in the same transaction
                await loans.UpdateDetailsAsync(id, new LoanDetailsChange(Source: p.Source), ct);
            await imports.MarkAsync(EstateTable.Loan, id.Value, plan.Mark, ct);
        }

        foreach (var plan in loanPlans.Where(l => l.Mode == Mode.Replaced))
        {
            var (p, r) = (plan.Parsed, plan.Record);
            var replaced = await items.ReplaceAsync(plan.Id, r.Name, p.Visibility, OwnerShares(p.Owners), ct);
            if (!replaced.IsSuccess)
                return replaced;
            await loans.UpdateDetailsAsync(plan.Id, new LoanDetailsChange(r.Lender, r.Lender is null, r.Note, r.Note is null,
                plan.Property?.Id, plan.Property is null, p.Source), ct);
            await imports.MarkAsync(EstateTable.Loan, plan.Id.Value, plan.Mark, ct);
        }

        // The phases and insurances with their file ids (LoanPhaseEntry.Import), a replaced loan's terms with them.
        foreach (var plan in loanPlans.Where(l => l.Mode != Mode.Created && l.RewritesContract))
            await loans.ReplaceContractAsync(plan.Id, plan.Contract!, ct);

        // Spec 3F2 §3: a table the file writes a row of is stored whole, each row with its file id (StoredBankRow.Import).
        foreach (var plan in loanPlans.Where(l => l.Bank is not null))
            await loans.ReplaceBankScheduleAsync(plan.Id, plan.Bank!, ct);

        foreach (var plan in feePlans.Where(f => f.Mode == Mode.Replaced))
            await loans.UpdateFeeAsync(plan.Parent.Id, plan.Id, plan.Value.Fee, plan.Value.Note, ct);
        foreach (var plan in feePlans.Where(f => f.Mode == Mode.Created))
            await loans.AddFeeAsync(plan.Parent.Id, plan.Id, plan.Value.Fee, plan.Value.Note, ct);
        foreach (var plan in eventPlans.Where(e => e.Mode == Mode.Replaced))
            await loans.UpdateRepaymentAsync(plan.Parent.Id, plan.Id, plan.Value.Repayment, plan.Value.FeeId, plan.Value.Note, ct);
        foreach (var plan in eventPlans.Where(e => e.Mode == Mode.Created))
            await loans.AddRepaymentAsync(plan.Parent.Id, plan.Id, plan.Value.Repayment, null, plan.Value.Note, ct);
        foreach (var plan in feePlans.Where(f => f.Writes))
            await imports.MarkAsync(EstateTable.Fee, plan.Id, plan.Mark, ct);
        foreach (var plan in eventPlans.Where(e => e.Writes))
            await imports.MarkAsync(EstateTable.Event, plan.Id, plan.Mark, ct);
        return Result.Success();
    }

    /// <summary>
    /// The readable loans and their parts. Spec 3F1 §4: a loan names its property only when the reader exports it
    /// (<paramref name="propertyFileIds"/>); one the reader cannot read is not in the file, and nothing here names it.
    /// </summary>
    private async Task ExportLoansAsync(ExportContext export, IReadOnlyCollection<ItemId> ids, IReadOnlyDictionary<ItemId, ItemAccess> heads,
        IReadOnlyDictionary<Guid, string> propertyFileIds, Dictionary<string, IReadOnlyList<JsonElement>> sections, CancellationToken ct)
    {
        var stored = (await loans.FindManyAsync(ids, ct)).Where(l => heads.ContainsKey(l.ItemId)).ToList(); // only the items that are loans
        var exported = stored.Select(l => l.ItemId).ToList();
        var marks = await imports.MarksAsync(EstateTable.Loan, exported, ct);
        var fileIds = FileIds.Assign("loan", stored.Select(l => Keyed(l.ItemId.Value, marks)).ToList(), export.Reader);
        var currencies = stored.ToDictionary(l => l.ItemId.Value, l => heads[l.ItemId].Currency!);

        var records = new List<LoanRecord>();
        foreach (var l in stored.OrderBy(l => heads[l.ItemId].DisplayName, StringComparer.Ordinal).ThenBy(l => fileIds[l.ItemId.Value], StringComparer.Ordinal))
        {
            var head = heads[l.ItemId];
            var id = fileIds[l.ItemId.Value];
            var currency = currencies[l.ItemId.Value];
            var c = l.Contract;
            export.Expose(l.ItemId);
            export.NameItem(l.ItemId, id);
            records.Add(new LoanRecord
            {
                Id = id, Name = head.DisplayName, Lender = l.Lender, Currency = currency, Principal = Money(c.Principal, currency),
                DisbursedOn = Iso(c.DisbursedOn), FirstPaymentDate = Iso(c.FirstPaymentDate), TotalPayments = c.TotalPayments,
                Property = l.PropertyId is { } linked && propertyFileIds.TryGetValue(linked.Value, out var propertyId) ? propertyId : null,
                ScheduleSource = LoanWire.Of(l.Source), RoundingMode = LoanWire.Of(c.Rounding), FirstPeriodMode = LoanWire.Of(c.FirstPeriod),
                Ownership = Shares(export, head), Visibility = LoanViews.Wire(head.Visibility), Note = l.Note,
            });
        }
        sections[LoansSection] = Elements(records);

        var phases = stored.SelectMany(l => l.Contract.Phases.Select(p => (Loan: l.ItemId.Value, Entry: p))).ToList();
        var phaseIds = FileIds.Assign("lp", phases.Select(p => new KeyedRow(p.Entry.Id, p.Entry.Import?.Ref, p.Entry.Import?.ImportedBy)).ToList(), export.Reader);
        sections[PhasesSection] = Elements(phases
            .Select(p => new LoanPhaseRecord
            {
                Id = phaseIds[p.Entry.Id], Loan = fileIds[p.Loan], Seq = p.Entry.Phase.Seq, Mode = LoanWire.Of(p.Entry.Phase.Mode),
                AnnualRate = Rate(p.Entry.Phase.AnnualRate), RateType = LoanWire.Of(p.Entry.Phase.RateType), Payments = p.Entry.Phase.Payments,
                PaymentOverride = Money(p.Entry.Phase.PaymentOverride, currencies[p.Loan]),
                OpeningBalanceOverride = Money(p.Entry.Phase.OpeningBalanceOverride, currencies[p.Loan]), Note = p.Entry.Note,
            })
            .OrderBy(r => r.Loan, StringComparer.Ordinal).ThenBy(r => r.Seq));

        var insurances = stored.SelectMany(l => l.Contract.Insurances.Select(i => (Loan: l.ItemId.Value, Entry: i))).ToList();
        var insuranceIds = FileIds.Assign("li", insurances.Select(i => new KeyedRow(i.Entry.Id, i.Entry.Import?.Ref, i.Entry.Import?.ImportedBy)).ToList(), export.Reader);
        sections[InsurancesSection] = Elements(insurances
            .Select(i => new LoanInsuranceRecord
            {
                Id = insuranceIds[i.Entry.Id], Loan = fileIds[i.Loan], Mode = LoanWire.Of(i.Entry.Insurance.Mode),
                MonthlyAmount = Money(i.Entry.Insurance.MonthlyAmount, currencies[i.Loan]), AnnualRate = Rate(i.Entry.Insurance.AnnualRate),
                StartsOn = Iso(i.Entry.Insurance.StartsOn), EndsOn = Iso(i.Entry.Insurance.EndsOn), Note = i.Entry.Note,
            })
            .OrderBy(r => r.Loan, StringComparer.Ordinal).ThenBy(r => r.StartsOn, StringComparer.Ordinal).ThenBy(r => r.Id, StringComparer.Ordinal));

        var feeMarks = await imports.MarksAsync(EstateTable.Fee, exported, ct);
        var fees = stored.SelectMany(l => l.Fees.Select(f => (Loan: l.ItemId.Value, Row: f))).ToList();
        var feeIds = FileIds.Assign("fee", fees.Select(f => Keyed(f.Row.Id, feeMarks)).ToList(), export.Reader);
        sections[FeesSection] = Elements(fees
            .Select(f => new LoanFeeRecord
            {
                Id = feeIds[f.Row.Id], Loan = fileIds[f.Loan], Kind = LoanWire.Of(f.Row.Fee.Kind), Amount = Money(f.Row.Fee.Amount, currencies[f.Loan]),
                OnDate = Iso(f.Row.Fee.OnDate), Note = f.Row.Note,
            })
            .OrderBy(r => r.Loan, StringComparer.Ordinal).ThenBy(r => r.OnDate, StringComparer.Ordinal).ThenBy(r => r.Id, StringComparer.Ordinal));

        var eventMarks = await imports.MarksAsync(EstateTable.Event, exported, ct);
        var events = stored.SelectMany(l => l.Repayments.Select(r => (Loan: l.ItemId.Value, Row: r))).ToList();
        var eventIds = FileIds.Assign("ev", events.Select(e => Keyed(e.Row.Id, eventMarks)).ToList(), export.Reader);
        sections[EventsSection] = Elements(events
            .Select(e => new LoanEventRecord
            {
                Id = eventIds[e.Row.Id], Loan = fileIds[e.Loan], OnDate = Iso(e.Row.Repayment.OnDate),
                Amount = Money(e.Row.Repayment.Amount, currencies[e.Loan]), Effect = LoanWire.Of(e.Row.Repayment.Effect), Note = e.Row.Note,
            })
            .OrderBy(r => r.Loan, StringComparer.Ordinal).ThenBy(r => r.OnDate, StringComparer.Ordinal).ThenBy(r => r.Id, StringComparer.Ordinal));

        var rows = stored.SelectMany(l => l.Bank.Select(b => (Loan: l.ItemId.Value, Entry: b))).ToList();
        var rowIds = FileIds.Assign("lbs", rows.Select(r => new KeyedRow(r.Entry.Id, r.Entry.Import?.Ref, r.Entry.Import?.ImportedBy)).ToList(), export.Reader);
        sections[BankScheduleSection] = Elements(rows
            .Select(r => new LoanBankScheduleRecord
            {
                Id = rowIds[r.Entry.Id], Loan = fileIds[r.Loan], DueDate = Iso(r.Entry.Row.DueDate), Payment = Cents(r.Entry.Row.Payment),
                Interest = Cents(r.Entry.Row.Interest), Principal = Cents(r.Entry.Row.Principal), Insurance = Cents(r.Entry.Row.Insurance),
                Crd = Cents(r.Entry.Row.Crd),
            })
            .OrderBy(r => r.Loan, StringComparer.Ordinal).ThenBy(r => r.DueDate, StringComparer.Ordinal));
    }

    /// <summary>A rate as the screens write it (<c>0.035</c>, <c>0</c>), whatever scale the column gives back.</summary>
    private static decimal Rate(decimal rate) => decimal.Parse(LoanReading.Rate(rate), CultureInfo.InvariantCulture);

    private static decimal? Rate(decimal? rate) => rate is { } value ? Rate(value) : null;

    /// <summary>A bank row's amount as the file writes it: two decimals, whatever scale the column gives back (spec 3F2 §3).</summary>
    private static decimal Cents(decimal amount) => decimal.Parse(LoanReading.Amount(amount), CultureInfo.InvariantCulture);

    private static decimal? Cents(decimal? amount) => amount is { } value ? Cents(value) : null;
}
