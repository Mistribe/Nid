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

/// <summary>The month's budget for paid providers (#488): its ceiling (0 € by default: no paid call), what was spent, what
/// open calls hold back, what is left, and where it stands. The amounts are the server's.</summary>
/// <remarks>Amounts as decimal strings in euros (<c>"12.50"</c>), as every amount Nid sends.</remarks>
public sealed record AiBudgetView(string MonthlyBudgetEur, LocalDate Month, string SpentEur, string ReservedEur, string RemainingEur, string Status);

/// <summary><c>off</c> (no ceiling: no paid call), <c>ok</c>, <c>alert</c> (80 % spent), <c>reached</c> (blocked until the 1st).</summary>
public static class BudgetStatus
{
    public const string Off = "off";
    public const string Ok = "ok";
    public const string Alert = "alert";
    public const string Reached = "reached";
}
