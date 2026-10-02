using Nid.Core.Contracts;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;

namespace Nid.Platform.Contracts;

/// <summary>
/// What every module's <see cref="IImportSection"/> does the same way: reading a section's records, the id and
/// null-element rules, person stand-ins on receipt, access checks for the resolver, and the failures of an apply.
/// </summary>
public static class ImportSectionKit
{
    /// <summary><c>import-format.md</c> §4: an entity's <c>id</c> is a stable file key; <c>import.invalid_id</c> at <c>.id</c>.</summary>
    public static bool TryId(string id, string path, ICollection<ImportIssue> issues)
    {
        if (NidDataJson.IsFileId(id))
            return true;
        issues.Add(ImportIssue.Error(ImportCodes.InvalidId, "An id is lowercase letters, digits and dashes.", path + ".id"));
        return false;
    }

    /// <summary>
    /// System.Text.Json's nullable annotations stop at a collection's elements: <c>"grants":[null]</c> reads as a list
    /// holding null. Refused here, as a malformed entity at the element, before anything reads it.
    /// </summary>
    public static bool NoNulls(IReadOnlyList<object?> elements, string path, ICollection<ImportIssue> issues)
    {
        var ok = true;
        for (var j = 0; j < elements.Count; j++)
            if (elements[j] is null)
            {
                issues.Add(ImportIssue.Error(ImportCodes.MalformedEntity, "A list holds no null element.", $"{path}[{j}]"));
                ok = false;
            }
        return ok;
    }

    /// <summary>
    /// Every staged entity of <paramref name="section"/> that reads as a <typeparamref name="T"/>, has a valid id and passes
    /// <paramref name="elements"/>; the first of each id, a repeat being <c>import.duplicate_id</c> at <c>.id</c>.
    /// <paramref name="unread"/> gets the raw <c>id</c> of an entity that did not read, when it has a string one.
    /// </summary>
    public static List<(StagedEntity Entity, T Record)> ReadAll<T>(
        IStagedBatches staged, string section, Func<T, string> id, List<ImportIssue> issues,
        Func<T, string, List<ImportIssue>, bool>? elements = null, Action<string>? unread = null)
        where T : class
    {
        var records = new List<(StagedEntity, T)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entity in staged.Section(section))
        {
            var path = NidDataJson.Path(section, entity.Index);
            if (!NidDataJson.TryRead<T>(entity.Value, path, issues, out var record) || !TryId(id(record), path, issues)
                || (elements is not null && !elements(record, path, issues)))
            {
                if (unread is not null && entity.Value.ValueKind == System.Text.Json.JsonValueKind.Object
                    && entity.Value.TryGetProperty("id", out var raw) && raw.ValueKind == System.Text.Json.JsonValueKind.String)
                    unread(raw.GetString()!);
                continue;
            }
            if (!seen.Add(id(record)))
            {
                issues.Add(ImportIssue.Error(ImportCodes.DuplicateId, $"'{id(record)}' appears twice in {section}.", path + ".id"));
                continue;
            }
            records.Add((entity, record));
        }
        return records;
    }

    /// <summary>
    /// For a batch checked alone (<see cref="IImportSection.ValidateBatch"/>): every file ref stands for someone, one
    /// distinct adult stand-in per ref, so a ref repeated is still seen.
    /// </summary>
    public static Func<string, ResolvedPerson> StandIns()
    {
        var standIns = new Dictionary<string, ResolvedPerson>(StringComparer.Ordinal);
        return fileRef => standIns.TryGetValue(fileRef, out var standIn)
            ? standIn
            : standIns[fileRef] = new ResolvedPerson(PersonId.New(), PersonKind.Adult);
    }

    public static async Task<bool> CanAsync(IAccessPolicy policy, PersonId actor, ItemId item, AccessKind kind, CancellationToken ct)
        => !(await policy.CheckAsync(actor, item, kind, ct)).TryGetFailure(out _);

    /// <summary>
    /// 1F3b spec §3.2: replacing is a write, on the item as it is now, that never takes it from another adult. The first
    /// refusal, or null. <paramref name="current"/> null (the item vanished) skips the owner and visibility checks;
    /// <paramref name="currency"/> null skips the currency check, otherwise a replacement keeps the item's currency.
    /// <paramref name="noun"/> is the item's name without article ("envelope").
    /// </summary>
    public static async Task<ImportIssue?> ReplaceRefusalAsync(
        IAccessPolicy policy, IItemRules rules, PersonId actor, ItemId id, ItemAccess? current,
        Visibility visibility, IReadOnlyList<Owner> owners, string? currency, string noun, string path,
        Func<Task<IReadOnlyList<PersonId>>> adults, CancellationToken ct)
    {
        if (!await CanAsync(policy, actor, id, AccessKind.Write, ct))
            return ImportIssue.Error(ImportCodes.NotPermitted,
                $"This replaces {(noun[0] is 'a' or 'e' or 'i' or 'o' or 'u' ? "an" : "a")} {noun} you cannot change.", path);
        if (current is null)
            return null;
        if (rules.DropsAnotherAdultOwner(actor, current.Owners, owners))
            return ImportIssue.Error(ImportCodes.NotPermitted, "This replacement removes another adult from the owners.", path + ".ownership");
        if (rules.TakesReadAway(actor, current.Visibility, current.Owners, visibility, owners, await adults()))
            return ImportIssue.Error(ImportCodes.NotPermitted,
                $"This replacement hides the {noun} from another adult who sees it today.", path + ".visibility");
        if (currency is not null && current.Currency != currency)
            return ImportIssue.Error(CoreFactCodes.CurrencyMismatch,
                $"This {noun} is in {current.Currency}: a replacement keeps its currency.", path + ".currency");
        return null;
    }

    /// <summary>The read check <see cref="ImportResolver"/> takes, with one policy call per item, however many rows touch it.</summary>
    public static Func<ItemId, Task<bool>> Readable(IAccessPolicy policy, PersonId actor, Dictionary<ItemId, bool> cache, CancellationToken ct)
        => async item =>
        {
            if (!cache.TryGetValue(item, out var canRead))
                cache[item] = canRead = await CanAsync(policy, actor, item, AccessKind.Read, ct);
            return canRead;
        };

    /// <summary>What validated a moment ago no longer parses: refuse rather than write half of it.</summary>
    public static Result<IReadOnlyDictionary<string, SectionCounts>> Drift(IEnumerable<ImportIssue> issues)
        => Result.Failure<IReadOnlyDictionary<string, SectionCounts>>(new Failure(ImportCodes.ApplyFailed,
            $"The staging changed between validation and writing: {string.Join("; ", issues.Select(i => $"{i.Code} at {i.Path}"))}", null));

    /// <summary>A write of an apply failed: its failure, as the section's.</summary>
    public static Result<IReadOnlyDictionary<string, SectionCounts>> FailureOf<T>(Result<T> result) where T : notnull
        => Result.Failure<IReadOnlyDictionary<string, SectionCounts>>(
            result.TryGetFailure(out var failure) ? failure! : new Failure(ImportCodes.ApplyFailed, "A write failed.", null));

    /// <summary>A write of an apply failed: its failure, as the section's.</summary>
    public static Result<IReadOnlyDictionary<string, SectionCounts>> FailureOf(Result result)
        => Result.Failure<IReadOnlyDictionary<string, SectionCounts>>(
            result.TryGetFailure(out var failure) ? failure! : new Failure(ImportCodes.ApplyFailed, "A write failed.", null));
}
