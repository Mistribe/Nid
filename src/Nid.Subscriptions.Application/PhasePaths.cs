namespace Nid.Subscriptions.Application;

/// <summary>Which field of a phase an engine refusal concerns (1E2a design §2.1).</summary>
public static class PhasePaths
{
    /// <summary>The field of a phase a refusal names, or null when it concerns the phase as a whole.</summary>
    public static string? Field(string code) => code switch
    {
        "subscriptions.phase_range" => "endsOn",
        "subscriptions.max_cycles" => "maxCycles",
        "subscriptions.duration_count" => "every.count",
        _ => null,
    };

    public static string Of(string code, string prefix) => Field(code) is { } field ? $"{prefix}.{field}" : prefix;
}
