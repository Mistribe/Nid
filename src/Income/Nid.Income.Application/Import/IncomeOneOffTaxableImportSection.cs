using System.Text.Json;
using Nid.Core.Contracts;
using Nid.Income.Contracts;
using Nid.Platform.Contracts;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;

namespace Nid.Income.Application.Import;

/// <summary>
/// The section <c>income_one_off_taxable</c> (tax-per spec 3.2): the net taxable income of the one-offs of the Core's
/// <c>flows</c> section. A one-off is a Core flow, whose id the Core generates, so the entry names it by the file id the
/// flow has in <c>flows</c> and the section runs after the Core's (<see cref="Order"/>): at commit it finds the flow the
/// Core just wrote through its import mark, as <c>IFactImports</c> does for every section that reads the facts. An entry
/// names a one-off of this file; any other file id is <c>import.unresolved_reference</c>, never an orphan row.
/// </summary>
public sealed class IncomeOneOffTaxableImportSection(IFactImports facts, IOneOffTaxableStore taxables, IAccessPolicy policy) : IImportSection
{
    public const string Section = "income_one_off_taxable";

    /// <summary>The Core's section of the flows the entries name (<c>FactsImportSection.FlowsSection</c>, not referenced across modules).</summary>
    private const string FlowsSection = "flows";

    /// <summary>After the Core's <c>valuations</c> and <c>flows</c> (90), before the holdings (95).</summary>
    public int Order => 92;

    /// <summary>An entry of the file as validation decided it; <see cref="Item"/> is the file id of the source the flow is on.</summary>
    private sealed record Plan(string FlowId, string Item, Money Taxable, Mode Mode);

    private enum Mode { Created, Unchanged, Replaced }

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<ImportedFact>> NoImports = new Dictionary<string, IReadOnlyList<ImportedFact>>();

    private readonly List<Plan> plans = [];

    /// <summary>The sources whose one-offs the file names: after the Core wrote, the rows of flows no longer current are swept.</summary>
    private readonly HashSet<string> sourceRefs = new(StringComparer.Ordinal);
    private IReadOnlyDictionary<string, SectionCounts> counts = new Dictionary<string, SectionCounts>();

    public string Module => "income";
    public IReadOnlyList<string> Sections => [Section];

    public IReadOnlyList<ImportIssue> ValidateBatch(string section, JsonElement entities, int firstIndex)
    {
        var issues = new List<ImportIssue>();
        var position = 0;
        foreach (var entity in entities.EnumerateArray())
        {
            var path = NidDataJson.Path(section, firstIndex + position++);
            if (NidDataJson.TryRead(entity, IncomeFileJson.Records.IncomeOneOffTaxableRecord, path, issues, out var record))
                TryEntry(record, path, issues, out _);
        }
        return issues;
    }

