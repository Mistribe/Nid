using System.Text.Json;
using Nid.Platform.Contracts;
using Nid.Subscriptions.Application.Catalog;
using Nid.Subscriptions.Contracts;
using Nid.Subscriptions.Domain.Catalog;
using UnambitiousFx.Functional;

namespace Nid.Subscriptions.Application.Import;

/// <summary>
/// Section <c>providers</c> (import-format.md §5.2, design §5 "Import"), validated before <c>subscriptions</c>. An entry
/// resolves, in order, to a provider the importer imported under its reference (unchanged, or a conflict when the
/// content differs); to one imported under that reference with the same content that the importer can see (the other
/// adult importing the same file: unchanged, reused); otherwise to a new custom provider with a generated key. A
/// provider the importer cannot see is never matched, so it is never revealed (Review Focus 3).
/// </summary>
internal sealed class ProvidersImport(IProviderStore providers, ProviderAccess access, ProviderKeys keys)
{
    public const string Section = "providers";

    private readonly Dictionary<string, string?> resolved = new(StringComparer.Ordinal); // file key → stored key; null: created at commit
    private readonly HashSet<string> failed = new(StringComparer.Ordinal);                 // entries already reported
    private readonly List<(StagedEntity Entity, ProviderRecord Record)> planned = [];

    public static IReadOnlyList<ImportIssue> ValidateBatch(JsonElement entities, int firstIndex)
    {
        var issues = new List<ImportIssue>();
        var position = 0;
        foreach (var entity in entities.EnumerateArray())
        {
            var path = NidDataJson.Path(Section, firstIndex + position++);
            if (NidDataJson.TryRead<ProviderRecord>(entity, path, issues, out var record))
                Check(record, path, issues);
        }

        return issues;
    }

    public async Task<SectionCounts> ValidateAllAsync(ImportContext context, IStagedBatches staged, List<ImportIssue> issues, CancellationToken ct)
    {
        resolved.Clear();
        failed.Clear();
        planned.Clear();

        var labels = (await providers.LabelsAsync(ct)).Select(l => l.Key).ToHashSet(StringComparer.Ordinal);
        var records = new List<(StagedEntity Entity, ProviderRecord Record)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entity in staged.Section(Section))
        {
            var path = NidDataJson.Path(Section, entity.Index);
            if (!NidDataJson.TryRead<ProviderRecord>(entity.Value, path, issues, out var record))
                continue;
            if (!Check(record, path, issues))
            {
                failed.Add(record.Key);
                continue;
            }
            if (!seen.Add(record.Key))
            {
                issues.Add(ImportIssue.Error(ImportCodes.DuplicateId, $"'{record.Key}' appears twice in providers.", path + ".key"));
                continue;
            }

            var unknownLabel = false;
            for (var j = 0; j < record.LabelKeys.Count; j++)
            {
                if (labels.Contains(record.LabelKeys[j]))
                    continue;
                issues.Add(ImportIssue.Error(ImportCodes.UnresolvedReference, $"'{record.LabelKeys[j]}' is not a label.", $"{path}.label_keys[{j}]"));
                unknownLabel = true;
            }
            if (unknownLabel)
            {
                failed.Add(record.Key);
                continue;
            }

            records.Add((entity, record));
        }

        var known = await providers.FindImportedAsync(records.Select(r => r.Record.Key).ToList(), ct);
        var visible = await access.FindAsync(context.Actor, known.Select(p => p.Key).ToList(), ct);
        var counts = SectionCounts.Zero;
        foreach (var (entity, record) in records)
        {
            // Only a provider the importer may still link is reused: visible and not archived, the rule of every write
            // (final review H4). The importer's own archived one keeps its reference (unique per importer), so the entry
            // is a conflict; another adult's archived one is left alone and the entry gets a new provider.
            var mine = known.FirstOrDefault(p => p.Import!.Ref == record.Key && p.CreatedBy == context.Actor);
            if (mine is not null)
            {
                if (mine.IsArchived)
                {
                    issues.Add(ImportIssue.Error(ImportCodes.Conflict,
                        $"'{record.Key}' was imported before and its provider is archived; nothing is overwritten (import-format.md §8).",
                        NidDataJson.Path(Section, entity.Index)));
                    failed.Add(record.Key);
                    counts += new SectionCounts(0, 0, 1);
                    continue;
                }

                if (mine.Import!.Hash != entity.Hash)
                {
                    issues.Add(ImportIssue.Error(ImportCodes.Conflict,
                        $"'{record.Key}' was imported before with other content; nothing is overwritten (import-format.md §8).",
                        NidDataJson.Path(Section, entity.Index)));
                    failed.Add(record.Key);
                    counts += new SectionCounts(0, 0, 1);
                    continue;
                }

                resolved[record.Key] = mine.Key;
                counts += new SectionCounts(0, 1, 0);
                continue;
            }

            var shared = known.FirstOrDefault(p => p.Import!.Ref == record.Key && p.Import.Hash == entity.Hash && visible.ContainsKey(p.Key) && !p.IsArchived);
            if (shared is not null)
            {
                resolved[record.Key] = shared.Key;
                counts += new SectionCounts(0, 1, 0);
                continue;
            }

            resolved[record.Key] = null;
            planned.Add((entity, record));
            counts += new SectionCounts(1, 0, 0);
        }

