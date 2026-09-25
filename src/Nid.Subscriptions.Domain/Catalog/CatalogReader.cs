using System.Text.Json;
using Nid.Subscriptions.Contracts;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;

namespace Nid.Subscriptions.Domain.Catalog;

/// <summary>
/// <c>labels.json</c> and <c>providers.json</c> of <c>Mistribe/SubTracker-Data</c> (design §4). Targets the format of
/// design §8 and tolerates today's: <c>#AARRGGBB</c> colours, a duplicate key (the first wins), a category without a
/// label (one is created), a member Nid does not read (<c>plans</c>); an <c>icon</c> that is not an https URL is ignored
/// and reported, its provider kept (1C2c design §5). An entry breaking a rule is refused
/// and listed; a file that is not a JSON array fails the reading, so nothing is written. <c>JsonDocument</c> only:
/// this assembly is AOT-compatible.
/// </summary>
public static class CatalogReader
{
    public const string LabelsFile = "labels";
    public const string ProvidersFile = "providers";

    private static readonly JsonDocumentOptions Options = new() { MaxDepth = 32 };

    private static ReadOnlySpan<byte> Bom => [0xEF, 0xBB, 0xBF];

    public static Result<CatalogReading> Read(byte[] labelsJson, byte[] providersJson)
    {
        using var labels = Parse(labelsJson, "labels.json", out var labelsFailure);
        if (labels is null)
            return Result.Failure<CatalogReading>(labelsFailure!);
        using var providers = Parse(providersJson, "providers.json", out var providersFailure);
        if (providers is null)
            return Result.Failure<CatalogReading>(providersFailure!);

        var refused = new List<CatalogRefusal>();
        var duplicates = new List<CatalogDuplicate>();
        var readLabels = ReadLabels(labels.RootElement, refused, duplicates);
        var known = readLabels.Select(l => l.Key).Append(CatalogLimits.OtherLabel).ToHashSet(StringComparer.Ordinal);
        var created = new SortedSet<string>(StringComparer.Ordinal);
        var iconsRefused = new List<IconRefusal>();
        var readProviders = ReadProviders(providers.RootElement, known, created, refused, duplicates, iconsRefused);

        var allLabels = readLabels
            .Concat(created.Select(key => new CatalogLabel(key, ProviderRules.NameFromKey(key, CatalogLimits.MaxLabelNameLength), CatalogLimits.NeutralColor)))
            .ToList();
        return Result.Success(new CatalogReading(allLabels, readProviders, created.ToList(), duplicates, refused) { IconsRefused = iconsRefused });
    }

    private static List<CatalogLabel> ReadLabels(JsonElement entries, List<CatalogRefusal> refused, List<CatalogDuplicate> duplicates)
    {
        var labels = new List<CatalogLabel>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var index = -1;
        foreach (var entry in entries.EnumerateArray())
        {
            index++;
            if (entry.ValueKind != JsonValueKind.Object)
            {
                refused.Add(new CatalogRefusal(LabelsFile, index, null, CatalogCodes.NotAnObject));
                continue;
            }

            var key = Text(entry, "key");
            if (!ProviderRules.IsKey(key))
            {
                refused.Add(new CatalogRefusal(LabelsFile, index, key, CatalogCodes.InvalidKey));
                continue;
            }
            if (key == CatalogLimits.OtherLabel)
            {
                refused.Add(new CatalogRefusal(LabelsFile, index, key, CatalogCodes.ReservedKey)); // a local label is never touched
                continue;
            }
            if (keys.Contains(key))
            {
                duplicates.Add(new CatalogDuplicate(LabelsFile, key));
                continue;
            }

            var name = ProviderRules.Name(Text(entry, "name"), CatalogLimits.MaxLabelNameLength);
            if (name is null)
            {
                refused.Add(new CatalogRefusal(LabelsFile, index, key, CatalogCodes.InvalidName));
                continue;
            }
            var color = ProviderRules.Color(Text(entry, "color"));
            if (color is null)
            {
                refused.Add(new CatalogRefusal(LabelsFile, index, key, CatalogCodes.InvalidColor));
                continue;
            }

            keys.Add(key);
            labels.Add(new CatalogLabel(key, name, color));
        }

        return labels;
    }

