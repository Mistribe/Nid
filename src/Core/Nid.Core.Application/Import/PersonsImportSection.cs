using System.Text.Json;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using UnambitiousFx.Functional;

namespace Nid.Core.Application.Import;

/// <summary>
/// Section <c>persons</c> (<c>import-format.md</c> §6). An import never creates an adult: an adult is the map's
/// choice, or the account with the same email. A dependent is the one with the same name, or a new one —
/// created only at commit.
/// </summary>
public sealed class PersonsImportSection(IPersonRegistry persons) : IImportSection
{
    public const string Section = "persons";

    private readonly List<(string Ref, string DisplayName)> planned = [];
    private int reused;

    public string Module => "core";
    public IReadOnlyList<string> Sections => [Section];
    public int Order => 0;

    public IReadOnlyList<ImportIssue> ValidateBatch(string section, JsonElement entities, int firstIndex)
    {
        var issues = new List<ImportIssue>();
        var position = 0;
        foreach (var entity in entities.EnumerateArray())
            Read(entity, firstIndex + position++, issues);
        return issues;
    }

    public async Task<SectionOutcome> ValidateAllAsync(ImportContext context, IStagedBatches staged, CancellationToken ct)
    {
        planned.Clear();
        reused = 0;
        var issues = new List<ImportIssue>();

        var records = new List<(int Index, PersonRecord Record)>();
        var refs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entity in staged.Section(Section))
        {
            if (Read(entity.Value, entity.Index, issues) is not { } record)
                continue;
            if (!refs.Add(record.Ref))
                issues.Add(ImportIssue.Error(ImportCodes.DuplicateId, $"'{record.Ref}' appears twice.", NidDataJson.Path(Section, entity.Index, "ref")));
            else
                records.Add((entity.Index, record));
        }

        var everyone = await persons.ListAsync(ct);
        var adults = everyone.Where(p => p.Kind == PersonKind.Adult).ToList();
        var dependents = everyone.Where(p => p.Kind == PersonKind.Dependent).ToList();
        var candidates = string.Join(", ", adults.Select(a => $"{a.DisplayName} ({a.Id})"));
        var claimed = new Dictionary<PersonId, string>();

        foreach (var (index, record) in records)
        {
            var path = NidDataJson.Path(Section, index);
            if (record.Kind == "adult")
            {
                PersonId? found = null;
                if (context.AdultMap.TryGetValue(record.Ref, out var mapped))
                {
                    if (adults.Any(a => a.Id == mapped))
                        found = mapped;
                    else
                    {
                        issues.Add(ImportIssue.Error(ImportCodes.AdultMapInvalid,
                            $"The map sends '{record.Ref}' to someone who is not an adult of this household. Adults: {candidates}.", path));
                        continue;
                    }
                }
                else if (record.Email is { } email)
                {
                    var matches = adults.Where(a => string.Equals(a.Email?.Trim(), email.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
                    if (matches.Count == 1)
                        found = matches[0].Id;
                }

                if (found is not { } adult)
                    issues.Add(ImportIssue.Error(ImportCodes.AdultUnresolved,
                        $"No account matches '{record.Ref}': map it to one of {candidates}.", path));
                else if (!claimed.TryAdd(adult, record.Ref))
                    issues.Add(ImportIssue.Error(ImportCodes.AdultDuplicate,
                        $"'{record.Ref}' and '{claimed[adult]}' are the same account.", path));
                else
                {
                    context.Persons[record.Ref] = new ResolvedPerson(adult, PersonKind.Adult);
                    reused++;
                }
            }
            else
            {
                var name = record.DisplayName.Trim();
                var matches = dependents.Where(d => string.Equals(d.DisplayName, name, StringComparison.OrdinalIgnoreCase)).ToList();
                if (matches.Count > 1 || planned.Any(p => string.Equals(p.DisplayName, name, StringComparison.OrdinalIgnoreCase)))
                    issues.Add(ImportIssue.Error(ImportCodes.DependentAmbiguous,
                        $"Several dependents are called '{name}': rename one so each is found once.", path));
                else if (matches.Count == 1)
                {
                    context.Persons[record.Ref] = new ResolvedPerson(matches[0].Id, PersonKind.Dependent);
                    reused++;
                }
                else
                {
                    context.Persons[record.Ref] = new ResolvedPerson(PersonId.New(), PersonKind.Dependent); // planned
                    planned.Add((record.Ref, name));
                }
            }
        }

        return new SectionOutcome(issues, Counts());
    }

    public async Task<Result<IReadOnlyDictionary<string, SectionCounts>>> ApplyAsync(ImportContext context, IStagedBatches staged, CancellationToken ct)
    {
        foreach (var (reference, displayName) in planned)
        {
            var created = await persons.CreateDependentAsync(displayName, ct);
            if (!created.TryGetValue(out var person))
                return created.TryGetFailure(out var failure)
                    ? Result.Failure<IReadOnlyDictionary<string, SectionCounts>>(failure!)
                    : throw new InvalidOperationException("A failed result carries a failure.");
            context.Persons[reference] = new ResolvedPerson(person.Id, PersonKind.Dependent);
        }

        return Result.Success(Counts());
    }

    private IReadOnlyDictionary<string, SectionCounts> Counts()
        => new Dictionary<string, SectionCounts> { [Section] = new(planned.Count, reused, 0) };

    private static PersonRecord? Read(JsonElement entity, int index, List<ImportIssue> issues)
    {
        var path = NidDataJson.Path(Section, index);
        if (!NidDataJson.TryRead(entity, CoreFileJson.Records.PersonRecord, path, issues, out var record))
            return null;

        var valid = true;
        if (!NidDataJson.IsFileId(record.Ref))
        {
            issues.Add(ImportIssue.Error(ImportCodes.InvalidId, "A ref is lowercase letters, digits and dashes.", NidDataJson.Path(Section, index, "ref")));
            valid = false;
        }
        if (record.Kind is not ("adult" or "dependent"))
        {
            issues.Add(ImportIssue.Error(ImportCodes.InvalidValue, "A person is an adult or a dependent.", NidDataJson.Path(Section, index, "kind")));
            valid = false;
        }
        if (string.IsNullOrWhiteSpace(record.DisplayName))
        {
            issues.Add(ImportIssue.Error(ImportCodes.InvalidValue, "A person has a name.", NidDataJson.Path(Section, index, "display_name")));
            valid = false;
        }

        return valid ? record : null;
    }

    /// <summary>1D2 design §5.4: the persons the other sections referenced, adults with their email.</summary>
    public Task<IReadOnlyDictionary<string, IReadOnlyList<JsonElement>>> ExportAsync(ExportContext context, CancellationToken ct)
    {
        // A snapshot: PersonRef marks what it is given, which is the set being read.
        var records = context.Referenced.ToList()
            .Select(id => (Ref: context.PersonRef(id), Info: context.Person(id)))
            .OrderBy(p => p.Info.Kind).ThenBy(p => p.Ref.Length).ThenBy(p => p.Ref, StringComparer.Ordinal)
            .Select(p => JsonSerializer.SerializeToElement(new PersonRecord
            {
                Ref = p.Ref,
                Kind = p.Info.Kind == PersonKind.Adult ? "adult" : "dependent",
                DisplayName = p.Info.DisplayName,
                Email = p.Info.Kind == PersonKind.Adult ? p.Info.Email : null,
            }, CoreFileJson.Records.PersonRecord))
            .ToList();
        return Task.FromResult<IReadOnlyDictionary<string, IReadOnlyList<JsonElement>>>(
            new Dictionary<string, IReadOnlyList<JsonElement>> { [Section] = records });
    }
}