        return counts;
    }

    /// <summary>The keys among <paramref name="keys"/> no file entry names that the importer may link: visible, not archived.</summary>
    public async Task<IReadOnlySet<string>> LinkableAsync(ImportContext context, IReadOnlyCollection<string> keys, CancellationToken ct)
    {
        var outside = keys.Where(k => !resolved.ContainsKey(k) && !failed.Contains(k)).Distinct(StringComparer.Ordinal).ToList();
        var found = await access.FindAsync(context.Actor, outside, ct);
        return found.Values.Where(p => !p.IsArchived).Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>A file entry names it (the file comes first), or the importer may link it (design §5 "Import").</summary>
    public bool Resolves(string key, IReadOnlySet<string> linkable) => resolved.ContainsKey(key) || linkable.Contains(key);

    /// <summary>A providers entry of that key was refused: it already says why, a subscription naming it adds nothing.</summary>
    public bool Failed(string key) => failed.Contains(key);

    public async Task<Result> ApplyAsync(ImportContext context, CancellationToken ct)
    {
        foreach (var (entity, record) in planned)
        {
            var name = ProviderRules.Name(record.Name, CatalogLimits.MaxNameLength)!;
            var provider = new Provider(
                await keys.NewAsync(name, ct), ProviderOrigin.Custom, name,
                ProviderRules.Url(record.Website), ProviderRules.Url(record.PricingPage),
                record.LabelKeys.Distinct(StringComparer.Ordinal).ToList(), null, context.Actor, null,
                new ImportRef(record.Key, entity.Hash));
            var created = await providers.CreateAsync(provider, ct);
            if (created.TryGetFailure(out var failure))
                return Result.Failure(failure!);
            resolved[record.Key] = provider.Key;
        }

        return Result.Success();
    }

    /// <summary>What a subscription stores for its <c>provider_key</c>: a file entry's stored key, else the key as written.</summary>
    public string? StoredKey(string? key)
        => key is not null && resolved.TryGetValue(key, out var stored) && stored is not null ? stored : key;

    /// <summary>The rules of one entry alone, on receipt and again at validation. Every issue is reported, not the first.</summary>
    private static bool Check(ProviderRecord record, string path, List<ImportIssue> issues)
    {
        var ok = true;
        if (!NidDataJson.IsFileId(record.Key))
            ok = Refuse(issues, ImportCodes.InvalidId, "A key is lowercase letters, digits and dashes.", path + ".key");
        if (ProviderRules.Name(record.Name, CatalogLimits.MaxNameLength) is null)
            ok = Refuse(issues, ImportCodes.InvalidValue, $"A provider has a name of 1 to {CatalogLimits.MaxNameLength} characters.", path + ".name");
        if (!ProviderRules.IsUrl(ProviderRules.Url(record.Website)))
            ok = Refuse(issues, ImportCodes.InvalidValue, "A website is an http or https address of at most 2 048 characters.", path + ".website");
        if (!ProviderRules.IsUrl(ProviderRules.Url(record.PricingPage)))
            ok = Refuse(issues, ImportCodes.InvalidValue, "A pricing page is an http or https address of at most 2 048 characters.", path + ".pricing_page");
        if (record.LabelKeys.Count == 0)
            ok = Refuse(issues, ImportCodes.InvalidValue, "A provider has at least one label.", path + ".label_keys");
        for (var j = 0; j < record.LabelKeys.Count; j++)
            if (record.LabelKeys[j] is null) // nullable annotations stop at a collection's elements (RecordParsing.TryElements)
                ok = Refuse(issues, ImportCodes.MalformedEntity, "A list holds no null element.", $"{path}.label_keys[{j}]");
        return ok;
    }

    private static bool Refuse(List<ImportIssue> issues, string code, string message, string path)
    {
        issues.Add(ImportIssue.Error(code, message, path));
        return false;
    }
}
