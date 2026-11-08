using System.Text.Json;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.Subscriptions.Contracts;
using Nid.Subscriptions.Domain;
using UnambitiousFx.Functional;

namespace Nid.Subscriptions.Application.Import;

/// <summary>
/// The section <c>subscription_insurances</c> (issue #208, design §11): what a subscription's policy covers. It has its own
/// section because <c>subscriptions</c> runs before <c>properties</c>, so a subscription could not name a property of the
/// same file; it runs after the real estate module (<see cref="Order"/>), when both the subscription and the property
/// have their real ids in <see cref="ImportContext.Items"/>. <c>subscription</c> and <c>property</c> name an entity of this
/// file, any other id is <c>import.unresolved_reference</c>; at most one insurance names a subscription.
/// </summary>
public sealed class InsuranceImportSection(ISubscriptionStore store, IAccessPolicy policy) : IImportSection
{
    public const string Section = "subscription_insurances";

    /// <summary>Sections of other modules this one points at, not referenced across modules.</summary>
    private const string SubscriptionsSection = "subscriptions";
    private const string PropertiesSection = "properties";

    /// <summary>The prefix of the file id an export derives for an insurance that has no import reference.</summary>
    private const string IdPrefix = "ins";

    private enum Mode { Created, Unchanged, Replaced }

    /// <summary>An entry as validation decided it; the ids it names are read again from the context at apply, when they are real.</summary>
    private sealed record Plan(StagedEntity Entity, InsuranceRecord Record, Mode Mode, PersonId ImportedBy);

    private readonly List<Plan> plans = [];
    private IReadOnlyDictionary<string, SectionCounts> counts = new Dictionary<string, SectionCounts>();

    public string Module => "subscriptions";
    public IReadOnlyList<string> Sections => [Section];

    /// <summary>After the real estate module (40), before the Core's facts (90).</summary>
    public int Order => 45;

    public IReadOnlyList<ImportIssue> ValidateBatch(string section, JsonElement entities, int firstIndex)
    {
        var issues = new List<ImportIssue>();
        var position = 0;
        foreach (var entity in entities.EnumerateArray())
        {
            var path = NidDataJson.Path(section, firstIndex + position++);
            if (NidDataJson.TryRead(entity, SubscriptionsFileJson.Records.InsuranceRecord, path, issues, out var record)
                && ImportSectionKit.TryId(record.Id, path, issues))
                TryRecord(record, path, issues, out _);
        }
        return issues;
    }

