namespace Nid.Tax.Domain;

/// <summary>The wire names of the engine's choices: an adult's employment status, the rental regime.</summary>
public static class TaxWire
{
    public static bool TryStatus(string? wire, out EmploymentStatus status)
    {
        (var ok, status) = wire switch
        {
            "non_executive" => (true, EmploymentStatus.NonExecutive),
            "executive" => (true, EmploymentStatus.Executive),
            "public" => (true, EmploymentStatus.Public),
            _ => (false, default),
        };
        return ok;
    }

    public static string Wire(EmploymentStatus status) => status switch
    {
        EmploymentStatus.Executive => "executive",
        EmploymentStatus.Public => "public",
        _ => "non_executive",
    };

    public static bool TryRegime(string? wire, out RentalRegime regime)
    {
        (var ok, regime) = wire switch { "micro" => (true, RentalRegime.Micro), "real" => (true, RentalRegime.Real), _ => (false, default) };
        return ok;
    }

    public static string Wire(RentalRegime regime) => regime == RentalRegime.Real ? "real" : "micro";
}
