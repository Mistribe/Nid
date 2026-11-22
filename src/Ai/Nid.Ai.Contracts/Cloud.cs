using NodaTime;

namespace Nid.Ai.Contracts;

/// <summary>Where the household is, for a provider's terms (<c>docs/ingestion/llm-providers.md</c> §4): the EEA, Switzerland and
/// the UK, or elsewhere. An admin confirms it; a profile whose terms train on the data there is never used.</summary>
public static class CloudRegions
{
    public const string EeaChUk = "eea_ch_uk";
    public const string Other = "other";

    public static readonly IReadOnlyList<string> All = [EeaChUk, Other];
}

/// <summary>A cloud profile as Réglages › IA shows it: what it is, what its terms say, whether the household uses it.</summary>
public sealed record CloudProfileView(
    string Id,
    string Provider,
    string Tier,
    string Host,
    string Model,
    bool TrainsOnData,
    bool HumanReview,
    int? RetentionDays,
    string TermsUrl,
    LocalDate ReviewedOn,
    bool ReviewDue,
    bool Eligible,
    bool Enabled,
    string? KeyHint,
    bool Suspended);

/// <summary>The household's cloud settings: its region (none until an admin confirms it) and every profile Nid ships.</summary>
public sealed record CloudSettingsView(string? Region, IReadOnlyList<CloudProfileView> Profiles);
