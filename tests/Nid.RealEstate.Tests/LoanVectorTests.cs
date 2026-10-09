using System.Globalization;
using System.Text.Json;
using Nid.Core.Contracts;
using Nid.RealEstate.Contracts;
using Nid.RealEstate.Domain;
using NodaTime;
using NodaTime.Text;

namespace Nid.RealEstate.Tests;

public class LoanVectorTests
{
    private static readonly string Path = System.IO.Path.Combine(AppContext.BaseDirectory, "vectors", "loans", "vectors.json");

    private static decimal Dec(JsonElement e) => decimal.Parse(e.GetString()!, NumberStyles.Number, CultureInfo.InvariantCulture);
    private static decimal? OptDec(JsonElement o, string name) => o.TryGetProperty(name, out var v) ? Dec(v) : null;
    private static LocalDate Date(JsonElement e) => LocalDatePattern.Iso.Parse(e.GetString()!).Value;
    private static LocalDate? OptDate(JsonElement o, string name) => o.TryGetProperty(name, out var v) ? Date(v) : null;
    private static T Enum<T>(JsonElement e) where T : struct, System.Enum
        => System.Enum.Parse<T>(e.GetString()!.Replace("_", ""), ignoreCase: true);

    private static LoanTerms Terms(JsonElement t) => new(
        Currency.Eur, Dec(t.GetProperty("principal")), Date(t.GetProperty("disbursed_on")), Date(t.GetProperty("first_payment_date")),
        t.GetProperty("total_payments").GetInt32(), Enum<InterestRounding>(t.GetProperty("rounding")), Enum<FirstPeriod>(t.GetProperty("first_period")),
        t.GetProperty("phases").EnumerateArray().Select(p => new LoanPhase(
            p.GetProperty("seq").GetInt32(), Enum<PhaseMode>(p.GetProperty("mode")), Dec(p.GetProperty("annual_rate")), Enum<RateType>(p.GetProperty("rate_type")),
            p.TryGetProperty("payments", out var n) ? n.GetInt32() : null, OptDec(p, "payment_override"), OptDec(p, "opening_balance_override"))).ToList(),
        t.GetProperty("insurances").EnumerateArray().Select(i => new LoanInsurance(
            Enum<InsuranceMode>(i.GetProperty("mode")), OptDec(i, "monthly_amount"), OptDec(i, "annual_rate"), Date(i.GetProperty("starts_on")), OptDate(i, "ends_on"))).ToList(),
        [],
        t.GetProperty("events").EnumerateArray().Select(e => new EarlyRepayment(
            Date(e.GetProperty("on")), Dec(e.GetProperty("amount")), Enum<RepaymentEffect>(e.GetProperty("effect")))).ToList());

    [Fact]
    public void Every_reference_loan_matches_to_the_cent_whatever_the_host_culture()
    {
        var culture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("fr-FR");
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path));
            var loans = doc.RootElement.GetProperty("loans").EnumerateArray().ToList();
            Assert.Equal(220, loans.Count);
            foreach (var loan in loans)
            {
                var id = loan.GetProperty("id").GetString();
                var built = LoanScheduleBuilder.Build(Terms(loan.GetProperty("terms")));
                Assert.True(built.TryGetValue(out var schedule), $"{id}: refused {built}");
                var expected = loan.GetProperty("rows").EnumerateArray().ToList();
                Assert.True(expected.Count == schedule!.Rows.Count, $"{id}: {schedule.Rows.Count} rows, the reference has {expected.Count}");
                for (var k = 0; k < expected.Count; k++)
                {
                    var e = expected[k].EnumerateArray().Select(Dec).ToArray();
                    var r = schedule.Rows[k];
                    var actual = new[] { r.Payment, r.Interest, r.Principal, r.Insurance, r.Balance };
                    Assert.True(e.SequenceEqual(actual),
                        $"{id} row {k + 1}: expected [{string.Join(", ", e)}], got [{string.Join(", ", actual)}]");
                }
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }
    }
}