    public async Task<SectionOutcome> ValidateAllAsync(ImportContext context, IStagedBatches staged, CancellationToken ct)
    {
        plans.Clear();
        var issues = new List<ImportIssue>();
        var tally = SectionCounts.Zero;
        var records = ImportSectionKit.ReadAll(staged, Section, SubscriptionsFileJson.Records.InsuranceRecord, r => r.Id, issues,
            (r, path, found) => TryRecord(r, path, found, out _));
        var known = await store.FindImportedAsync(ImportedTable.Insurance, records.Select(r => r.Record.Id).ToList(), ct);
        var canRead = ImportSectionKit.Readable(policy, context.Actor, new Dictionary<ItemId, bool>(), ct);
        var writable = new Dictionary<ItemId, bool>();
        var seen = new HashSet<string>(StringComparer.Ordinal); // subscriptions an insurance of this file names

        foreach (var (entity, record) in records)
        {
            var path = NidDataJson.Path(Section, entity.Index);

            // A subscription or property the file refused is reported there, once.
            if (context.FailedItems.Contains((SubscriptionsSection, record.Subscription))
                || (record.Property is { } named && context.FailedItems.Contains((PropertiesSection, named))))
                continue;
            if (!context.Items.TryGetValue((SubscriptionsSection, record.Subscription), out var subscription))
            {
                issues.Add(ImportIssue.Error(ImportCodes.UnresolvedReference, $"'{record.Subscription}' names no entity of {SubscriptionsSection}.", path + ".subscription"));
                continue;
            }
            if (record.Property is { } property && !context.Items.ContainsKey((PropertiesSection, property)))
            {
                // Only a property of this file: the importer reads it (they created it, or the section resolved it as one they read).
                issues.Add(ImportIssue.Error(ImportCodes.UnresolvedReference, $"'{property}' names no entity of {PropertiesSection}.", path + ".property"));
                continue;
            }
            if (!seen.Add(record.Subscription))
            {
                issues.Add(ImportIssue.Error(ImportCodes.DuplicateId, $"'{record.Subscription}' already has an insurance in {Section}.", path + ".subscription"));
                continue;
            }

            var candidates = known.GetValueOrDefault(record.Id)?
                .Select(k => new ImportCandidate(k.Id, k.Hash, k.ImportedBy, k.Items)).ToList();
            var resolution = await ImportResolver.ResolveAsync(context.Actor, candidates, canRead, belongs: c => c.Items.Contains(subscription));
            if (resolution.OwnHidden)
            {
                issues.Add(ImportIssue.Error(ImportCodes.NotPermitted, $"'{record.Id}' was imported before as an insurance you cannot see.", path));
                continue;
            }

            var mode = Mode.Created;
            var importedBy = context.Actor;
            if (resolution.Row is { } row)
            {
                if (row.Items[0] != subscription)
                {
                    issues.Add(ImportIssue.Error(ImportCodes.InvalidValue,
                        $"'{record.Id}' was imported on another subscription: give the insurance of this one an id of its own.", path + ".subscription"));
                    continue;
                }
                if (row.Hash == entity.Hash)
                {
                    // A UI edit keeps the provenance, so the hash stays that of the last imported file: the same file again is
                    // unchanged and the edit stays, as for a subscription (§8).
                    plans.Add(new Plan(entity, record, Mode.Unchanged, row.ImportedBy ?? context.Actor));
                    tally += new SectionCounts(0, 1, 0);
                    continue;
                }
                if (!context.Replace)
                {
                    issues.Add(Conflict(record.Id, path, "was imported before with other content"));
                    tally += new SectionCounts(0, 0, 1);
                    continue;
                }
                (mode, importedBy) = (Mode.Replaced, row.ImportedBy ?? context.Actor);
            }
            else if (!context.NewItems.Contains(subscription) && (await store.FindInsuranceAsync(subscription, ct)) is not null)
            {
                // The one row a subscription may have is not this file id's: one set on the screen, or imported under another id.
                if (!context.Replace)
                {
                    issues.Add(Conflict(record.Id, path, "names a subscription that already has an insurance"));
                    tally += new SectionCounts(0, 0, 1);
                    continue;
                }
                mode = Mode.Replaced;
            }

            // A write, as the subscriptions section's: a subscription this import creates is the actor's by construction.
            if (!context.NewItems.Contains(subscription) && !await WritableAsync(context.Actor, subscription, writable, ct))
            {
                issues.Add(ImportIssue.Error(ImportCodes.NotPermitted, mode == Mode.Created
                    ? "This adds an insurance to a subscription you cannot change."
                    : "This replaces the insurance of a subscription you cannot change.", path));
                continue;
            }

            plans.Add(new Plan(entity, record, mode, importedBy));
            tally += mode == Mode.Created ? new SectionCounts(1, 0, 0) : new SectionCounts(0, 0, 0, 1);
        }

        counts = new Dictionary<string, SectionCounts> { [Section] = tally };
        return new SectionOutcome(issues, counts);
    }

    public async Task<Result<IReadOnlyDictionary<string, SectionCounts>>> ApplyAsync(ImportContext context, IStagedBatches staged, CancellationToken ct)
    {
        foreach (var plan in plans.Where(p => p.Mode != Mode.Unchanged))
        {
            // The sections before have written: both ids are real by now.
            if (!context.Items.TryGetValue((SubscriptionsSection, plan.Record.Subscription), out var subscription))
                return Drifted(plan, ".subscription", plan.Record.Subscription, SubscriptionsSection);
            ItemId? property = null;
            if (plan.Record.Property is { } named)
            {
                if (!context.Items.TryGetValue((PropertiesSection, named), out var resolved))
                    return Drifted(plan, ".property", named, PropertiesSection);
                property = resolved;
            }

            if (!TryRecord(plan.Record, NidDataJson.Path(Section, plan.Entity.Index), [], out var details))
                return ImportSectionKit.Drift([ImportIssue.Error(ImportCodes.InvalidValue, "The insurance no longer reads.", Section)]);
            var set = await store.SetInsuranceAsync(subscription, details with { PropertyId = property },
                new ImportRef(plan.Record.Id, plan.Entity.Hash, plan.ImportedBy), ct);
            if (set.TryGetFailure(out _))
                return ImportSectionKit.FailureOf(set);
        }
        return Result.Success(counts);
    }

