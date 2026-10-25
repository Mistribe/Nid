using System.Buffers.Text;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
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
        var modules = sections.ToList();

        // Two passes: the first only finds who is referenced, so the second numbers those persons alone (§5.4) and a
        // ref does not depend on who else lives in the household — a fresh instance gives the same refs back.
        var probe = new ExportContext(reader, Refs(persons, persons.Select(p => p.Id).ToHashSet()));
        await ExportAllAsync(modules, probe, ct);
        var context = new ExportContext(reader, Refs(persons, probe.Referenced.ToHashSet()));
        var exported = await ExportAllAsync(modules, context, ct);

        // File order: modules by ascending order, each section in the order the module lists them (import-format.md §3).
        var ordered = modules.OrderBy(s => s.Order).ThenBy(s => s.Module, StringComparer.Ordinal)
            .SelectMany(s => s.Sections)
            .Select(name => new ExportSection(name, exported.GetValueOrDefault(name) ?? []))
            .ToList();

        return new ExportModel(ordered, Fingerprint(ordered), context.Exposed.ToList());
    }

    /// <summary>
    /// 2B1b: ascending order, so a fact section (order 90) finds the file id of every item an item section (10, 20…)
    /// exported; persons (order 0) last, since it reads whom the others referenced (§2).
    /// </summary>
    private static async Task<Dictionary<string, IReadOnlyList<JsonElement>>> ExportAllAsync(
        IReadOnlyList<IImportSection> modules, ExportContext context, CancellationToken ct)
    {
        var exported = new Dictionary<string, IReadOnlyList<JsonElement>>(StringComparer.Ordinal);
        foreach (var section in modules.OrderBy(s => s.Order == 0 ? 1 : 0).ThenBy(s => s.Order).ThenBy(s => s.Module, StringComparer.Ordinal))
        {
            foreach (var (name, entities) in await section.ExportAsync(context, ct))
            {
                if (!section.Sections.Contains(name))
                    throw new InvalidOperationException($"Module '{section.Module}' exported '{name}', which is not one of its sections.");
                exported[name] = entities;
            }
        }
        return exported;
    }

    /// <summary>
    /// §5.4: adults first, by what a file carries (name, email), then id; numbered per kind. The referenced persons get
    /// the first numbers; the others follow, only so that a write between the two passes never finds a person without a ref.
    /// </summary>
    private static Dictionary<PersonId, (string Ref, PersonInfo Info)> Refs(IReadOnlyList<PersonInfo> persons, IReadOnlySet<PersonId> referenced)
    {
        var refs = new Dictionary<PersonId, (string, PersonInfo)>();
        foreach (var group in persons.GroupBy(p => p.Kind))
        {
            var prefix = group.Key == PersonKind.Adult ? "adult" : "dependent";
            var n = 0;
            foreach (var person in group.OrderBy(p => referenced.Contains(p.Id) ? 0 : 1).ThenBy(p => p.DisplayName, StringComparer.Ordinal)
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
public static partial class ExportCursor
{
    [JsonSerializable(typeof(Payload))]
    private sealed partial class CursorJson : JsonSerializerContext;

    public static string Encode(string revision, int offset)
        => Base64Url.EncodeToString(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new Payload(revision, offset), CursorJson.Default.Payload)));

    public static bool TryDecode(string cursor, out string revision, out int offset)
    {
        (revision, offset) = ("", 0);
        try
        {
            var payload = JsonSerializer.Deserialize(Base64Url.DecodeFromChars(cursor), CursorJson.Default.Payload);
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
        [property: JsonPropertyName("r")] string R,
        [property: JsonPropertyName("o")] int O);
}
