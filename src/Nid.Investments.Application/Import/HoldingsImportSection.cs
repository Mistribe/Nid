using System.Text.Json;
using System.Text.RegularExpressions;
using Nid.Core.Contracts;
using Nid.Investments.Contracts;
using Nid.Investments.Domain;
using Nid.Platform.Contracts;
using NodaTime;
using NodaTime.Text;
using UnambitiousFx.Functional;

namespace Nid.Investments.Application.Import;

/// <summary>
/// The sections <c>holdings</c>, <c>holding_snapshots</c> and <c>holding_movements</c> (issue #34 §5). A row names its envelope by
/// the file id the section <c>envelopes</c> resolved; the rules and warnings are the API's (<see cref="HoldingRules"/>,
/// <see cref="MovementRules"/>, <see cref="HoldingMath"/>), never restated here. Runs after the Core's facts: a movement's
/// <c>envelope_flow</c> names a flow of the file, found once written. A file id resolves only among rows the importer can see
/// (<see cref="ImportResolver"/>). Like the envelope's values and flows, an envelope closed in the file still takes its history.
/// </summary>
public sealed partial class HoldingsImportSection(
    IHoldingStore store, IEnvelopeStore envelopes, IItemRegistry items, IFactImports facts, IAccessPolicy policy, IHouseholdCalendar calendar) : IImportSection
{
    public const string HoldingsSection = "holdings";
    public const string SnapshotsSection = "holding_snapshots";
    public const string MovementsSection = "holding_movements";

    private enum Mode { Created, Unchanged, Replaced }

    /// <summary>An envelope of the file and what it holds once this file applies; <see cref="Id"/> is planned for a new one until applied.</summary>
    private sealed class EnvelopePlan(string fileId, ItemId id, Currency currency, bool created)
    {
        public string FileId { get; } = fileId;
        public ItemId Id { get; } = id;
        public Currency Currency { get; } = currency;
        public bool Created { get; } = created;
        public bool? Writable { get; set; }
        public HashSet<Guid> StoredHoldings { get; } = [];
        public HashSet<Guid> StoredSnapshots { get; } = [];
        public HashSet<Guid> StoredMovements { get; } = [];
        public List<StoredHolding> Holdings { get; } = [];
        public List<HoldingSnapshot> Snapshots { get; } = [];
        public List<HoldingMovement> Movements { get; } = [];
    }

    /// <summary>A row of the file as validation decided it: <see cref="Id"/> is its stored id, or a new one.</summary>
    private sealed record RowPlan<T>(string FileId, string Path, EnvelopePlan Envelope, Mode Mode, ImportMark Mark, T Value)
    {
        public bool Writes => Mode != Mode.Unchanged;
    }

    private readonly List<RowPlan<EnvelopeHolding>> holdingPlans = [];
    private readonly List<RowPlan<HoldingSnapshot>> snapshotPlans = [];
    private readonly List<RowPlan<(HoldingMovement Movement, string? Flow)>> movementPlans = [];
    private Dictionary<string, SectionCounts> tally = [];
    private ImportContext context = null!;
    private Func<ItemId, Task<bool>> canRead = null!;

    public string Module => "investments";
    public IReadOnlyList<string> Sections => [HoldingsSection, SnapshotsSection, MovementsSection];
    public int Order => 95;

    public IReadOnlyList<ImportIssue> ValidateBatch(string section, JsonElement entities, int firstIndex)
    {
        var issues = new List<ImportIssue>();
        var position = 0;
        foreach (var entity in entities.EnumerateArray())
        {
            var path = NidDataJson.Path(section, firstIndex + position++);
            switch (section)
            {
                case HoldingsSection when Read<HoldingRecord>(entity, path, issues, r => r.Id, out var holding):
                    if (TryHolding(holding, path, issues, out var draft))
                        Check(HoldingRules.Check(Guid.Empty, default, draft, []), path, issues, out _);
                    break;
                case SnapshotsSection when Read<HoldingSnapshotRecord>(entity, path, issues, r => r.Id, out var snapshot):
                    if (ImportSectionKit.NoNulls(snapshot.Lines, path + ".lines", issues))
                    {
                        EnvelopeParsing.TryDate(snapshot.AsOf, path + ".as_of", null, issues, out _);
                        for (var j = 0; j < snapshot.Lines.Count; j++)
                            TryMoney(snapshot.Lines[j].Value, $"{path}.lines[{j}].value", issues, out _);
                    }
                    break;
                case MovementsSection when Read<HoldingMovementRecord>(entity, path, issues, r => r.Id, out var movement):
                    EnvelopeParsing.TryDate(movement.OccurredOn, path + ".occurred_on", null, issues, out _);
                    TryMoney(movement.Amount, path + ".amount", issues, out _);
                    break;
            }
        }
        return issues;
    }

    public async Task<SectionOutcome> ValidateAllAsync(ImportContext context, IStagedBatches staged, CancellationToken ct)
    {
        this.context = context;
        canRead = ImportSectionKit.Readable(policy, context.Actor, new Dictionary<ItemId, bool>(), ct); // one policy check per item
        tally = Sections.ToDictionary(s => s, _ => SectionCounts.Zero, StringComparer.Ordinal);
        holdingPlans.Clear();
        snapshotPlans.Clear();
        movementPlans.Clear();
        var issues = new List<ImportIssue>();
        var today = await calendar.TodayAsync(ct); // the household's, as the API decides it
        var envelopePlans = new Dictionary<string, EnvelopePlan>(StringComparer.Ordinal);

        async Task<EnvelopePlan?> EnvelopeOfAsync(string fileId, string path)
        {
            if (context.FailedItems.Contains((EnvelopesImportSection.Section, fileId)))
                return null; // reported there
            if (envelopePlans.TryGetValue(fileId, out var known))
                return known;
            if (!context.Items.TryGetValue((EnvelopesImportSection.Section, fileId), out var id))
            {
                issues.Add(ImportIssue.Error(ImportCodes.UnresolvedReference, $"'{fileId}' names no entity of envelopes.", path));
                return null;
            }
            var plan = new EnvelopePlan(fileId, id, context.ItemCurrencies[id]!.Value, context.NewItems.Contains(id));
            if (!plan.Created)
            {
                plan.Holdings.AddRange(await store.HoldingsAsync(id, ct));
                plan.Snapshots.AddRange(await store.SnapshotsAsync(id, ct));
                plan.Movements.AddRange(await store.MovementsAsync(id, ct));
                plan.StoredHoldings.UnionWith(plan.Holdings.Select(h => h.Holding.Id));
                plan.StoredSnapshots.UnionWith(plan.Snapshots.Select(s => s.Id));
                plan.StoredMovements.UnionWith(plan.Movements.Select(m => m.Id));
            }
            return envelopePlans[fileId] = plan;
        }

        // Holdings: each one checked against its envelope's holdings as this file leaves them (an ISIN once per envelope).
        var failedHoldings = new HashSet<string>(StringComparer.Ordinal);
        var holdingRecords = ImportSectionKit.ReadAll<HoldingRecord>(staged, HoldingsSection, r => r.Id, issues, unread: id => failedHoldings.Add(id));
        var knownHoldings = await store.FindImportedAsync(HoldingTable.Holding, holdingRecords.Select(r => r.Record.Id).ToList(), ct);
        var holdingsByFileId = new Dictionary<string, RowPlan<EnvelopeHolding>>(StringComparer.Ordinal);
        foreach (var (entity, record) in holdingRecords)
        {
            var path = NidDataJson.Path(HoldingsSection, entity.Index);
            if (await EnvelopeOfAsync(record.Item, path + ".item") is not { } envelope || !TryHolding(record, path, issues, out var draft)
                || await ResolveRowAsync(HoldingsSection, "holding", entity, record.Id, path, "item", envelope, knownHoldings, envelope.StoredHoldings, issues, ct)
                    is not { } row
                || !Check(HoldingRules.Check(row.Id, envelope.Id, draft, envelope.Holdings), path, issues, out var holding))
            {
                failedHoldings.Add(record.Id);
                continue;
            }
            envelope.Holdings.RemoveAll(h => h.Holding.Id == holding.Id);
            envelope.Holdings.Add(new StoredHolding(holding, 0));
            holdingsByFileId[record.Id] = Plan(holdingPlans, HoldingsSection, record.Id, path, envelope, row, entity, holding);
        }

        // The holding a snapshot line or a movement names: one of this file, of the same envelope; one refused is reported already.
        bool TryHoldingOf(string fileId, EnvelopePlan? envelope, string path, out RowPlan<EnvelopeHolding> plan)
        {
            if (!holdingsByFileId.TryGetValue(fileId, out plan!))
            {
                if (!failedHoldings.Contains(fileId))
                    issues.Add(ImportIssue.Error(ImportCodes.UnresolvedReference, $"'{fileId}' names no entity of holdings.", path));
                return false;
            }
            if (envelope is null || plan.Envelope == envelope)
                return true;
            issues.Add(ImportIssue.Error(ImportCodes.InvalidValue, "This holding is not one of this envelope's.", path));
            return false;
        }

        var stagedFacts = StagedFacts(staged);

        // Snapshots: one per envelope and date (holdings spec D5); a stored one of that date the file does not name is replaced only with replace.
        var snapshotRecords = ImportSectionKit.ReadAll<HoldingSnapshotRecord>(staged, SnapshotsSection, r => r.Id, issues,
            (r, path, found) => ImportSectionKit.NoNulls(r.Lines, path + ".lines", found));
        var knownSnapshots = await store.FindImportedAsync(HoldingTable.Snapshot, snapshotRecords.Select(r => r.Record.Id).ToList(), ct);
        var fileSnapshots = new HashSet<Guid>();
        foreach (var (entity, record) in snapshotRecords)
        {
            var path = NidDataJson.Path(SnapshotsSection, entity.Index);
            var dated = EnvelopeParsing.TryDate(record.AsOf, path + ".as_of", null, issues, out var asOf);
            if (await EnvelopeOfAsync(record.Item, path + ".item") is not { } envelope || !dated)
                continue;
            var lines = new List<SnapshotLineDraft>();
            for (var j = 0; j < record.Lines.Count; j++)
            {
                var line = record.Lines[j];
                var at = $"{path}.lines[{j}]";
                if (TryHoldingOf(line.Holding, envelope, at + ".holding", out var holding) & TryMoney(line.Value, at + ".value", issues, out var value))
                    lines.Add(new SnapshotLineDraft(holding.Value.Id, line.Units, line.UnitPrice, value, line.CostBasisPerUnit));
            }
            if (lines.Count != record.Lines.Count
                || await ResolveRowAsync(SnapshotsSection, "snapshot", entity, record.Id, path, "item", envelope, knownSnapshots, envelope.StoredSnapshots, issues, ct)
                    is not { } row)
                continue;

            if (envelope.Snapshots.FirstOrDefault(s => s.AsOf == asOf && s.Id != row.Id) is { } sameDay)
            {
                if (fileSnapshots.Contains(sameDay.Id))
                {
                    issues.Add(ImportIssue.Error(ImportCodes.DuplicateFact, $"This file already has a snapshot of '{record.Item}' on {record.AsOf}.", path + ".as_of"));
                    continue;
                }
                if (row.Mode != Mode.Created || !context.Replace)
                {
                    issues.Add(ImportIssue.Error(ImportCodes.Conflict,
                        $"'{record.Item}' already has a snapshot on {record.AsOf}, which this file does not name; nothing is overwritten (import-format.md §8).",
                        path + ".as_of"));
                    Tally(SnapshotsSection, new SectionCounts(0, 0, 1));
                    continue;
                }
                row = (Mode.Replaced, sameDay.Id, context.Actor);
            }

            if (!Check(HoldingRules.Check(row.Id, envelope.Id, new SnapshotDraft(asOf!.Value, record.SourceLabel, record.Complete, lines, false),
                    envelope.Holdings, envelope.Currency, today), path, issues, out var snapshot))
                continue;
            envelope.Snapshots.RemoveAll(s => s.Id == snapshot.Id);
            envelope.Snapshots.Add(snapshot);
            fileSnapshots.Add(snapshot.Id);
            Plan(snapshotPlans, SnapshotsSection, record.Id, path, envelope, row, entity, snapshot);

            var valuation = stagedFacts.Valuation(envelope.FileId, snapshot.AsOf)
                            ?? (envelope.Created ? null : await StoredValuationAsync(envelope.Id, snapshot.AsOf, ct));
            foreach (var warning in HoldingMath.Warnings(snapshot, valuation))
                issues.Add(Warning(warning, warning.HoldingId is { } h ? $"{path}.lines[{snapshot.Lines.ToList().FindIndex(l => l.HoldingId == h)}]" : path));
        }

        // Movements: the API's rules; a switch's legs come with their reference, written as one.
        var movementRecords = ImportSectionKit.ReadAll<HoldingMovementRecord>(staged, MovementsSection, r => r.Id, issues);
        var knownMovements = await store.FindImportedAsync(HoldingTable.Movement, movementRecords.Select(r => r.Record.Id).ToList(), ct);
        foreach (var (entity, record) in movementRecords)
        {
            var path = NidDataJson.Path(MovementsSection, entity.Index);
            if (!TryHoldingOf(record.Holding, null, path + ".holding", out var holding)
                | !EnvelopeParsing.TryDate(record.OccurredOn, path + ".occurred_on", null, issues, out var on)
                | !TryMoney(record.Amount, path + ".amount", issues, out var amount))
                continue;
            var envelope = holding.Envelope;
            if (record.Kind is MovementKinds.SwitchIn or MovementKinds.SwitchOut && record.SwitchRef is null)
            {
                issues.Add(ImportIssue.Error(InvestmentsCodes.InvalidMovement, "A switch's leg carries the reference it shares with the other leg.", path + ".switch_ref"));
                continue;
            }
            if (record.SwitchRef is { Length: > MovementRules.TextMax })
            {
                issues.Add(ImportIssue.Error(InvestmentsCodes.InvalidMovement, $"A reference has at most {MovementRules.TextMax} characters.", path + ".switch_ref"));
                continue;
            }
            if (record.EnvelopeFlow is { } flow && stagedFacts.FlowItem(flow) != envelope.FileId)
            {
                issues.Add(ImportIssue.Error(ImportCodes.UnresolvedReference, $"'{flow}' names no flow of '{envelope.FileId}' in flows.", path + ".envelope_flow"));
                continue;
            }
            if (await ResolveRowAsync(MovementsSection, "movement", entity, record.Id, path, "holding", envelope, knownMovements, envelope.StoredMovements, issues, ct)
                    is not { } row
                || !Check(MovementRules.Check(envelope.Id, holding.Value.Id, record.Kind, on!.Value, record.Units, record.UnitPrice, amount, null,
                    record.SwitchRef, record.Label, record.ExternalRef, envelope.Holdings, [], envelope.Currency, today), path, issues, out var movement))
                continue;
            movement = movement with { Id = row.Id };
            envelope.Movements.RemoveAll(m => m.Id == movement.Id);
            envelope.Movements.Add(movement);
            Plan(movementPlans, MovementsSection, record.Id, path, envelope, row, entity, (movement, record.EnvelopeFlow));
        }

        // The movement warnings of the dates this file writes, against the envelope's flows once the file applies.
        foreach (var envelope in envelopePlans.Values)
        {
            var written = movementPlans.Where(p => p.Envelope == envelope && p.Writes).ToList();
            if (written.Count == 0)
                continue;
            var stored = envelope.Created ? [] : await facts.FlowsOfAsync([envelope.Id], ct);
            var flows = stored.Where(f => f.Import is null || stagedFacts.FlowItem(f.Import.Ref) is null).Select(f => f.Flow)
                .Concat(stagedFacts.Flows(envelope.FileId, envelope.Id, envelope.Currency)).ToList();
            foreach (var warning in HoldingMath.Warnings(envelope.Movements, flows))
                if (written.FirstOrDefault(p => p.Value.Movement.OccurredOn == warning.On) is { } at)
                    issues.Add(Warning(warning, at.Path));
        }

        return new SectionOutcome(issues, tally);
    }

    public async Task<Result<IReadOnlyDictionary<string, SectionCounts>>> ApplyAsync(ImportContext context, IStagedBatches staged, CancellationToken ct)
    {
        // A created envelope's real id replaced the planned one in the context when the section envelopes applied.
        ItemId Real(EnvelopePlan envelope) => context.Items[(EnvelopesImportSection.Section, envelope.FileId)];

        foreach (var plan in holdingPlans.Where(p => p.Writes))
        {
            var holding = plan.Value with { Envelope = Real(plan.Envelope) };
            if (plan.Mode == Mode.Created)
                await store.AddAsync(holding, ct);
            else
                await store.ReplaceAsync(holding, ct);
            await store.MarkAsync(HoldingTable.Holding, holding.Id, plan.Mark, ct);
        }

        // In place first: a snapshot replaced may free the date a new one takes.
        foreach (var plan in snapshotPlans.Where(p => p.Mode == Mode.Replaced).Concat(snapshotPlans.Where(p => p.Mode == Mode.Created)))
        {
            await store.SaveSnapshotAsync(plan.Value with { Envelope = Real(plan.Envelope) }, ct);
            await store.MarkAsync(HoldingTable.Snapshot, plan.Value.Id, plan.Mark, ct);
        }

        var flows = new Dictionary<ItemId, IReadOnlyList<StoredFlow>>();
        foreach (var plan in movementPlans.Where(p => p.Writes))
        {
            var envelope = Real(plan.Envelope);
            Guid? flowId = null;
            if (plan.Value.Flow is { } reference)
            {
                // The Core's flows are written: the one of this file id on this envelope, the importer's own first.
                if (!flows.TryGetValue(envelope, out var stored))
                    flows[envelope] = stored = await facts.FlowsOfAsync([envelope], ct);
                flowId = stored.Where(f => f.Import?.Ref == reference).OrderBy(f => f.Import!.ImportedBy == context.Actor ? 0 : 1).ThenBy(f => f.Flow.Id)
                    .FirstOrDefault()?.Flow.Id;
                if (flowId is null)
                    return ImportSectionKit.Drift([ImportIssue.Error(ImportCodes.UnresolvedReference, $"'{reference}' no longer names a flow.", plan.Path + ".envelope_flow")]);
            }
            var movement = plan.Value.Movement with { Envelope = envelope, EnvelopeFlowId = flowId };
            if (plan.Mode == Mode.Created)
                await store.AddMovementsAsync([movement], ct);
            else
                await store.ReplaceMovementAsync(movement, ct);
            await store.MarkAsync(HoldingTable.Movement, movement.Id, plan.Mark, ct);
        }

        return Result.Success<IReadOnlyDictionary<string, SectionCounts>>(tally);
    }

    /// <summary>
    /// The holdings, snapshots and movements of every envelope the section <c>envelopes</c> exported (it ran first and named them), by
    /// envelope, name or date, then file id. A movement's flow gets the file id the Core's <c>flows</c> gives it: the same rows, the same ids.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, IReadOnlyList<JsonElement>>> ExportAsync(ExportContext context, CancellationToken ct)
    {
        var named = context.ItemFileIds;
        var exported = (await envelopes.ReadAllAsync(named.Keys.ToList(), ct)).Select(e => e.Envelope.ItemId).ToList();
        var heads = await items.FindManyAsync(exported, ct);
        var currencies = exported.ToDictionary(e => e, e => heads[e].Currency!);
        var (holdings, snapshots, movements, marks) = await store.ExportAsync(exported, ct);

        var flowIds = named.Count == 0 || movements.All(m => m.EnvelopeFlowId is null)
            ? new Dictionary<Guid, string>()
            : FileIds.Assign("fl", (await facts.FlowsOfAsync(named.Keys.ToList(), ct))
                .Select(f => new KeyedRow(f.Flow.Id, f.Import?.Ref, f.Import?.ImportedBy)).ToList(), context.Reader);
        KeyedRow Keyed(Guid id) => marks.TryGetValue(id, out var mark) ? new KeyedRow(id, mark.Ref, mark.ImportedBy) : new KeyedRow(id, null, null);
        var holdingIds = FileIds.Assign("hold", holdings.Select(h => Keyed(h.Id)).ToList(), context.Reader);
        var snapshotIds = FileIds.Assign("hsnap", snapshots.Select(s => Keyed(s.Id)).ToList(), context.Reader);
        var movementIds = FileIds.Assign("hmov", movements.Select(m => Keyed(m.Id)).ToList(), context.Reader);

        var holdingRecords = holdings
            .Select(h => new HoldingRecord
            {
                Id = holdingIds[h.Id], Item = named[h.Envelope], Name = h.Name, Isin = h.Isin, Kind = h.Kind, AssetClass = h.AssetClass,
                Issuer = h.Issuer, RiskIndicator = h.RiskIndicator, OpenedOn = Iso(h.OpenedOn), MaturesOn = Iso(h.MaturesOn),
                EntryCostPct = h.EntryCostPct, ClosedOn = Iso(h.ClosedOn), Note = h.Note,
            })
            .OrderBy(r => r.Item, StringComparer.Ordinal).ThenBy(r => r.Name, StringComparer.Ordinal).ThenBy(r => r.Id, StringComparer.Ordinal);
        var snapshotRecords = snapshots
            .Select(s => new HoldingSnapshotRecord
            {
                Id = snapshotIds[s.Id], Item = named[s.Envelope], AsOf = LocalDatePattern.Iso.Format(s.AsOf), SourceLabel = s.SourceLabel, Complete = s.Complete,
                Lines = s.Lines
                    .Select(l => new SnapshotLineRecord
                    {
                        Holding = holdingIds[l.HoldingId], Units = l.Units, UnitPrice = l.UnitPrice,
                        Value = new MoneyRecord { Amount = l.Value, Currency = currencies[s.Envelope] }, CostBasisPerUnit = l.CostBasisPerUnit,
                    })
                    .OrderBy(l => l.Holding, StringComparer.Ordinal).ToList(),
            })
            .OrderBy(r => r.Item, StringComparer.Ordinal).ThenBy(r => r.AsOf, StringComparer.Ordinal).ThenBy(r => r.Id, StringComparer.Ordinal);
        var movementRecords = movements
            .Select(m => new HoldingMovementRecord
            {
                Id = movementIds[m.Id], Holding = holdingIds[m.HoldingId], OccurredOn = LocalDatePattern.Iso.Format(m.OccurredOn), Kind = m.Kind,
                Units = m.Units, UnitPrice = m.UnitPrice, Amount = new MoneyRecord { Amount = m.Amount, Currency = currencies[m.Envelope] },
                // A flow corrected since is no longer current, so not exported: the link goes with it.
                EnvelopeFlow = m.EnvelopeFlowId is { } f ? flowIds.GetValueOrDefault(f) : null,
                SwitchRef = m.SwitchRef, Label = m.Label, ExternalRef = m.ExternalRef,
            })
            .OrderBy(r => r.Holding, StringComparer.Ordinal).ThenBy(r => r.OccurredOn, StringComparer.Ordinal).ThenBy(r => r.Id, StringComparer.Ordinal);

        return new Dictionary<string, IReadOnlyList<JsonElement>>
        {
            [HoldingsSection] = Elements(holdingRecords),
            [SnapshotsSection] = Elements(snapshotRecords),
            [MovementsSection] = Elements(movementRecords),
        };
    }
}
