using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Nid.Core.Contracts;
using Nid.Investments.Contracts;
using Nid.Platform.Contracts;
using NodaTime;
using NodaTime.Text;
using UnambitiousFx.Functional;

namespace Nid.Investments.Application.Import;

public sealed partial class HoldingsImportSection
{
    /// <summary>
    /// import-format.md §8 for a row under an envelope (<paramref name="noun"/>; <paramref name="member"/> is the file member that ties
    /// it to the envelope): unchanged, a conflict, replaced, or created; a write needs the actor to change the envelope. A stored row
    /// never moves to another envelope. Null when refused, the issue added.
    /// </summary>
    private async Task<(Mode Mode, Guid Id, PersonId ImportedBy)?> ResolveRowAsync(string section, string noun, StagedEntity entity, string fileId,
        string path, string member, EnvelopePlan envelope, IReadOnlyDictionary<string, IReadOnlyList<ImportedHoldingRow>> known,
        IReadOnlySet<Guid> storedIds, List<ImportIssue> issues, CancellationToken ct)
    {
        // Every importer's rows under this id: only ImportResolver's answer may reach an issue.
        var candidates = known.GetValueOrDefault(fileId)?.Select(k => new ImportCandidate(k.Id, k.Hash, k.ImportedBy, [k.Envelope])).ToList();
        var resolution = await ImportResolver.ResolveAsync(context.Actor, candidates, canRead, belongs: c => c.Items[0] == envelope.Id);
        if (resolution.OwnHidden)
        {
            issues.Add(ImportIssue.Error(ImportCodes.NotPermitted, $"'{fileId}' was imported before as a {noun} you cannot see.", path));
            return null;
        }

        var (mode, id, importedBy) = (Mode.Created, Guid.CreateVersion7(), context.Actor);
        if (resolution.Row is { } row)
        {
            if (row.Items[0] != envelope.Id || !storedIds.Contains(row.Id))
            {
                issues.Add(ImportIssue.Error(ImportCodes.InvalidValue,
                    $"A {noun} stays on its envelope: give the {noun} of the other envelope an id of its own.", path + "." + member));
                return null;
            }
            if (row.Hash == entity.Hash)
                return (Mode.Unchanged, row.Id, row.ImportedBy ?? context.Actor);
            if (!context.Replace)
            {
                issues.Add(ImportIssue.Error(ImportCodes.Conflict,
                    $"'{fileId}' was imported before with other content; nothing is overwritten (import-format.md §8).", path));
                Tally(section, new SectionCounts(0, 0, 1));
                return null;
            }
            (mode, id, importedBy) = (Mode.Replaced, row.Id, row.ImportedBy ?? context.Actor);
        }

        // A write: an existing envelope needs the actor to change it; one this import creates is theirs by construction.
        if (!(envelope.Writable ??= envelope.Created || await ImportSectionKit.CanAsync(policy, context.Actor, envelope.Id, AccessKind.Write, ct)))
        {
            issues.Add(ImportIssue.Error(ImportCodes.NotPermitted, mode == Mode.Created
                ? $"This adds a {noun} to an envelope you cannot change."
                : $"This replaces a {noun} of an envelope you cannot change.", path));
            return null;
        }
        return (mode, id, importedBy);
    }

    /// <summary>A row validated: planned, and counted.</summary>
    private RowPlan<T> Plan<T>(List<RowPlan<T>> plans, string section, string fileId, string path, EnvelopePlan envelope,
        (Mode Mode, Guid Id, PersonId ImportedBy) row, StagedEntity entity, T value)
    {
        var plan = new RowPlan<T>(fileId, path, envelope, row.Mode, new ImportMark(fileId, entity.Hash, row.ImportedBy), value);
        plans.Add(plan);
        Tally(section, row.Mode switch
        {
            Mode.Created => new SectionCounts(1, 0, 0),
            Mode.Unchanged => new SectionCounts(0, 1, 0),
            _ => new SectionCounts(0, 0, 0, 1),
        });
        return plan;
    }

    private void Tally(string section, SectionCounts delta) => tally[section] = tally.GetValueOrDefault(section, SectionCounts.Zero) + delta;

    private static bool Read<T>(JsonElement entity, string path, List<ImportIssue> issues, Func<T, string> id, out T record) where T : class
        => NidDataJson.TryRead(entity, path, issues, out record!) && ImportSectionKit.TryId(id(record), path, issues);

    /// <summary>A holding record's dates parsed (none bounded by today: a maturity is ahead); <see cref="HoldingRules"/> checks the rest.</summary>
    private static bool TryHolding(HoldingRecord r, string path, List<ImportIssue> issues, out HoldingDraft draft)
    {
        var ok = EnvelopeParsing.TryDate(r.OpenedOn, path + ".opened_on", null, issues, out var openedOn)
                 & EnvelopeParsing.TryDate(r.MaturesOn, path + ".matures_on", null, issues, out var maturesOn)
                 & EnvelopeParsing.TryDate(r.ClosedOn, path + ".closed_on", null, issues, out var closedOn);
        draft = new HoldingDraft(r.Name, r.Isin, r.Kind, r.AssetClass, r.Issuer, r.RiskIndicator, openedOn, maturesOn, r.EntryCostPct, closedOn, r.Note);
        return ok;
    }