    public async Task<SectionOutcome> ValidateAllAsync(ImportContext context, IStagedBatches staged, CancellationToken ct)
    {
        plans.Clear();
        sourceRefs.Clear();
        var issues = new List<ImportIssue>();
        var tally = SectionCounts.Zero;
        var readable = new Dictionary<ItemId, bool>();
        var canRead = ImportSectionKit.Readable(policy, context.Actor, readable, ct);
        var writable = new Dictionary<ItemId, bool>();

        // The one-offs of the file's flows section, by file id.
        var oneOffs = new Dictionary<string, (string Item, string Currency, string Hash)>(StringComparer.Ordinal);
        foreach (var flow in staged.Section(FlowsSection))
            if (flow.Value.ValueKind == JsonValueKind.Object && Text(flow.Value, "id") is { } id && Text(flow.Value, "kind") == "income"
                && Text(flow.Value, "category") == IncomeWire.OneOffCategory && Text(flow.Value, "item") is { } item
                && flow.Value.TryGetProperty("amount", out var amount) && amount.ValueKind == JsonValueKind.Object)
            {
                oneOffs.TryAdd(id, (item, Text(amount, "currency") ?? "", flow.Hash));
                sourceRefs.Add(item);
            }

        // What the Core stored under those file ids: every importer's rows, resolved as the Core resolves them.
        var known = oneOffs.Count == 0 ? NoImports : await facts.FindImportedAsync(FactTable.Flow, oneOffs.Keys.ToList(), ct);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entity in staged.Section(Section))
        {
            var path = NidDataJson.Path(Section, entity.Index);
            if (!NidDataJson.TryRead(entity.Value, IncomeFileJson.Records.IncomeOneOffTaxableRecord, path, issues, out var record) || !TryEntry(record, path, issues, out var taxable))
                continue;
            if (!seen.Add(record.FlowId))
            {
                issues.Add(ImportIssue.Error(ImportCodes.DuplicateId, $"'{record.FlowId}' appears twice in {Section}.", path + ".flow_id"));
                continue;
            }
            if (!oneOffs.TryGetValue(record.FlowId, out var flow))
            {
                issues.Add(ImportIssue.Error(ImportCodes.UnresolvedReference, $"'{record.FlowId}' names no one-off income of flows.", path + ".flow_id"));
                continue;
            }
            if (context.FailedItems.Contains(("income_sources", flow.Item)))
                continue; // its source is reported already
            // The flow is on an income source of this file: the table's foreign key holds, and the source is known writable or not.
            if (!context.Items.TryGetValue(("income_sources", flow.Item), out var source))
            {
                issues.Add(ImportIssue.Error(ImportCodes.InvalidValue, $"'{flow.Item}' names no entity of income_sources.", path + ".flow_id"));
                continue;
            }
            if (taxable.Currency.Code != flow.Currency)
            {
                issues.Add(ImportIssue.Error(CoreFactCodes.CurrencyMismatch, $"This one-off is in {flow.Currency}: so is its net taxable amount.",
                    path + ".taxable_amount.currency"));
                continue;
            }

            var candidates = known.GetValueOrDefault(record.FlowId)?
                .Select(k => new ImportCandidate(k.Id, k.Hash, k.ImportedBy, [k.Item])).ToList();
            var resolution = await ImportResolver.ResolveAsync(context.Actor, candidates, canRead, belongs: c => c.Items[0] == source);
            if (resolution.OwnHidden)
                continue; // the Core's section reports it

            // Stored for this very flow (the file leaves it as it is): equal is unchanged, other is a replacement.
            decimal? stored = null;
            if (resolution.Row is { } row && row.Hash == flow.Hash)
                stored = (await taxables.ForFlowsAsync([row.Id], ct)).TryGetValue(row.Id, out var current) ? current : null;
            if (stored == taxable.Amount)
            {
                plans.Add(new Plan(record.FlowId, flow.Item, taxable, Mode.Unchanged));
                tally += new SectionCounts(0, 1, 0);
                continue;
            }
            if (stored is not null && !context.Replace)
            {
                issues.Add(ImportIssue.Error(ImportCodes.Conflict,
                    $"'{record.FlowId}' was imported before with another net taxable amount; nothing is overwritten (import-format.md 8).", path));
                tally += new SectionCounts(0, 0, 1);
                continue;
            }

            // A write, as the Core's: a source this import creates is the actor's by construction.
            if (!context.NewItems.Contains(source) && !await WritableAsync(context.Actor, source, writable, ct))
            {
                issues.Add(ImportIssue.Error(ImportCodes.NotPermitted, "This sets the net taxable amount of an income source you cannot change.", path));
                continue;
            }
            plans.Add(new Plan(record.FlowId, flow.Item, taxable, stored is null ? Mode.Created : Mode.Replaced));
            tally += stored is null ? new SectionCounts(1, 0, 0) : new SectionCounts(0, 0, 0, 1);
        }

