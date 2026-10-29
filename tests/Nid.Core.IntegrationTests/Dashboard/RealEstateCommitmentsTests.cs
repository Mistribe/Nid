using System.Text.Json;
using Nid.Core.Contracts;
using Nid.TestSupport;
using static Nid.TestSupport.LoanApi;
using static Nid.TestSupport.PropertyApi;

namespace Nid.Core.IntegrationTests.Dashboard;

/// <summary>3E1 spec §3: the loans' and the planned costs' commitments, each its own source. This month is October 2025.</summary>
public class RealEstateCommitmentsTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static async Task<JsonElement> DashboardAsync(NidApp app, PersonId reader, string scope = "household")
        => JsonDocument.Parse(await app.GetRawAsync(reader, $"/api/v1/dashboard?scope={scope}")).RootElement;

    private static string? Total(JsonElement figure)
        => figure.GetProperty("total").ValueKind == JsonValueKind.Null ? null : figure.GetProperty("total").GetProperty("amount").GetString();

    private static IEnumerable<string> Sources(JsonElement view)
        => view.GetProperty("commitmentSources").EnumerateArray()
            .Select(s => $"{s.GetProperty("source").GetString()} {s.GetProperty("amortized").GetProperty("amount").GetString()}");

    private static async Task<Guid> CostAsync(NidApp app, PersonId actor, Guid property, object body)
        => await CreatedAsync(await app.RawAsync(actor, HttpMethod.Post, $"{Properties}/{property}/recurring-costs", body));

    /// <summary><see cref="Short"/> (12 000 at 0 %, twelve payments of 1 000), disbursed and first paid on the given dates.</summary>
    private static Dictionary<string, object?> Loan(NidApp app, string name, string disbursedOn, string firstPaymentDate)
    {
        var body = Short(app, name);
        body["disbursedOn"] = disbursedOn;
        body["firstPaymentDate"] = firstPaymentDate;
        return body;
    }

    [Fact]
    public async Task A_loans_payment_and_insurance_and_the_planned_costs_are_two_sources()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var loan = Short(app, "Prêt travaux"); // 1 000 on the 15th
        loan["insurances"] = new[] { new { mode = "fixed_monthly", monthlyAmount = "20", startsOn = "2025-01-15" } };
        await NewAsync(app, loan);
        var home = await NewPropertyAsync(app, Home(app));
        var year = new { count = 1, unit = "year" };
        await CostAsync(app, app.AdultA, home, new { category = "property_tax", amount = "1200", every = year, startsOn = "2020-10-20" }); // 100 a month; due 2025-10-20
        await CostAsync(app, app.AdultA, home, new { category = "condo_fees", amount = "150", every = new { count = 1, unit = "month" }, startsOn = "2025-01-05" });
        await CostAsync(app, app.AdultA, home, new { category = "insurance", amount = "300", every = year, startsOn = "2020-07-01", endsOn = "2024-07-01" }); // ended

        var view = await DashboardAsync(app, app.AdultA);

        Assert.Equal("1270.00", Total(view.GetProperty("commitments")));   // 1 020 + 100 + 150
        Assert.Equal("2370.00", Total(view.GetProperty("cashThisMonth"))); // 1 020 + 1 200 + 150
        Assert.Equal(new[] { "loans 1020.00", "property_costs 250.00" }, Sources(view));
        Assert.Equal(0, view.GetProperty("subscriptions").GetInt32());
    }

    [Fact]
    public async Task A_loan_not_yet_paying_counts_with_nothing_due_and_one_ended_earlier_this_month_still_pays_this_month()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await NewAsync(app, Loan(app, "Prêt neuf", "2025-10-01", "2025-11-15"));
        await NewAsync(app, Loan(app, "Prêt soldé", "2024-01-01", "2024-01-15")); // last payment 2024-12-15: counts nowhere

        var waiting = await DashboardAsync(app, app.AdultA);
        Assert.Null(Total(waiting.GetProperty("commitments"))); // the tile reads « Aucun coût ce mois-ci »: the source is listed
        Assert.Equal(new[] { "loans 0.00" }, Sources(waiting));

        await NewAsync(app, Loan(app, "Prêt fini ce mois-ci", "2024-11-01", "2024-11-05")); // last payment 2025-10-05
        var ending = await DashboardAsync(app, app.AdultA);
        Assert.Equal("1000.00", Total(ending.GetProperty("commitments")));
        Assert.Equal("1000.00", Total(ending.GetProperty("cashThisMonth")));
        Assert.Equal(new[] { "loans 1000.00" }, Sources(ending));
    }

    [Theory]
    [InlineData("household")]
    [InlineData("me")]
    public async Task The_other_adults_private_loan_and_property_commit_nothing(string scope)
    {
        await using var app = await NidApp.StartAsync(fixture);
        var loan = Short(app, "Secret");
        loan["visibility"] = "private";
        loan["borrowers"] = new[] { new { personId = app.AdultB.Value, shareBp = 10_000 } };
        await NewAsync(app, loan, app.AdultB);
        var secret = await NewPropertyAsync(app, Home(app, "Secret", "private", app.AdultB), app.AdultB);
        await CostAsync(app, app.AdultB, secret, new { category = "property_tax", amount = "1200", every = new { count = 1, unit = "year" }, startsOn = "2020-10-20" });

        var view = await DashboardAsync(app, app.AdultA, scope);

        Assert.Null(Total(view.GetProperty("commitments")));
        Assert.Null(Total(view.GetProperty("cashThisMonth")));
        Assert.Empty(Sources(view));
    }
}
