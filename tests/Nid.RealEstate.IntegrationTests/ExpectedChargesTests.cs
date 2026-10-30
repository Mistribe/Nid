using Microsoft.Extensions.DependencyInjection;
using Nid.Core.Contracts;
using Nid.RealEstate.Contracts;
using Nid.Platform.Contracts;
using Nid.TestSupport;
using NodaTime;
using static Nid.TestSupport.LoanApi;
using static Nid.TestSupport.PropertyApi;

namespace Nid.RealEstate.IntegrationTests;

/// <summary>5C spec §6.1: the loan instalments, planned costs and rents Banking expects to see on a statement.</summary>
public class ExpectedChargesTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static async Task<IReadOnlyList<ExpectedCharge>> ChargesAsync(NidApp app, LocalDate from, LocalDate to)
    {
        await using var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.Browser(app.AdultA);
        var result = await scope.ServiceProvider.GetServices<IExpectedCharges>().Single(c => c.GetType().Name == "RealEstateExpectedCharges")
            .ChargesAsync(app.AdultA, from, to, default);
        return result.TryGetValue(out var charges) ? charges : throw new InvalidOperationException();
    }

    [Fact]
    public async Task A_loan_instalment_includes_insurance()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var body = Short(app); // 1 000 a month from 2025-01-15, no interest
        body["insurances"] = new[] { new { mode = "fixed_monthly", monthlyAmount = "20", startsOn = "2025-01-15" } };
        await NewAsync(app, body);

        var charge = Assert.Single(await ChargesAsync(app, new LocalDate(2025, 3, 1), new LocalDate(2025, 3, 31)));

        Assert.Equal((ChargeKind.LoanInstalment, "2025-03-15", 1020m, false, 5), (charge.Kind, charge.Key, charge.Amount.Amount, charge.Credit, charge.ToleranceDays));
        Assert.Equal(["Banque"], charge.LabelHints);
    }

    [Fact]
    public async Task A_property_cost_and_a_rent()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await NewPropertyAsync(app, Home(app));
        var cost = await CreatedAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Properties}/{id}/recurring-costs", new
        {
            category = "property_tax", amount = "1200", every = new { count = 1, unit = "year" }, startsOn = "2021-10-15",
        }));
        await CreatedAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Properties}/{id}/tenancies",
            new { label = "Bail", startsOn = "2025-01-01", rentMonthly = "750", chargesMonthly = "50" }));

        var charges = await ChargesAsync(app, new LocalDate(2026, 10, 1), new LocalDate(2026, 10, 31));

        var tax = Assert.Single(charges, c => c.Kind == ChargeKind.PropertyCost);
        Assert.Equal(($"cost:{cost:N}:2026-10-15", 1200m, false, new LocalDate(2026, 10, 15)), (tax.Key, tax.Amount.Amount, tax.Credit, tax.DueOn));
        var rent = Assert.Single(charges, c => c.Kind == ChargeKind.Rent);
        Assert.Equal(("rent:2026-10", 800m, true, 10), (rent.Key, rent.Amount.Amount, rent.Credit, rent.ToleranceDays));
    }
}