    /// <summary>
    /// The insurance of every subscription the reader exports (named by the subscriptions section, which runs before this
    /// one). A property is named only when the reader exports it: one they cannot read is not in the file, and nothing here
    /// names it, so the record is written without <c>property</c> and re-importing it loses the link (design §11).
    /// </summary>
    public async Task<IReadOnlyDictionary<string, IReadOnlyList<JsonElement>>> ExportAsync(ExportContext context, CancellationToken ct)
    {
        var stored = (await store.ReadInsurancesAsync(context.ItemFileIds.Keys.ToList(), ct)).Values.ToList();
        var fileIds = FileIds.Assign(IdPrefix,
            stored.Select(s => new KeyedRow(s.SubscriptionId.Value, s.Import?.Ref, s.Import?.ImportedBy)).ToList(), context.Reader);

        var records = stored
            .Select(s => new InsuranceRecord
            {
                Id = fileIds[s.SubscriptionId.Value],
                Subscription = context.ItemFileIds[s.SubscriptionId],
                Kind = s.Details.Kind.ToString().ToLowerInvariant(),
                Property = s.Details.PropertyId is { } linked && context.ItemFileIds.TryGetValue(linked, out var propertyId) ? propertyId : null,
                InsuredLabel = s.Details.InsuredLabel,
                PolicyNumber = s.Details.PolicyNumber,
                InsurerContact = s.Details.InsurerContact,
                Guarantees = s.Details.Guarantees.Select(g => new GuaranteeRecord { Label = g.Label, Ceiling = g.Ceiling, Deductible = g.Deductible }).ToList(),
            })
            .OrderBy(r => r.Id, StringComparer.Ordinal)
            .Select(r => JsonSerializer.SerializeToElement(r, SubscriptionsFileJson.Records.InsuranceRecord))
            .ToList();
        return new Dictionary<string, IReadOnlyList<JsonElement>> { [Section] = records };
    }

    /// <summary>
    /// The rules of one entry alone: file ids, a kind, no null guarantee and the screens' limits (<see cref="InsuranceRules"/>),
    /// at the member of the file that carries them. <paramref name="details"/> names the property by a placeholder when there is one.
    /// </summary>
    private static bool TryRecord(InsuranceRecord record, string path, List<ImportIssue> issues, out InsuranceDetails details)
    {
        details = new InsuranceDetails(InsuranceKind.Other, null, null, null, null, []);
        if (!NidDataJson.IsFileId(record.Subscription))
        {
            issues.Add(ImportIssue.Error(ImportCodes.InvalidId, "An id is lowercase letters, digits and dashes.", path + ".subscription"));
            return false;
        }
        if (record.Property is { } property && !NidDataJson.IsFileId(property))
        {
            issues.Add(ImportIssue.Error(ImportCodes.InvalidId, "An id is lowercase letters, digits and dashes.", path + ".property"));
            return false;
        }
        if (!TryKind(record.Kind, out var kind))
        {
            issues.Add(ImportIssue.Error(ImportCodes.InvalidValue, "A kind is home, car, health, provident or other.", path + ".kind"));
            return false;
        }
        if (!ImportSectionKit.NoNulls(record.Guarantees, path + ".guarantees", issues))
            return false;

        details = new InsuranceDetails(
            kind, record.Property is null ? null : new ItemId(Guid.Empty), record.InsuredLabel, record.PolicyNumber, record.InsurerContact,
            record.Guarantees.Select(g => new InsuranceGuarantee(g.Label, g.Ceiling, g.Deductible)).ToList());
        if (InsuranceRules.Validate(details) is not { } issue)
            return true;
        issues.Add(ImportIssue.Error(issue.Code, issue.Message, path + "." + FilePath(issue)));
        return false;
    }

    private static bool TryKind(string text, out InsuranceKind kind)
    {
        (var known, kind) = text switch
        {
            "home" => (true, InsuranceKind.Home),
            "car" => (true, InsuranceKind.Car),
            "health" => (true, InsuranceKind.Health),
            "provident" => (true, InsuranceKind.Provident),
            "other" => (true, InsuranceKind.Other),
            _ => (false, InsuranceKind.Other),
        };
        return known;
    }

    /// <summary>The file's snake_case member of a refused rule (<see cref="InsurancePaths.Api"/> is the API's camelCase).</summary>
    private static string FilePath(InsuranceIssue issue) => issue.Field switch
    {
        InsuranceField.Guarantees => "guarantees",
        InsuranceField.GuaranteeLabel => $"guarantees[{issue.Index}].label",
        InsuranceField.GuaranteeCeiling => $"guarantees[{issue.Index}].ceiling",
        InsuranceField.GuaranteeDeductible => $"guarantees[{issue.Index}].deductible",
        InsuranceField.InsuredLabel => "insured_label",
        InsuranceField.PolicyNumber => "policy_number",
        _ => "insurer_contact",
    };

    private static ImportIssue Conflict(string id, string path, string why)
        => ImportIssue.Error(ImportCodes.Conflict, $"'{id}' {why}; nothing is overwritten (import-format.md §8).", path);

    private static Result<IReadOnlyDictionary<string, SectionCounts>> Drifted(Plan plan, string member, string fileId, string section)
        => ImportSectionKit.Drift([ImportIssue.Error(ImportCodes.UnresolvedReference, $"'{fileId}' names no entity of {section}.", Section + "." + plan.Record.Id + member)]);

    private async Task<bool> WritableAsync(PersonId actor, ItemId item, Dictionary<ItemId, bool> cache, CancellationToken ct)
    {
        if (!cache.TryGetValue(item, out var can))
            cache[item] = can = await ImportSectionKit.CanAsync(policy, actor, item, AccessKind.Write, ct);
        return can;
    }
}