    /// <summary>An amount with its currency; whether it is the envelope's is the API's rule, checked after.</summary>
    private static bool TryMoney(MoneyRecord r, string path, List<ImportIssue> issues, out Money money)
    {
        money = default;
        if (Currency.TryCreate(r.Currency, out var currency) && Money.Create(r.Amount, currency).TryGetValue(out money))
            return true;
        issues.Add(ImportIssue.Error(ImportCodes.InvalidValue, "An amount has at most 4 decimals, in an ISO 4217 currency in capitals.", path));
        return false;
    }

    /// <summary>The API's refusal at the file member its <c>field</c> names (<c>lines[0].holdingId</c> → <c>lines[0].holding</c>).</summary>
    private static bool Check<T>(Result<T> result, string path, List<ImportIssue> issues, out T value) where T : notnull
    {
        if (result.TryGetValue(out value!))
            return true;
        result.TryGetFailure(out var failure);
        var member = failure!.Metadata is { } metadata && metadata.TryGetValue("field", out var field) && field is string name ? "." + Member(name) : "";
        issues.Add(ImportIssue.Error(failure.Code, failure.Message, path + member));
        return false;
    }

    private static string Member(string apiField)
        => Capital().Replace(apiField.Replace("holdingId", "holding").Replace("envelopeFlowId", "envelope_flow"), m => "_" + char.ToLowerInvariant(m.Value[0]));

    [GeneratedRegex("[A-Z]")]
    private static partial Regex Capital();

    private static ImportIssue Warning(HoldingWarning warning, string path) => ImportIssue.Warning("investments." + warning.Code, warning.Code switch
    {
        HoldingWarnings.ValueMismatch => "Units × unit price differs from the value as printed by more than 0.01.",
        HoldingWarnings.SumMismatch => "The lines' total differs from the envelope's value on this date by more than 1.",
        HoldingWarnings.AllocationMismatch => "On this date, the allocations do not sum to the envelope's payments in, or to its withdrawals.",
        _ => "The two legs of this switch differ by more than 1.",
    }, path);

    private async Task<decimal?> StoredValuationAsync(ItemId envelope, LocalDate on, CancellationToken ct)
        => (await facts.ValuationsOfAsync([envelope], ct)).FirstOrDefault(v => v.Valuation.AsOf == on)?.Valuation.Value.Amount;

    private static string? Iso(LocalDate? date) => date is { } d ? LocalDatePattern.Iso.Format(d) : null;

    private static IReadOnlyList<JsonElement> Elements<T>(IEnumerable<T> records)
        => records.Select(r => JsonSerializer.SerializeToElement(r, NidDataJson.Options)).ToList();

    /// <summary>
    /// The Core's values and flows of this file, read as far as the warnings need them; anything malformed is the Core section's
    /// to report.
    /// </summary>
    private static FileFacts StagedFacts(IStagedBatches staged)
    {
        var result = new FileFacts();
        foreach (var entity in staged.Section("valuations"))
            if (Text(entity.Value, "item") is { } item && Date(entity.Value, "as_of") is { } on && Amount(entity.Value) is { } amount)
                result.Valuations[(item, on)] = amount;
        foreach (var entity in staged.Section("flows"))
        {
            if (Text(entity.Value, "id") is not { } id || Text(entity.Value, "item") is not { } item)
                continue;
            result.FlowItems.TryAdd(id, item);
            if (Date(entity.Value, "occurred_on") is { } on && Amount(entity.Value) is { } amount
                && Text(entity.Value, "kind") switch { "opening" => FlowKind.Opening, "contribution" => FlowKind.Contribution, "withdrawal" => FlowKind.Withdrawal, _ => (FlowKind?)null }
                    is { } kind)
                result.FlowRows.Add((item, on, kind, amount));
        }
        return result;

        static string? Text(JsonElement e, string member)
            => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(member, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        static LocalDate? Date(JsonElement e, string member)
            => Text(e, member) is { } text && LocalDatePattern.Iso.Parse(text) is { Success: true } parse ? parse.Value : null;

        static decimal? Amount(JsonElement e)
        {
            if (!e.TryGetProperty("amount", out var money) || money.ValueKind != JsonValueKind.Object || !money.TryGetProperty("amount", out var amount))
                return null;
            return amount.ValueKind switch
            {
                JsonValueKind.Number when amount.TryGetDecimal(out var number) => number,
                JsonValueKind.String when decimal.TryParse(amount.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) => parsed,
                _ => null,
            };
        }
    }

    private sealed class FileFacts
    {
        public Dictionary<(string Item, LocalDate On), decimal> Valuations { get; } = [];
        public Dictionary<string, string> FlowItems { get; } = new(StringComparer.Ordinal);
        public List<(string Item, LocalDate On, FlowKind Kind, decimal Amount)> FlowRows { get; } = [];

        public decimal? Valuation(string item, LocalDate on) => Valuations.TryGetValue((item, on), out var amount) ? amount : null;

        /// <summary>The envelope a flow of the file goes to, by the flow's file id; null when the file has no such flow.</summary>
        public string? FlowItem(string fileId) => FlowItems.GetValueOrDefault(fileId);

        public IEnumerable<Flow> Flows(string item, ItemId id, Currency currency)
            => FlowRows.Where(f => f.Item == item && Money.Create(f.Amount, currency).TryGetValue(out _))
                .Select(f => new Flow(Guid.Empty, id, f.On, f.Kind, Money.Create(f.Amount, currency).TryGetValue(out var m) ? m : default, null, null, null));
    }
}
