using NodaTime;

namespace Nid.Childcare.Domain;

/// <summary>
/// A contract's figures against the legal minimums in force over a period: the hourly rate (IDCC 3239 annexe 5) and the upkeep
/// for 9 hours (90 % of the MG, <see cref="ChildcareRules"/>). A warning, never a refusal: the pay already floors the upkeep at the
/// minimum (<see cref="Upkeep.ForDay"/>), and validated months stay as they are. Pure.
/// </summary>
public static class LegalMinimums
{
    /// <summary>The same strings as <c>ChildcareCodes</c>; the Domain does not reference the codes' project.</summary>
    public static class Codes
    {
        public const string GrossRate = "childcare.below_minimum_gross_rate";
        public const string NetRate = "childcare.below_minimum_net_rate";
        public const string Upkeep = "childcare.below_minimum_upkeep";
    }

    /// <summary>A conventional minimum hourly wage per child, gross and its net equivalent, from <paramref name="From"/>.</summary>
    public sealed record HourlyMinimum(LocalDate From, decimal Gross, decimal Net);

    /// <summary>
    /// A revaluation is one new row. The legal floor (0,281 × SMIC, CASF art. D.423-9) stays below these, so it is not listed.
    /// 3,64 € (2,84 € net) from 1 April 2025: avenant n° 9 du 25 novembre 2024 à l'annexe 5, Légifrance KALIARTI000051315270, extended by
    /// the arrêté of 25 February 2025 (JO 18 March 2025); net per the DREETS Pays de la Loire sheet of January 2026.
    /// 4,20 € (3,28 € net) from 1 June 2026: avenant n° 10 du 5 février 2026, extended by the arrêté of 4 May 2026 (JO 8 May 2026);
    /// DREETS Pays de la Loire, « Rémunération, indemnités et frais » of 12 June 2026.
    /// shortcut: no row before April 2025 (the 3,50 € of avenant n° 8 has no net figure on a primary source), add one if a contract needs it.
    /// shortcut: the 4 % uplift of the AM-AP title (4,37 €) is not checked, the terms do not record the title; add a flag to the terms to check it.
    /// </summary>
    public static readonly IReadOnlyList<HourlyMinimum> Hourly = [new(new(2025, 4, 1), 3.64m, 2.84m), new(new(2026, 6, 1), 4.20m, 3.28m)];

    /// <summary>A minimum the figure falls below: its code, the date the minimum took effect, the minimum and the figure.</summary>
    public sealed record Breach(string Code, LocalDate Since, decimal Minimum, decimal Value);

    /// <summary>The latest revaluation either table knows of.</summary>
    public static LocalDate LastChange => LocalDate.Max(Hourly[^1].From, ChildcareRules.For(LocalDate.MaxIsoValue).From);

    /// <summary>
    /// The minimums broken on <paramref name="on"/>. Minimums only rise, so checking a period is checking its last day. The hourly rate
    /// is checked gross when the terms have one, net otherwise.
    /// </summary>
    public static IReadOnlyList<Breach> Check(decimal netHourlyRate, decimal? grossHourlyRate, decimal upkeepPer9h, LocalDate on)
    {
        var breaches = new List<Breach>();
        if (Hourly.LastOrDefault(h => h.From <= on) is { } hourly)
        {
            if (grossHourlyRate is { } gross && gross < hourly.Gross) breaches.Add(new(Codes.GrossRate, hourly.From, hourly.Gross, gross));
            else if (grossHourlyRate is null && netHourlyRate < hourly.Net) breaches.Add(new(Codes.NetRate, hourly.From, hourly.Net, netHourlyRate));
        }
        if (on >= ChildcareRules.FirstDay && ChildcareRules.For(on) is var rules && upkeepPer9h < rules.Upkeep9hMinimum)
            breaches.Add(new(Codes.Upkeep, rules.From, rules.Upkeep9hMinimum, upkeepPer9h));
        return breaches;
    }
}