    private static List<CatalogProvider> ReadProviders(
        JsonElement entries, IReadOnlySet<string> knownLabels, SortedSet<string> created,
        List<CatalogRefusal> refused, List<CatalogDuplicate> duplicates, List<IconRefusal> iconsRefused)
    {
        var providers = new List<CatalogProvider>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var index = -1;
        foreach (var entry in entries.EnumerateArray())
        {
            index++;
            if (entry.ValueKind != JsonValueKind.Object)
            {
                refused.Add(new CatalogRefusal(ProvidersFile, index, null, CatalogCodes.NotAnObject));
                continue;
            }

            var key = Text(entry, "key");
            if (!ProviderRules.IsKey(key))
            {
                refused.Add(new CatalogRefusal(ProvidersFile, index, key, CatalogCodes.InvalidKey));
                continue;
            }
            if (keys.Contains(key))
            {
                duplicates.Add(new CatalogDuplicate(ProvidersFile, key));
                continue;
            }

            var name = ProviderRules.Name(Text(entry, "name"), CatalogLimits.MaxNameLength);
            if (name is null)
            {
                refused.Add(new CatalogRefusal(ProvidersFile, index, key, CatalogCodes.InvalidName));
                continue;
            }
            if (!TryUrl(entry, "website", out var website) || !TryUrl(entry, "pricing_page", out var pricingPage))
            {
                refused.Add(new CatalogRefusal(ProvidersFile, index, key, CatalogCodes.InvalidUrl));
                continue;
            }
            var categoryProblem = Categories(entry, out var categories);
            if (categoryProblem is not null)
            {
                refused.Add(new CatalogRefusal(ProvidersFile, index, key, categoryProblem));
                continue;
            }

            foreach (var category in categories.Where(c => !knownLabels.Contains(c)))
                created.Add(category);
            keys.Add(key);
            providers.Add(new CatalogProvider(key, name, website, pricingPage, categories, Icon(entry, key, iconsRefused)));
        }

        return providers;
    }

    /// <summary>The distinct categories in file order, or the reason the entry is refused.</summary>
    private static string? Categories(JsonElement entry, out List<string> categories)
    {
        categories = [];
        if (!entry.TryGetProperty("categories", out var value) || value.ValueKind != JsonValueKind.Array || value.GetArrayLength() == 0)
            return CatalogCodes.NoCategory;

        foreach (var category in value.EnumerateArray())
        {
            var key = category.ValueKind == JsonValueKind.String ? category.GetString() : null;
            if (!ProviderRules.IsKey(key))
                return CatalogCodes.InvalidCategory;
            if (!categories.Contains(key))
                categories.Add(key);
        }

        return null;
    }

    /// <summary>Absent or <c>null</c> is fine; a string must be an address; anything else is refused.</summary>
    private static bool TryUrl(JsonElement entry, string member, out string? url)
    {
        url = null;
        if (!entry.TryGetProperty(member, out var value) || value.ValueKind == JsonValueKind.Null)
            return true;
        if (value.ValueKind != JsonValueKind.String)
            return false;
        url = value.GetString();
        return ProviderRules.IsUrl(url);
    }

    /// <summary>1C2c design §5: absent or <c>null</c> is no icon; anything but an https URL is ignored and reported.</summary>
    private static string? Icon(JsonElement entry, string key, List<IconRefusal> refused)
    {
        if (!entry.TryGetProperty("icon", out var value) || value.ValueKind == JsonValueKind.Null)
            return null;

        var url = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        var problem = url is null ? IconCodes.InvalidUrl : ProviderRules.IconProblem(url);
        if (problem is null)
            return url;

        refused.Add(new IconRefusal(key, problem));
        return null;
    }

    private static string? Text(JsonElement entry, string member)
        => entry.TryGetProperty(member, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static JsonDocument? Parse(byte[] json, string file, out Failure? failure)
    {
        failure = null;
        ReadOnlyMemory<byte> bytes = json;
        if (bytes.Span.StartsWith(Bom)) // some producers write one ahead of the text (1C2a design §10)
            bytes = bytes[Bom.Length..];

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(bytes, Options);
        }
        catch (JsonException)
        {
            failure = new Failure(CatalogCodes.SourceMalformed, $"{file} is not JSON.", null);
            return null;
        }

        if (document.RootElement.ValueKind == JsonValueKind.Array)
            return document;

        document.Dispose();
        failure = new Failure(CatalogCodes.SourceMalformed, $"{file} is not a JSON array.", null);
        return null;
    }
}
