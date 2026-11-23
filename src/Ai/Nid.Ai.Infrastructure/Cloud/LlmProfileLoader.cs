using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Nid.Ai.Application;
using Nid.Ai.Contracts;
using NodaTime;
using NodaTime.Text;

namespace Nid.Ai.Infrastructure.Cloud;

/// <summary>
/// The profiles Nid ships (<c>deploy/llm-profiles/*.json</c>, embedded): read once, each checked. A profile that is
/// malformed, lets its provider train on the data in any region, or names an unknown provider is left out and logged:
/// Nid starts without it rather than calling it (<c>docs/ingestion/llm-providers.md</c> §4).
/// </summary>
public sealed partial class LlmProfileLoader : ILlmProfiles
{
    public static readonly IReadOnlySet<string> Providers = new HashSet<string> { "anthropic", "openai", "gemini", "mistral" };

    public LlmProfileLoader(ILogger<LlmProfileLoader> logger) : this(Embedded(), logger)
    {
    }

    public LlmProfileLoader(IEnumerable<(string Name, string Json)> files, ILogger<LlmProfileLoader> logger)
    {
        var loaded = new List<LlmProfile>();
        foreach (var (name, json) in files)
        {
            if (Read(json) is { } profile && loaded.All(p => p.Id != profile.Id))
                loaded.Add(profile);
            else
                LogRefused(logger, name);
        }

        All = loaded;
    }

    public IReadOnlyList<LlmProfile> All { get; }

    /// <summary>The profile, or null when it does not hold (§4): shape, provider, host, terms.</summary>
    public static LlmProfile? Read(string json)
    {
        ProfileFile? file;
        try
        {
            file = JsonSerializer.Deserialize(json, ProfileJson.Default.ProfileFile);
        }
        catch (JsonException)
        {
            return null;
        }

        if (file is null || file.Id is not { Length: > 0 and <= 64 } || !Providers.Contains(file.Provider)
            || file.Tier is not ("free" or "paid") || Uri.CheckHostName(file.Host) != UriHostNameType.Dns || file.Model is not { Length: > 0 and <= 200 }
            || file.Capabilities.Count == 0 || file.DataTerms is not { } terms
            || !CloudRegions.All.All(terms.TrainsOnData.ContainsKey) || terms.TrainsOnData.Values.Any(trains => trains)
            || !Uri.TryCreate(terms.TermsUrl, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps
            || LocalDatePattern.Iso.Parse(terms.ReviewedOn) is not { Success: true } reviewed
            || (file.Tier == "paid" && file.Pricing is null)
            || file.ContextTokens is not (>= 1024 and <= 10_000_000))
            return null;

        return new LlmProfile(file.Id, file.Provider, file.Tier, file.Host, file.Model, file.Capabilities,
            new DataTerms(terms.TrainsOnData, terms.HumanReview, terms.RetentionDays, terms.RetentionSwitches ?? [], terms.TermsUrl, reviewed.Value),
            file.Quotas, file.Pricing, file.ContextTokens.Value);
    }

    private static IEnumerable<(string Name, string Json)> Embedded()
    {
        var assembly = typeof(LlmProfileLoader).Assembly;
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.StartsWith("llm-profiles.", StringComparison.Ordinal)).Order())
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            yield return (name, reader.ReadToEnd());
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "The cloud profile {Name} does not hold (shape, provider, host or terms): it is not loaded")]
    private static partial void LogRefused(ILogger logger, string name);
}

internal sealed record ProfileFile(
    string Id, string Provider, string Tier, string Host, string Model, IReadOnlyList<string> Capabilities, TermsFile? DataTerms,
    Quotas? Quotas = null, Pricing? Pricing = null, int? ContextTokens = null);

internal sealed record TermsFile(
    IReadOnlyDictionary<string, bool> TrainsOnData, bool HumanReview, string TermsUrl, string ReviewedOn, int? RetentionDays = null,
    IReadOnlyList<string>? RetentionSwitches = null);

/// <summary>The profile files: camelCase, no unknown field (a typo in a term is not a term silently dropped).</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true)]
[JsonSerializable(typeof(ProfileFile))]
internal sealed partial class ProfileJson : JsonSerializerContext;
