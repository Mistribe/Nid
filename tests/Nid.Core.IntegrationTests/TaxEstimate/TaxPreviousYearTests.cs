using Microsoft.Extensions.DependencyInjection;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.TestSupport;
using NodaTime;

namespace Nid.Core.IntegrationTests.TaxEstimate;

/// <summary>Issue #438: the estimate reads the year before once per request, which holds only while no contribution's past year depends on the reading date.</summary>
public class TaxPreviousYearTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task A_contribution_s_lines_for_a_past_year_are_the_same_on_any_later_day()
    {
        await using var app = await NidApp.StartAsync(fixture);
        // 2024: a salary, a let property with one rent recorded (the others projected while 2024 is read), a nanny with two months.
        var salary = await app.NewIncomeSourceAsync("Salaire", app.AdultA);
        (await app.RawAsync(app.AdultA, HttpMethod.Post, $"/api/v1/income/sources/{salary.Value}/phases",
            new { label = "Salaire", startsOn = "2024-01-01", netAmount = new { amount = "3000", currency = "EUR" }, every = new { count = 1, unit = "month" } })).EnsureSuccessStatusCode();
        var property = $"{PropertyApi.Properties}/{(await app.NewPropertyAsync("Locatif", Visibility.Shared, (app.AdultA, 10_000))).Value}";
        await LoanApi.CreatedAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, $"{property}/tenancies",
            new { label = "Bail", startsOn = "2024-01-01", rentMonthly = "750", chargesMonthly = "0" }));
        await LoanApi.CreatedAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, $"{property}/flows",
            new { occurredOn = "2024-03-05", kind = "income", amount = "700", category = "rent" }));
        var nanny = $"/api/v1/childcare/nannies/{await LoanApi.CreatedAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, "/api/v1/childcare/nannies",
            new { name = "Garde à domicile", visibility = "shared", owners = new[] { new { personId = app.AdultA.Value, shareBp = 10_000 } } }))}";
        foreach (var month in new[] { "2024-03", "2024-04" })
            (await app.RawAsync(app.AdultA, HttpMethod.Put, $"{nanny}/months/{month}", new { paid = "1000", cmg = "400" })).EnsureSuccessStatusCode();

        await using var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.Browser(app.AdultA);
        var ct = TestContext.Current.CancellationToken;
        var changing = 0;
        foreach (var contribution in scope.ServiceProvider.GetServices<ITaxContribution>())
        {
            async Task<IReadOnlyList<TaxLine>> LinesAsync(LocalDate on)
            {
                var read = await contribution.TaxLinesAsync(app.AdultA, VisibilityScope.Household, 2024, on, ct);
                Assert.True(read.TryGetValue(out var lines), contribution.GetType().Name);
                return lines!;
            }

            var later = await LinesAsync(new LocalDate(2025, 1, 1));
            foreach (var on in new LocalDate[] { new(2025, 6, 30), new(2025, 12, 31), new(2026, 3, 1) })
                Assert.True(later.SequenceEqual(await LinesAsync(on)), $"{contribution.GetType().Name} reads 2024 differently on {on}");
            if (!later.SequenceEqual(await LinesAsync(new LocalDate(2024, 4, 30)))) changing++;
        }
        // The rental and the nanny project the months after the reading day while their year runs: the household would show a breach.
        Assert.True(changing >= 2);
    }
}
