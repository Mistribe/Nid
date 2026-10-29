using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Nid.Income.Application;

namespace Nid.Core.IntegrationTests.TaxEstimate;

/// <summary>Tax-per spec §3.3: the lines the Income module gives the estimate for the payslip's figures.</summary>
public class IncomeTaxLinesTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static object Money(string amount) => new { amount, currency = "EUR" };

    private static async Task PhaseAsync(NidApp app, PersonId owner, ItemId source, string label, string startsOn, string net, string? gross, string? taxable)
        => (await app.RawAsync(owner, HttpMethod.Post, $"/api/v1/income/sources/{source.Value}/phases", new
        {
            label, startsOn, netAmount = Money(net), every = new { count = 1, unit = "month" }, maxCycles = 1,
            grossAmount = gross is null ? null : Money(gross), taxableAmount = taxable is null ? null : Money(taxable),
        })).EnsureSuccessStatusCode();

    private static async Task OneOffAsync(NidApp app, PersonId owner, ItemId source, string on, string amount, string? taxable, string note)
        => await Ok(await app.RawAsync(owner, HttpMethod.Post, $"/api/v1/income/sources/{source.Value}/one-offs", new
        {
            occurredOn = on, amount = Money(amount), note, taxableAmount = taxable is null ? null : Money(taxable),
        }));

    private static async Task Ok(HttpResponseMessage response)
        => Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());

    private static async Task<IReadOnlyList<TaxLine>> LinesAsync(NidApp app, PersonId reader, VisibilityScope scope)
    {
        await using var s = app.Services.CreateAsyncScope();
        s.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.Browser(reader, false);
        var lines = await s.ServiceProvider.GetServices<ITaxContribution>().OfType<IncomeSourcesTax>().Single()
            .TaxLinesAsync(reader, scope, 2025, new NodaTime.LocalDate(2025, 12, 31), default);
        Assert.True(lines.TryGetValue(out var value));
        return value!;
    }

    private static decimal Sum(IEnumerable<TaxLine> lines, string code) => lines.Where(l => l.Code == code).Sum(l => l.Amount);

    [Fact]
    public async Task Each_payment_goes_to_the_line_of_what_it_holds()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var source = await app.NewIncomeSourceAsync("Salaire A", app.AdultA);
        await PhaseAsync(app, app.AdultA, source, "Complet", "2025-01-31", "3200", "5000", "4000");
        await PhaseAsync(app, app.AdultA, source, "Brut", "2025-02-28", "3900", "5000", null);
        await PhaseAsync(app, app.AdultA, source, "Net", "2025-03-31", "3000", null, null);
        await OneOffAsync(app, app.AdultA, source, "2025-04-15", "1500", "1200", "Prime");

        var lines = await LinesAsync(app, app.AdultA, VisibilityScope.Me);

        Assert.Equal(5200m, Sum(lines, TaxLineCodes.SalaryTaxable));
        Assert.Equal(5000m, Sum(lines, TaxLineCodes.SalaryGross));
        Assert.Equal(3000m, Sum(lines, TaxLineCodes.SalaryNet));
        Assert.Equal(0m, Sum(lines, TaxLineCodes.SalaryOneOff));
        Assert.Equal(5000m, Sum(lines, TaxLineCodes.SalaryRateGross));
        Assert.Equal(4000m, Sum(lines, TaxLineCodes.SalaryRateTaxable));
        var details = lines.Where(l => l.Code == TaxLineCodes.SalaryDetail).OrderBy(l => l.On).ToList();
        Assert.Equal(
            [("2025-01-31", "entered", 4000m), ("2025-02-28", "gross", 5000m), ("2025-03-31", "net", 3000m), ("2025-04-15", "entered", 1200m)],
            details.Select(d => (d.On!.Value.ToString("yyyy-MM-dd", null), d.Category!, d.Amount)).ToArray());
        Assert.Equal("Prime", details[3].Note);
        Assert.Equal("Complet", details[0].Note);
        Assert.All(details, d => Assert.Equal(app.AdultA, d.Person));
    }

    [Fact]
    public async Task Salary_detail_of_the_other_adults_private_source_stays_out()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var theirs = await app.NewIncomeSourceAsync("Salaire B", app.AdultB, visibility: "private");
        await PhaseAsync(app, app.AdultB, theirs, "Complet", "2025-01-31", "3200", "5000", "4000");

        foreach (var scope in new[] { VisibilityScope.Me, VisibilityScope.Household })
        {
            var lines = await LinesAsync(app, app.AdultA, scope);
            Assert.DoesNotContain(lines, l => l.Code is TaxLineCodes.SalaryDetail or TaxLineCodes.SalaryTaxable or TaxLineCodes.SalaryRateGross);
        }
        Assert.Contains(await LinesAsync(app, app.AdultB, VisibilityScope.Me), l => l.Code == TaxLineCodes.SalaryTaxable);
    }
}
