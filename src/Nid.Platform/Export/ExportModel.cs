using System.Buffers.Text;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Nid.Core.Contracts;
using Nid.Platform.Canonical;
using Nid.Platform.Contracts;

namespace Nid.Platform.Export;

public sealed record ExportSection(string Name, IReadOnlyList<JsonElement> Entities);

/// <summary>
/// The reader's whole export (1D2 design §2 to §4), rebuilt on every manifest and page call: nothing is stored and
/// nothing is locked, and <see cref="Revision"/> tells two builds apart exactly when their content differs.
/// </summary>
public sealed record ExportModel(IReadOnlyList<ExportSection> Sections, string Revision, IReadOnlyCollection<ItemId> Items)
{
    public static async Task<ExportModel> BuildAsync(
        PersonId reader, IReadOnlyList<PersonInfo> persons, IEnumerable<IImportSection> sections, CancellationToken ct)
    {
        var context = new ExportContext(reader, Refs(persons));
        var modules = sections.ToList();

        // Descending order: item sections mark the persons they name before persons (order 0) reads them (§2).
        var exported = new Dictionary<string, IReadOnlyList<JsonElement>>(StringComparer.Ordinal);
        foreach (var section in modules.OrderByDescending(s => s.Order).ThenBy(s => s.Module, StringComparer.Ordinal))
        {
            foreach (var (name, entities) in await section.ExportAsync(context, ct))
            {
                if (!section.Sections.Contains(name))
                    throw new InvalidOperationException($"Module '{section.Module}' exported '{name}', which is not one of its sections.");
                exported[name] = entities;
            }
        }

        // File order: modules by ascending order, each section in the order the module lists them (import-format.md §3).
        var ordered = modules.OrderBy(s => s.Order).ThenBy(s => s.Module, StringComparer.Ordinal)
            .SelectMany(s => s.Sections)
            .Select(name => new ExportSection(name, exported.GetValueOrDefault(name) ?? []))
            .ToList();

        return new ExportModel(ordered, Fingerprint(ordered), context.Exposed.ToList());
    }

    /// <summary>§5.4: every person the reader can see, adults first, by what a file carries, then id; numbered per kind.</summary>
    private static Dictionary<PersonId, (string Ref, PersonInfo Info)> Refs(IReadOnlyList<PersonInfo> persons)
    {
        var refs = new Dictionary<PersonId, (string, PersonInfo)>();
        foreach (var group in persons.GroupBy(p => p.Kind))
        {
            var prefix = group.Key == PersonKind.Adult ? "adult" : "dependent";
            var n = 0;
            foreach (var person in group.OrderBy(p => p.DisplayName, StringComparer.Ordinal)
                         .ThenBy(p => p.Email ?? "", StringComparer.Ordinal).ThenBy(p => p.Id.Value))
                refs[person.Id] = ($"{prefix}-{++n}", person);
        }
        return refs;
    }

    private static string Fingerprint(IReadOnlyList<ExportSection> sections)
    {
        var array = new JsonArray();
        foreach (var section in sections)
        {
            var entities = new JsonArray();
            foreach (var entity in section.Entities)
                entities.Add(JsonNode.Parse(entity.GetRawText()));
            array.Add(new JsonObject { ["section"] = section.Name, ["entities"] = entities });
        }
        using var document = JsonDocument.Parse(array.ToJsonString());
        return Rfc8785.Hash(document.RootElement);
    }
}

/// <summary>The opaque page cursor of §3: base64url of <c>{"r": revision, "o": offset}</c>.</summary>
public static class ExportCursor
{
    public static string Encode(string revision, int offset)
        => Base64Url.EncodeToString(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new Payload(revision, offset))));

    public static bool TryDecode(string cursor, out string revision, out int offset)
    {
        (revision, offset) = ("", 0);
        try
        {
            var payload = JsonSerializer.Deserialize<Payload>(Base64Url.DecodeFromChars(cursor));
            if (payload is null || payload.R is null || payload.O < 0)
                return false;
            (revision, offset) = (payload.R, payload.O);
            return true;
        }
        catch (Exception e) when (e is FormatException or JsonException)
        {
            return false;
        }
    }

    private sealed record Payload(
        [property: System.Text.Json.Serialization.JsonPropertyName("r")] string R,
        [property: System.Text.Json.Serialization.JsonPropertyName("o")] int O);
}
