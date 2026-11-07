using System.Globalization;
using System.Text.Json;
using Nid.Childcare.Contracts;
using Nid.Childcare.Domain;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using NodaTime;
using NodaTime.Text;
using UnambitiousFx.Functional;

namespace Nid.Childcare.Application.Import;

/// <summary>
/// Childcare decisions 24–26: the sections <c>childcare_arrangements</c> (an arrangement, its dated terms and weeks without care) and
/// <c>childcare_statements</c> (its validated months, their figures re-computed from the file and compared). Runs after the goals and
/// before the bank, whose links may name an arrangement of the file.
/// </summary>
public sealed class ChildcareImportSection(
    IItemRegistry items, IChildcareStore store, IAccessPolicy policy, IItemRules rules, IHouseholdCalendar calendar, IVisibleItems visible,
    IPersonRegistry persons, IClock clock) : IImportSection
{
    public const string Arrangements = "childcare_arrangements";
    public const string Statements = "childcare_statements";

    private static readonly LocalTimePattern Time = LocalTimePattern.CreateWithInvariantCulture("HH:mm");
    private static readonly YearMonthPattern MonthPattern = YearMonthPattern.CreateWithInvariantCulture("uuuu-MM");

    private enum Mode { Created, Unchanged, Replaced }

    private sealed record Parsed(
        PersonId Child, PersonKind ChildKind, Visibility Visibility, IReadOnlyList<Owner> Owners, IReadOnlyList<Terms> Revisions, IReadOnlyList<CareGap> Gaps, IReadOnlyList<WeekOverride> Overrides, ShareLevel ShareLevel = ShareLevel.View);

    private sealed class Plan(StagedEntity entity, ArrangementRecord record, Parsed parsed, string path)
    {
        public StagedEntity Entity { get; } = entity;
        public ArrangementRecord Record { get; } = record;
        public Parsed Parsed { get; set; } = parsed;
        public string Path { get; } = path;
        public ItemId Id { get; set; }
        public Mode Mode { get; set; }
        public bool WasClosed { get; set; }
        public Arrangement? Stored { get; set; }
    }

    private sealed record StatementPlan(Plan Arrangement, Statement Statement, Mode Mode);

    private readonly List<Plan> plans = [];
    private readonly List<StatementPlan> statementPlans = [];
    private IReadOnlyDictionary<string, SectionCounts> counts = new Dictionary<string, SectionCounts>();

    public string Module => "childcare";
    public IReadOnlyList<string> Sections => [Arrangements, Statements];
    public int Order => 105;

    public IReadOnlyList<ImportIssue> ValidateBatch(string section, JsonElement entities, int firstIndex)
    {
        var issues = new List<ImportIssue>();
        var standIn = ImportSectionKit.StandIns();
        var position = 0;
        foreach (var entity in entities.EnumerateArray())
        {
            var path = NidDataJson.Path(section, firstIndex + position++);
            if (section == Arrangements)
            {
                if (NidDataJson.TryRead(entity, ChildcareFileJson.Records.ArrangementRecord, path, issues, out var record)
                    && ImportSectionKit.TryId(record.Id, path, issues) && NoNulls(record, path, issues))
                    TryParse(record, path, standIn, issues, out _);
            }
            else if (NidDataJson.TryRead(entity, ChildcareFileJson.Records.StatementRecord, path, issues, out var statement)
                     && ImportSectionKit.NoNulls(statement.Days, path + ".days", issues))
                TryParseStatement(statement, path, issues, out _, out _);
        }
        return issues;
    }

    public async Task<SectionOutcome> ValidateAllAsync(ImportContext context, IStagedBatches staged, CancellationToken ct)
    {
        plans.Clear();
        statementPlans.Clear();
        var issues = new List<ImportIssue>();
        var arrangementsTally = SectionCounts.Zero;
        var statementsTally = SectionCounts.Zero;
        var today = await calendar.TodayAsync(ct);
        var readable = new Dictionary<ItemId, bool>();
        var canRead = ImportSectionKit.Readable(policy, context.Actor, readable, ct);
        IReadOnlyList<PersonId>? adults = null;

        var records = ImportSectionKit.ReadAll(staged, Arrangements, ChildcareFileJson.Records.ArrangementRecord, r => r.Id, issues, NoNulls);
        var known = await store.FindImportedAsync(records.Select(r => r.Record.Id).ToList(), ct);
        foreach (var (entity, record) in records)
        {
            var path = NidDataJson.Path(Arrangements, entity.Index);
            if (!TryParse(record, path, r => context.Persons.GetValueOrDefault(r), issues, out var parsed) || !await ValidAsync(parsed, path, issues, ct))
            {
                context.FailedItems.Add((Arrangements, record.Id));
                continue;
            }
            var plan = new Plan(entity, record, parsed, path);
            var candidates = known.GetValueOrDefault(record.Id)?.Select(k => new ImportCandidate(k.Item.Value, k.Hash, k.ImportedBy, [k.Item])).ToList();
            var resolution = await ImportResolver.ResolveAsync(context.Actor, candidates, canRead);
            if (resolution.OwnHidden)
            {
                issues.Add(ImportIssue.Error(ImportCodes.NotPermitted, $"'{record.Id}' was imported before as an arrangement you cannot see.", path));
                context.FailedItems.Add((Arrangements, record.Id));
                continue;
            }
            if (resolution.Row is { } row)
            {
                plan.Id = new ItemId(row.Id);
                var current = (await items.FindAsync(plan.Id, ct))!;
                plan.Stored = await store.FindAsync(plan.Id, ct);
                plan.WasClosed = current.ArchivedAt is not null;
                if (row.Hash == entity.Hash)
                    plan.Mode = Mode.Unchanged;
                else if (!context.Replace)
                {
                    issues.Add(ImportIssue.Error(ImportCodes.Conflict,
                        $"'{record.Id}' was imported before with other content; nothing is overwritten (import-format.md §8).", path));
                    arrangementsTally += new SectionCounts(0, 0, 1);
                    context.FailedItems.Add((Arrangements, record.Id));
                    continue;
                }
                else
                {
                    var refusal = await ImportSectionKit.ReplaceRefusalAsync(policy, rules, context.Actor, plan.Id, current, parsed.Visibility,
                        parsed.ShareLevel, parsed.Owners, currency: null, "arrangement", path, async () => adults ??= await persons.AdultIdsAsync(ct), ct);
                    if (refusal is not null)
                    {
                        issues.Add(refusal);
                        context.FailedItems.Add((Arrangements, record.Id));
                        continue;
                    }
                    plan.Mode = Mode.Replaced;
                }
            }
            else
            {
                if (!rules.CanCreate(context.Actor, parsed.Owners))
                {
                    issues.Add(ImportIssue.Error(ImportCodes.NotPermitted, "An item is imported by one of its owners, unless every owner is a dependent.", path));
                    context.FailedItems.Add((Arrangements, record.Id));
                    continue;
                }
                plan.Id = ItemId.New(); // planned; ApplyAsync replaces it with the real one
                plan.Mode = Mode.Created;
                context.NewItems.Add(plan.Id); // writable by construction: the bank's links may name it
            }
            // The planned revisions carry the item id the statements are computed against.
            plan.Parsed = parsed with { Revisions = parsed.Revisions.Select(t => t with { ItemId = plan.Id }).ToList() };
            plans.Add(plan);
        }

        foreach (var plan in plans)
        {
            arrangementsTally += plan.Mode switch
            {
                Mode.Created => new SectionCounts(1, 0, 0),
                Mode.Unchanged => new SectionCounts(0, 1, 0),
                _ => new SectionCounts(0, 0, 0, 1),
            };
            context.Items[(Arrangements, plan.Record.Id)] = plan.Id;
        }

        statementsTally = await ValidateStatementsAsync(context, staged, today, issues, ct);
        counts = new Dictionary<string, SectionCounts> { [Arrangements] = arrangementsTally, [Statements] = statementsTally };
        return new SectionOutcome(issues, counts);
    }

    /// <summary>
    /// Decision 25: each arrangement's statements in month order, each re-computed from the file (and, for an arrangement already stored,
    /// its stored months) and compared with the file's figures.
    /// </summary>
    private async Task<SectionCounts> ValidateStatementsAsync(ImportContext context, IStagedBatches staged, LocalDate today, List<ImportIssue> issues, CancellationToken ct)
    {
        var tally = SectionCounts.Zero;
        var entries = new List<(StagedEntity Entity, StatementRecord Record, string Path, YearMonth Month, IReadOnlyList<StatementDay> Days)>();
        foreach (var entity in staged.Section(Statements))
        {
            var path = NidDataJson.Path(Statements, entity.Index);
            if (!NidDataJson.TryRead(entity.Value, ChildcareFileJson.Records.StatementRecord, path, issues, out var record)
                || !TryParseStatement(record, path, issues, out var month, out var days))
                continue;
            if (context.FailedItems.Contains((Arrangements, record.Arrangement)))
                continue; // reported with its arrangement
            if (plans.All(p => p.Record.Id != record.Arrangement))
            {
                issues.Add(ImportIssue.Error(ChildcareCodes.UnknownArrangement, $"'{record.Arrangement}' names no entity of {Arrangements}.", path + ".arrangement"));
                continue;
            }
            if (entries.Any(e => e.Record.Arrangement == record.Arrangement && e.Month == month))
            {
                issues.Add(ImportIssue.Error(ChildcareCodes.StatementTwice, "One statement per arrangement and month.", path + ".month"));
                continue;
            }
            entries.Add((entity, record, path, month, days));
        }

        var byArrangement = entries.ToLookup(e => e.Record.Arrangement);
        foreach (var plan in plans.Where(p => byArrangement.Contains(p.Record.Id) || p.Mode == Mode.Replaced))
        {
            var group = byArrangement[plan.Record.Id];
            var (revisions, gaps, overrides) = (plan.Parsed.Revisions, plan.Parsed.Gaps, plan.Parsed.Overrides);
            var start = revisions[0].From;
            var possible = StatementContext.Months(start, revisions[^1].To, today);
            var stored = plan.Mode == Mode.Created ? [] : (await store.StatementsOfAsync(plan.Id, ct)).Where(s => s.Validated).ToList();
            var known = new List<Statement>(stored);
            if (plan.Mode == Mode.Replaced && !await StoredStillFitAsync(plan, stored, group.Select(e => e.Month).ToHashSet(), possible, issues, ct))
                continue;
            foreach (var (entity, record, path, month, fileDays) in group.OrderBy(e => e.Month))
            {
                if (!possible.Contains(month))
                {
                    issues.Add(ImportIssue.Error(ChildcareCodes.MonthOutOfContract, "This month is outside the arrangement's dates or the rules' table.", path + ".month"));
                    continue;
                }
                if (possible.Contains(month.PlusMonths(-1)) && known.All(s => s.Month != month.PlusMonths(-1)))
                {
                    issues.Add(ImportIssue.Error(ChildcareCodes.PreviousNotValidated, "The previous month's statement comes first (decision 13).", path + ".month"));
                    continue;
                }
                var terms = MonthTerms.For(revisions, gaps, overrides, month);
                var days = MonthAssembler.Replanned(terms, fileDays);
                var previous = known.FirstOrDefault(s => s.Month == month.PlusMonths(-1));
                if (previous is { Days.Count: 0 })
                    previous = await store.FindStatementAsync(plan.Id, previous.Month, ct);
                var evaluation = MonthAssembler.Evaluate(terms, days, [], known, previous, record.Mileage, record.Cmg, DeclaredOf(record.Declared));
                if (evaluation.Figures is not { } figures)
                {
                    foreach (var (field, code) in evaluation.Problems)
                        issues.Add(ImportIssue.Error(code, StatementAssembly.Message(code), $"{path}.{field}"));
                    if (evaluation.Missing.Count > 0)
                        issues.Add(ImportIssue.Error(ChildcareCodes.StatementIncomplete,
                            $"June's reference months are missing: {string.Join(", ", evaluation.Missing.Select(StatementAssembly.Format))}.", path));
                    continue;
                }
                if (!Same(figures, record.Figures))
                {
                    issues.Add(ImportIssue.Error(ChildcareCodes.FiguresDiffer, "The figures re-computed from the file differ from the file's.", path + ".figures"));
                    continue;
                }
                // Validated at the start of the next month: about when its Pajemploi declaration was made (decision 22's due date).
                var statement = new Statement(plan.Id, month, true, record.Mileage, record.Cmg, days, figures, 0,
                    month.PlusMonths(1).OnDayOfMonth(1).AtStartOfDayInZone(DateTimeZone.Utc).ToInstant(), DeclaredOf(record.Declared));
                var already = stored.FirstOrDefault(s => s.Month == month);
                Mode mode;
                if (already is null)
                    mode = Mode.Created;
                else if (Same(already.Figures!, FiguresOf(figures)))
                    mode = Mode.Unchanged; // compared at the stored precision: hours keep two decimals
                else if (context.Replace)
                    mode = Mode.Replaced;
                else
                {
                    issues.Add(ImportIssue.Error(ImportCodes.Conflict, "This month is stored with other figures; nothing is overwritten.", path));
                    tally += new SectionCounts(0, 0, 1);
                    continue;
                }
                known.RemoveAll(s => s.Month == month);
                known.Add(statement);
                statementPlans.Add(new StatementPlan(plan, statement, mode));
                tally += mode switch
                {
                    Mode.Created => new SectionCounts(1, 0, 0),
                    Mode.Unchanged => new SectionCounts(0, 1, 0),
                    _ => new SectionCounts(0, 0, 0, 1),
                };
            }
        }
        return tally;
    }

    /// <summary>
    /// Replacing an arrangement: its validated months the file leaves out must still lie within the file's dates (the API's
    /// end_before_validated) and come out with the same figures under the file's terms; else the replacement is refused.
    /// </summary>
    private async Task<bool> StoredStillFitAsync(
        Plan plan, IReadOnlyList<Statement> stored, IReadOnlySet<YearMonth> inFile, IReadOnlyList<YearMonth> possible, List<ImportIssue> issues, CancellationToken ct)
    {
        var known = new List<Statement>();
        foreach (var month in stored.OrderBy(s => s.Month))
        {
            if (!possible.Contains(month.Month))
            {
                issues.Add(ImportIssue.Error(ChildcareCodes.EndBeforeValidated,
                    $"{StatementAssembly.Format(month.Month)} is validated and falls outside the file's dates: reopen it first.", plan.Path + ".terms"));
                return false;
            }
            if (!inFile.Contains(month.Month))
            {
                var full = (await store.FindStatementAsync(plan.Id, month.Month, ct))!;
                var terms = MonthTerms.For(plan.Parsed.Revisions, plan.Parsed.Gaps, plan.Parsed.Overrides, month.Month);
                var evaluation = MonthAssembler.Evaluate(terms, MonthAssembler.Replanned(terms, full.Days), [], known,
                    known.LastOrDefault(s => s.Month == month.Month.PlusMonths(-1)), full.Mileage, full.Cmg, full.Declared);
                if (evaluation.Figures is not { } figures || !Same(full.Figures!, FiguresOf(figures)))
                {
                    issues.Add(ImportIssue.Error(ChildcareCodes.FiguresDiffer,
                        $"{StatementAssembly.Format(month.Month)} is validated and would change under the file's terms: reopen it or add it to the file.", plan.Path + ".terms"));
                    return false;
                }
                known.Add(full);
            }
            else
                known.Add(month);
        }
        return true;
    }

    public async Task<Result<IReadOnlyDictionary<string, SectionCounts>>> ApplyAsync(ImportContext context, IStagedBatches staged, CancellationToken ct)
    {
        foreach (var plan in plans.Where(p => p.Mode != Mode.Unchanged))
        {
            // A person this import creates was planned at validation: the real ids are the context's now.
            plan.Parsed = plan.Parsed with { Child = context.Persons[plan.Record.Child].Id };
            var parsed = plan.Parsed;
            var owners = plan.Record.Ownership.Select(o => new OwnerShare(context.Persons[o.Person].Id, o.ShareBp)).ToList();
            var mark = new ImportMark(plan.Record.Id, plan.Entity.Hash, context.Actor);
            if (plan.Mode == Mode.Created)
            {
                var made = await items.CreateAsync(new NewItem(ChildcareModule.Key, ChildcareKinds.Childminder, plan.Record.Name, parsed.Visibility, null, owners, parsed.ShareLevel), ct);
                if (!made.TryGetValue(out var id))
                    return ImportSectionKit.FailureOf(made);
                plan.Id = id;
                context.Items[(Arrangements, plan.Record.Id)] = id;
                var revisions = parsed.Revisions.Select(t => t with { ItemId = id }).ToList();
                await store.CreateAsync(ArrangementOf(plan, id, 0), revisions[0] with { To = null }, ct, mark);
                foreach (var terms in revisions.Skip(1))
                    await store.AddTermsAsync(terms with { To = null }, ct);
                if (revisions[^1].To is { } end)
                    await store.SetEndAsync(id, end, ct);
                await store.ReplaceGapsAsync(id, parsed.Gaps, ct);
                foreach (var o in parsed.Overrides)
                    await store.SetOverrideAsync(id, o.Monday, o.WeekType, ct);
            }
            else
            {
                var item = await items.ReplaceAsync(plan.Id, plan.Record.Name, parsed.Visibility, owners, ct, parsed.ShareLevel);
                if (item.TryGetFailure(out var failure))
                    return Result.Failure<IReadOnlyDictionary<string, SectionCounts>>(failure!);
                await store.ReplaceImportedAsync(ArrangementOf(plan, plan.Id, plan.Stored!.Version), parsed.Revisions, parsed.Gaps, parsed.Overrides, mark, ct);
            }

            var archive = (plan.WasClosed, plan.Record.Closed) switch
            {
                (false, true) => await items.ArchiveAsync(plan.Id, clock.GetCurrentInstant(), ct),
                (true, false) => await items.UnarchiveAsync(plan.Id, ct),
                _ => Result.Success(),
            };
            if (archive.TryGetFailure(out var archiveFailure))
                return Result.Failure<IReadOnlyDictionary<string, SectionCounts>>(archiveFailure!);
        }

        foreach (var statement in statementPlans.Where(s => s.Mode != Mode.Unchanged))
            await store.PutValidatedAsync(statement.Statement with { ItemId = statement.Arrangement.Id }, ct);
        return Result.Success(counts);
    }

    /// <summary>
    /// Every arrangement the reader can read, closed ones included, by name then file id, and their validated months (decision 24:
    /// drafts stay out). An arrangement is named for the bank's links.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, IReadOnlyList<JsonElement>>> ExportAsync(ExportContext context, CancellationToken ct)
    {
        var ids = (await visible.ForAsync(context.Reader, VisibilityScope.Me, includeArchived: true, ct)).ToList();
        var stored = await store.FindManyAsync(ids, ct);
        var heads = await items.FindManyAsync(stored.Select(a => a.ItemId).ToList(), ct);
        var exported = stored.Where(a => heads.ContainsKey(a.ItemId)).ToList();
        var marks = await store.ImportMarksAsync(exported.Select(a => a.ItemId).ToList(), ct);
        var fileIds = FileIds.Assign("cc", exported.Select(a => new KeyedRow(a.ItemId.Value, marks.GetValueOrDefault(a.ItemId)?.Ref,
            marks.GetValueOrDefault(a.ItemId)?.ImportedBy)).ToList(), context.Reader);
        var terms = (await store.TermsOfAsync(exported.Select(a => a.ItemId).ToList(), ct)).ToLookup(t => t.ItemId);
        var gaps = (await store.GapsOfAsync(exported.Select(a => a.ItemId).ToList(), ct)).ToLookup(g => g.Item, g => g.Gap);
        var overrides = (await store.OverridesOfAsync(exported.Select(a => a.ItemId).ToList(), ct)).ToLookup(o => o.Item, o => o.Override);

        var arrangements = new List<ArrangementRecord>();
        var statements = new List<StatementRecord>();
        foreach (var a in exported.OrderBy(a => heads[a.ItemId].DisplayName, StringComparer.Ordinal).ThenBy(a => fileIds[a.ItemId.Value], StringComparer.Ordinal))
        {
            var head = heads[a.ItemId];
            var fileId = fileIds[a.ItemId.Value];
            context.Expose(a.ItemId);
            context.NameItem(a.ItemId, fileId);
            arrangements.Add(new ArrangementRecord
            {
                Id = fileId, Name = head.DisplayName, Child = context.PersonRef(a.Child),
                Carer = new CarerRecord { Name = a.CarerName, Pajemploi = a.CarerPajemploi, Approval = a.CarerApproval },
                Terms = terms[a.ItemId].Select(t => new TermsRecord
                {
                    From = LocalDatePattern.Iso.Format(t.From), To = t.To is { } to ? LocalDatePattern.Iso.Format(to) : null,
                    NetHourlyRate = Exact(t.NetHourlyRate), GrossHourlyRate = t.GrossHourlyRate is { } g ? Exact(g) : null, OvertimePct = Exact(t.OvertimeRate * 100m),
                    UpkeepPer9h = Exact(t.UpkeepPer9h), MealPrice = t.MealPrice is { } m ? Exact(m) : null,
                    WeekTypes = t.WeekTypes.Select(w => new WeekTypeRecord
                    {
                        Name = w.Name, Weeks = w.Weeks,
                        Schedule = w.Schedule.Select(s => new SlotRecord { Day = (int)s.Day, Arrival = Time.Format(s.Arrival), Departure = Time.Format(s.Departure), Meal = s.Meal }).ToList(),
                    }).ToList(),
                }).ToList(),
                Gaps = gaps[a.ItemId].Select(g => new GapRecord { Monday = LocalDatePattern.Iso.Format(g.Monday), Weeks = g.Weeks }).ToList(),
                WeekOverrides = overrides[a.ItemId].Select(o => new WeekOverrideRecord { Monday = LocalDatePattern.Iso.Format(o.Monday), WeekType = o.WeekType }).ToList(),
                Visibility = head.Visibility == Visibility.Private ? "private" : "shared",
                ShareLevel = ImportSectionKit.ShareLevelText(head),
                Ownership = head.Owners.Select(w => new ShareRecord { Person = context.PersonRef(w.PersonId), ShareBp = w.ShareBp })
                    .OrderBy(w => w.Person, StringComparer.Ordinal).ToList(),
                Closed = head.ArchivedAt is not null,
            });
            foreach (var month in (await store.StatementsOfAsync(a.ItemId, ct)).Where(s => s.Validated).OrderBy(s => s.Month))
            {
                var s = (await store.FindStatementAsync(a.ItemId, month.Month, ct))!;
                statements.Add(RecordOf(fileId, s));
            }
        }
        return new Dictionary<string, IReadOnlyList<JsonElement>>
        {
            [Arrangements] = arrangements.Select(r => JsonSerializer.SerializeToElement(r, ChildcareFileJson.Records.ArrangementRecord)).ToList(),
            [Statements] = statements.Select(r => JsonSerializer.SerializeToElement(r, ChildcareFileJson.Records.StatementRecord)).ToList(),
        };
    }

    private static StatementRecord RecordOf(string arrangement, Statement s)
    {
        var f = s.Figures!;
        var d = f.Declaration;
        return new StatementRecord
        {
            Arrangement = arrangement, Month = MonthPattern.Format(s.Month), Mileage = Exact(s.Mileage), Cmg = Exact(f.Cmg),
            Declared = s.Declared is { } entered && entered != new DeclaredFigures() ? RecordOf(entered) : null,
            Days = s.Days.Select(x => new DayRecord
            {
                Date = LocalDatePattern.Iso.Format(x.Date), Planned = x.Planned, Present = x.Present, Arrival = x.Arrival is { } ar ? Time.Format(ar) : null,
                Departure = x.Departure is { } de ? Time.Format(de) : null, Meal = x.Meal, Absence = x.Absence is { } ab ? AbsenceCodes.Of(ab) : null,
            }).ToList(),
            Figures = FiguresOf(f),
        };
    }

    private static DeclaredFigures? DeclaredOf(DeclaredRecord? r)
        => r is null ? null : new(r.NetPay, r.Upkeep, r.Meals, r.NormalHours, r.ActivityDays, r.PaidLeaveDays, r.ComplementaryHours, r.OvertimeHours);

    private static DeclaredRecord RecordOf(DeclaredFigures d)
    {
        static decimal? Optional(decimal? value) => value is { } v ? Exact(v) : null;
        return new DeclaredRecord
        {
            NormalHours = d.NormalHours, ActivityDays = d.ActivityDays, PaidLeaveDays = d.PaidLeaveDays, ComplementaryHours = Optional(d.ComplementaryHours),
            OvertimeHours = Optional(d.OvertimeHours), NetPay = Optional(d.NetPay), Upkeep = Optional(d.Upkeep), Meals = Optional(d.Meals),
        };
    }

    private static FiguresRecord FiguresOf(MonthFigures f)
    {
        var d = f.Declaration;
        return new FiguresRecord
        {
            NormalHours = d.NormalHours, ActivityDays = d.ActivityDays, PaidLeaveDays = d.PaidLeaveDays, ComplementaryHours = Exact(d.ComplementaryHours),
            OvertimeHours = Exact(d.OvertimeHours), NetPay = Exact(d.NetPay), Upkeep = Exact(d.Upkeep), Meals = Exact(d.Meals), BasePay = Exact(f.BasePay),
            Deduction = Exact(f.Deduction), ExtraPay = Exact(f.ExtraPay), PaidLeavePay = Exact(f.PaidLeavePay), Cost = Exact(f.Cost), TaxBase = Exact(f.TaxBase),
            ChildSickDeducted = f.ChildSickDeducted,
        };
    }

    /// <summary>Decision 25: the file's figures, field by field, at the cent the server rounds them to.</summary>
    private static bool Same(MonthFigures f, FiguresRecord r)
    {
        var d = f.Declaration;
        static bool Eq(decimal a, decimal b) => decimal.Round(a, 2) == decimal.Round(b, 2);
        return d.NormalHours == r.NormalHours && d.ActivityDays == r.ActivityDays && d.PaidLeaveDays == r.PaidLeaveDays && f.ChildSickDeducted == r.ChildSickDeducted
               && Eq(d.ComplementaryHours, r.ComplementaryHours) && Eq(d.OvertimeHours, r.OvertimeHours) && Eq(d.NetPay, r.NetPay) && Eq(d.Upkeep, r.Upkeep)
               && Eq(d.Meals, r.Meals) && Eq(f.BasePay, r.BasePay) && Eq(f.Deduction, r.Deduction) && Eq(f.ExtraPay, r.ExtraPay)
               && Eq(f.PaidLeavePay, r.PaidLeavePay) && Eq(f.Cost, r.Cost) && Eq(f.TaxBase, r.TaxBase);
    }

    /// <summary>A stored figure without its trailing zeros (numeric(19,4) reads back as 4.0000).</summary>
    private static decimal Exact(decimal value) => value / 1.000000000000000000000000000000000m;

    private Task<bool> ValidAsync(Parsed parsed, string path, List<ImportIssue> issues, CancellationToken ct)
    {
        var ok = true;
        // The file's persons are resolved (or planned) by then: a dependent this import creates is not stored yet.
        if (parsed.ChildKind != PersonKind.Dependent)
        {
            issues.Add(ImportIssue.Error(ChildcareCodes.ChildNotDependent, "The child is one of the household's dependents.", path + ".child"));
            ok = false;
        }
        if (rules.ValidateOwners(parsed.Visibility, parsed.Owners).TryGetFailure(out var failure))
        {
            issues.Add(ImportIssue.Error(failure!.Code, failure.Message, path + ".ownership"));
            ok = false;
        }
        return Task.FromResult(ok);
    }

    private static bool NoNulls(ArrangementRecord record, string path, List<ImportIssue> issues)
        => ImportSectionKit.NoNulls(record.Ownership, path + ".ownership", issues)
           & ImportSectionKit.NoNulls(record.Terms, path + ".terms", issues)
           & ImportSectionKit.NoNulls(record.Gaps, path + ".gaps", issues)
           & ImportSectionKit.NoNulls(record.WeekOverrides ?? [], path + ".week_overrides", issues)
           & record.Terms.Select((t, j) => t is null || NoNulls(t, $"{path}.terms[{j}]", issues)).Aggregate(true, (a, b) => a & b);

    private static bool NoNulls(TermsRecord t, string at, List<ImportIssue> issues)
        => ImportSectionKit.NoNulls(t.Schedule ?? [], at + ".schedule", issues)
           & ImportSectionKit.NoNulls(t.WeekTypes ?? [], at + ".week_types", issues)
           & (t.WeekTypes ?? []).Select((w, k) => w is null || ImportSectionKit.NoNulls(w.Schedule, $"{at}.week_types[{k}].schedule", issues)).Aggregate(true, (a, b) => a & b);

    /// <summary>The file's arrangement as the API's checks see it (<see cref="ChildcareCheck"/>, <see cref="ContractCheck"/>), at the file's paths.</summary>
    private static bool TryParse(ArrangementRecord r, string path, Func<string, ResolvedPerson?> person, List<ImportIssue> issues, out Parsed parsed)
    {
        parsed = null!;
        var ok = true;
        void Error(string code, string message, string at) { issues.Add(ImportIssue.Error(code, message, at)); ok = false; }

        if (r.Name.Trim().Length is < 1 or > ChildcareCheck.NameMax) Error(ImportCodes.InvalidValue, $"A name is 1 to {ChildcareCheck.NameMax} characters.", path + ".name");
        if (r.Carer.Name.Trim().Length is < 1 or > ChildcareCheck.NameMax) Error(ImportCodes.InvalidValue, "The carer's name is 1 to 120 characters.", path + ".carer.name");
        var visibility = r.Visibility switch { "private" => Visibility.Private, "shared" => Visibility.Shared, _ => (Visibility?)null };
        if (visibility is null) Error(ImportCodes.InvalidValue, "Visibility is shared or private.", path + ".visibility");
        if (ImportSectionKit.ShareLevelRefusal(r.ShareLevel, visibility ?? Visibility.Shared, out var level) is { } refused)
            Error(ImportCodes.InvalidValue, refused, path + ".share_level");
        var child = person(r.Child);
        if (child is null) Error(ImportCodes.UnresolvedReference, $"'{r.Child}' is not in persons.", path + ".child");

        var owners = new List<Owner>();
        for (var j = 0; j < r.Ownership.Count; j++)
            if (person(r.Ownership[j].Person) is { } owner)
                owners.Add(new Owner(owner.Id, owner.Kind, r.Ownership[j].ShareBp));
            else
                Error(ImportCodes.UnresolvedReference, $"'{r.Ownership[j].Person}' is not in persons.", $"{path}.ownership[{j}].person");

        if (r.Terms.Count == 0) Error(ImportCodes.InvalidValue, "An arrangement has at least one revision.", path + ".terms");
        var revisions = new List<Terms>();
        for (var j = 0; j < r.Terms.Count; j++)
        {
            var t = r.Terms[j];
            var at = $"{path}.terms[{j}]";
            var from = Date(t.From, at + ".from", issues, ref ok);
            LocalDate? to = t.To is null ? null : Date(t.To, at + ".to", issues, ref ok);
            var legacy = t.ProgrammedWeeks is not null || t.Schedule is not null;
            var types = new List<WeekType>();
            if (legacy && t.WeekTypes is not null) Error(ChildcareCodes.TermsBothFormats, ChildcareCheck.Message(ChildcareCodes.TermsBothFormats), at);
            else if (legacy && (t.ProgrammedWeeks is null || t.Schedule is null)) Error(ImportCodes.InvalidValue, "programmed_weeks and schedule go together.", at);
            else if (legacy) types.Add(new WeekType("Semaine type", t.ProgrammedWeeks!.Value, Slots(t.Schedule!, at + ".schedule", Error)));
            else if (t.WeekTypes is null) Error(ImportCodes.InvalidValue, "A revision has week_types, or programmed_weeks with schedule.", at);
            else
                for (var k = 0; k < t.WeekTypes.Count; k++)
                    types.Add(new WeekType(t.WeekTypes[k].Name?.Trim() ?? "", t.WeekTypes[k].Weeks, Slots(t.WeekTypes[k].Schedule, $"{at}.week_types[{k}].schedule", Error)));
            if (types.Count > 0 || t.WeekTypes is { Count: 0 })
                foreach (var (field, code) in ContractCheck.WeekTypes(types))
                    Error(code, StatementAssembly.Message(code), $"{at}.{Snake(field)}");
            if (t.NetHourlyRate <= 0 || t.OvertimePct < 0 || t.UpkeepPer9h < 0 || t.MealPrice < 0 || t.GrossHourlyRate <= 0)
                Error(ImportCodes.InvalidValue, "Rates are positive, the upkeep and the meal zero or more.", at);
            if (from is { } f)
            {
                if (j > 0 && revisions.Count == j && revisions[j - 1].To != f.PlusDays(-1))
                    Error(ImportCodes.InvalidValue, "Each revision ends the day before the next one starts.", $"{path}.terms[{j - 1}].to");
                if (to is { } end && end < f) Error(ImportCodes.InvalidValue, "A revision ends on or after its start.", at + ".to");
                revisions.Add(new Terms(new ItemId(Guid.Empty), f, to, t.NetHourlyRate, t.GrossHourlyRate, t.OvertimePct / 100m, t.UpkeepPer9h, t.MealPrice,
                    types));
            }
        }
        if (revisions.Count > 0 && revisions.Take(revisions.Count - 1).Any(t => t.To is null))
            Error(ImportCodes.InvalidValue, "Only the last revision is open.", path + ".terms");

        var gaps = new List<CareGap>();
        for (var j = 0; j < r.Gaps.Count; j++)
            if (Date(r.Gaps[j].Monday, $"{path}.gaps[{j}].monday", issues, ref ok) is { } monday)
                gaps.Add(new CareGap(monday, r.Gaps[j].Weeks));
        if (revisions.Count > 0)
            // As the API: the latest weeks bound the years from the latest revision, the earlier years were checked against theirs.
            foreach (var (field, code) in ContractCheck.Gaps(revisions[0].From, revisions[^1].ProgrammedWeeks, gaps, revisions[^1].From))
                Error(code, StatementAssembly.Message(code), $"{path}.{field}");

        var overrides = new List<WeekOverride>();
        for (var j = 0; j < (r.WeekOverrides?.Count ?? 0); j++)
            if (Date(r.WeekOverrides![j].Monday, $"{path}.week_overrides[{j}].monday", issues, ref ok) is { } monday)
                overrides.Add(new WeekOverride(monday, r.WeekOverrides[j].WeekType));
        if (ok && revisions.Count == r.Terms.Count)
            foreach (var (field, code) in ContractCheck.Overrides(overrides, revisions[0].From, revisions[^1].To, [.. revisions.Select(t => (t.From, t.WeekTypes.Count))]))
                Error(code, StatementAssembly.Message(code), $"{path}.{Snake(field)}");

        if (ok)
            parsed = new Parsed(child!.Id, child.Kind, visibility!.Value, owners, revisions, gaps, overrides, level);
        return ok;
    }

    private static bool TryParseStatement(StatementRecord r, string path, List<ImportIssue> issues, out YearMonth month, out IReadOnlyList<StatementDay> days)
    {
        var ok = true;
        var parsedMonth = MonthPattern.Parse(r.Month);
        month = parsedMonth.Success ? parsedMonth.Value : default;
        if (!parsedMonth.Success) { issues.Add(ImportIssue.Error(ImportCodes.InvalidValue, "A month is written YYYY-MM.", path + ".month")); ok = false; }
        if (r.Mileage < 0 || r.Cmg < 0 || DeclaredOf(r.Declared) is { AnyNegative: true }) { issues.Add(ImportIssue.Error(ImportCodes.InvalidValue, "The mileage, the CMG and the declared amounts are zero or more.", path)); ok = false; }
        var list = new List<StatementDay>();
        for (var j = 0; j < r.Days.Count; j++)
        {
            var d = r.Days[j];
            var at = $"{path}.days[{j}]";
            var date = Date(d.Date, at + ".date", issues, ref ok);
            LocalTime? arrival = null, departure = null;
            if (d.Arrival is not null) { var p = Time.Parse(d.Arrival); if (p.Success) arrival = p.Value; else { issues.Add(ImportIssue.Error(ImportCodes.InvalidValue, "A time is written HH:mm.", at + ".arrival")); ok = false; } }
            if (d.Departure is not null) { var p = Time.Parse(d.Departure); if (p.Success) departure = p.Value; else { issues.Add(ImportIssue.Error(ImportCodes.InvalidValue, "A time is written HH:mm.", at + ".departure")); ok = false; } }
            AbsenceReason? absence = null;
            if (d.Absence is not null && (absence = AbsenceCodes.TryParse(d.Absence)) is null)
            {
                issues.Add(ImportIssue.Error(ImportCodes.InvalidValue, "An absence is family, parent_rtt, child_sick, carer_absent or holiday.", at + ".absence"));
                ok = false;
            }
            if (date is { } on)
                list.Add(new StatementDay(on, d.Planned, d.Present, arrival, departure, d.Meal, absence));
        }
        days = list;
        return ok;
    }

    private static LocalDate? Date(string text, string path, List<ImportIssue> issues, ref bool ok)
    {
        var parsed = LocalDatePattern.Iso.Parse(text);
        if (parsed.Success)
            return parsed.Value;
        issues.Add(ImportIssue.Error(ImportCodes.InvalidValue, "A date is written YYYY-MM-DD.", path));
        ok = false;
        return null;
    }

    private static string Snake(string field)
        => field.Replace("programmedWeeks", "programmed_weeks").Replace("weekTypes", "week_types").Replace("weekOverrides", "week_overrides").Replace("weekType", "week_type");

    /// <summary>A schedule's slots; a bad day or time is reported and skipped.</summary>
    private static List<WeekdaySlot> Slots(IReadOnlyList<SlotRecord> records, string at, Action<string, string, string> error)
    {
        var slots = new List<WeekdaySlot>();
        for (var k = 0; k < records.Count; k++)
        {
            var slot = records[k];
            var arrival = Time.Parse(slot.Arrival);
            var departure = Time.Parse(slot.Departure);
            if (slot.Day is < 1 or > 7) error(ImportCodes.InvalidValue, "A day is 1 (Monday) to 7 (Sunday).", $"{at}[{k}].day");
            else if (!arrival.Success || !departure.Success) error(ImportCodes.InvalidValue, "A time is written HH:mm.", $"{at}[{k}]");
            else slots.Add(new WeekdaySlot((IsoDayOfWeek)slot.Day, arrival.Value, departure.Value, slot.Meal));
        }
        return slots;
    }

    private static Arrangement ArrangementOf(Plan plan, ItemId id, uint version)
        => new(id, plan.Parsed.Child, plan.Record.Carer.Name.Trim(), Blank(plan.Record.Carer.Pajemploi), Blank(plan.Record.Carer.Approval), version);

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