        counts = new Dictionary<string, SectionCounts> { [Section] = tally };
        return new SectionOutcome(issues, counts);
    }

    public async Task<Result<IReadOnlyDictionary<string, SectionCounts>>> ApplyAsync(ImportContext context, IStagedBatches staged, CancellationToken ct)
    {
        var readable = new Dictionary<ItemId, bool>();
        var canRead = ImportSectionKit.Readable(policy, context.Actor, readable, ct);
        var written = plans.Where(p => p.Mode != Mode.Unchanged).ToList();
        var known = written.Count == 0 ? NoImports : await facts.FindImportedAsync(FactTable.Flow, written.Select(p => p.FlowId).ToList(), ct);
        foreach (var plan in written)
        {
            // The Core has written by now: the flow is found as the Core found it, by its file id on the source (real ids by now).
            if (!context.Items.TryGetValue(("income_sources", plan.Item), out var source))
                return Drifted(plan);
            var candidates = known.GetValueOrDefault(plan.FlowId)?
                .Select(k => new ImportCandidate(k.Id, k.Hash, k.ImportedBy, [k.Item])).ToList();
            if ((await ImportResolver.ResolveAsync(context.Actor, candidates, canRead, belongs: c => c.Items[0] == source)).Row is not { } row)
                return Drifted(plan);
            await taxables.SetAsync(row.Id, source, plan.Taxable.Amount, ct);
        }

        // Whatever the Core did (a correction under a new id, a flow retired by its natural key), no row is left on a flow that
        // is no longer current: the file's sources are swept.
        var sources = sourceRefs.Select(r => context.Items.TryGetValue(("income_sources", r), out var id) ? id : (ItemId?)null)
            .OfType<ItemId>().Distinct().ToList();
        if (sources.Count > 0)
        {
            var current = (await facts.FlowsOfAsync(sources, ct)).Select(f => f.Flow.Id).ToList();
            await taxables.RemoveStaleAsync(sources, current, ct);
        }
        return Result.Success(counts);
    }

    private static Result<IReadOnlyDictionary<string, SectionCounts>> Drifted(Plan plan)
        => ImportSectionKit.Drift([ImportIssue.Error(ImportCodes.UnresolvedReference, $"'{plan.FlowId}' names no one-off income of flows.", Section)]);

    private async Task<bool> WritableAsync(PersonId actor, ItemId item, Dictionary<ItemId, bool> cache, CancellationToken ct)
    {
        if (!cache.TryGetValue(item, out var can))
            cache[item] = can = await ImportSectionKit.CanAsync(policy, actor, item, AccessKind.Write, ct);
        return can;
    }

    /// <summary>
    /// The one-offs that have a net taxable amount, as the Core's flows section exports them (same rows, same file ids), by
    /// file id. The reader's own sources are the items named by the sections before this one.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, IReadOnlyList<JsonElement>>> ExportAsync(ExportContext context, CancellationToken ct)
    {
        var items = context.ItemFileIds.Keys.ToList();
        var stored = items.Count == 0 ? [] : await facts.FlowsOfAsync(items, ct);
        // The ids the Core's section gave them: Assign is deterministic on the same rows (FactsImportSection.ExportAsync).
        var fileIds = FileIds.Assign("fl", stored.Select(f => new KeyedRow(f.Flow.Id, f.Import?.Ref, f.Import?.ImportedBy)).ToList(), context.Reader);
        var oneOffs = stored.Select(s => s.Flow).Where(f => f.Kind == FlowKind.Income && f.Category == IncomeWire.OneOffCategory).ToList();
        var amounts = await taxables.ForFlowsAsync(oneOffs.Select(f => f.Id).ToList(), ct);

        var records = oneOffs
            .Where(f => amounts.ContainsKey(f.Id))
            .Select(f => new IncomeOneOffTaxableRecord
            {
                FlowId = fileIds[f.Id],
                TaxableAmount = new MoneyRecord { Amount = amounts[f.Id], Currency = f.Amount.Currency.Code },
            })
            .OrderBy(r => r.FlowId, StringComparer.Ordinal)
            .Select(r => JsonSerializer.SerializeToElement(r, IncomeFileJson.Records.IncomeOneOffTaxableRecord))
            .ToList();
        return new Dictionary<string, IReadOnlyList<JsonElement>> { [Section] = records };
    }

    private static string? Text(JsonElement element, string member)
        => element.TryGetProperty(member, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>The rules of one entry alone: a file id, an amount in an ISO currency, more than zero.</summary>
    private static bool TryEntry(IncomeOneOffTaxableRecord record, string path, List<ImportIssue> issues, out Money taxable)
    {
        taxable = default;
        if (!NidDataJson.IsFileId(record.FlowId))
        {
            issues.Add(ImportIssue.Error(ImportCodes.InvalidId, "An id is lowercase letters, digits and dashes.", path + ".flow_id"));
            return false;
        }
        if (!Currency.TryCreate(record.TaxableAmount.Currency, out var currency))
        {
            issues.Add(ImportIssue.Error(ImportCodes.InvalidValue, "A currency is an ISO 4217 code in capitals.", path + ".taxable_amount.currency"));
            return false;
        }
        var created = Money.Create(record.TaxableAmount.Amount, currency);
        if (!created.TryGetValue(out taxable))
        {
            created.TryGetFailure(out var failure);
            issues.Add(ImportIssue.Error(failure!.Code, failure.Message, path + ".taxable_amount.amount"));
            return false;
        }
        if (taxable.Amount <= 0)
        {
            issues.Add(ImportIssue.Error(IncomeCodes.InvalidTaxable, "A net taxable amount is more than zero.", path + ".taxable_amount.amount"));
            return false;
        }
        return true;
    }
}
