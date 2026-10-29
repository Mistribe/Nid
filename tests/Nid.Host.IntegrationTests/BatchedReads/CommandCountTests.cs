using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nid.Core.Contracts;
using Nid.Core.Infrastructure.Persistence;
using Nid.Goals.Infrastructure.Persistence;
using Nid.Income.Infrastructure.Persistence;
using Nid.Investments.Infrastructure.Persistence;
using Nid.Platform.Infrastructure.Persistence;
using Nid.RealEstate.Contracts;
using Nid.RealEstate.Infrastructure.Persistence;
using Nid.Subscriptions.Infrastructure.Persistence;
using Nid.TestSupport;
using static Nid.TestSupport.LoanApi;
using static Nid.TestSupport.PropertyApi;

namespace Nid.Host.IntegrationTests.BatchedReads;

/// <summary>
/// Batched reads spec §4, the permanent guard: each route issues as many SQL commands for 2N items as for N — no read per item.
/// Every round adds, for A, a shared envelope with a value and a contribution, a shared property with an estimate, a planned
/// cost and a tenancy, a loan linked to it reading its bank's table (3F2) and one in USD converted at a stored rate, a closed envelope with a value, a monthly subscription, an income source with a phase, and three goals (on the envelopes, an emergency
/// fund, a free one with entries): the same shapes each
/// round, so every branch a route takes is taken alike for N and 2N.
/// </summary>
public class CommandCountTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const int N = 2;

    [Theory]
    [InlineData("/api/v1/realestate/loans?scope=me")]
    [InlineData("/api/v1/realestate/properties?scope=me")]
    [InlineData("/api/v1/dashboard?scope=me")]
    [InlineData("/api/v1/dashboard/net-worth?scope=me&period=all")]
    [InlineData("/api/v1/platform/export/manifest")]
    [InlineData("/api/v1/investments/envelopes?scope=me")]
    [InlineData("/api/v1/investments/envelopes?scope=me&archived=true")]
    [InlineData("/api/v1/goals?scope=me&closed=true")]
    [InlineData("/api/v1/realestate/routine?on=2025-10-15")] // 3E2: the round's tax (due 20 October) and tenancy are both proposed
    public async Task A_route_issues_as_many_commands_for_2N_items_as_for_N(string route)
    {
        var counter = new CommandCounter();
        await using var app = await NidApp.StartAsync(fixture, services => Count(services, counter));
        await using (var scope = app.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<IFxRateStore>()
                .WriteAsync([new FxRate(NidApp.GoldenCaseToday, Currency.Eur, 0.9m, FxRateSource.Ecb)], default); // 1 USD = 0.90 EUR
        for (var i = 0; i < N; i++)
            await RoundAsync(app, i);
        var once = await CommandsAsync(app, counter, route);
        for (var i = N; i < 2 * N; i++)
            await RoundAsync(app, i);
        var twice = await CommandsAsync(app, counter, route);

        Assert.True(once > 0, "the counter saw no command: it is not registered");
        Assert.Equal(once, twice);
    }

    private static void Count(IServiceCollection services, CommandCounter counter)
    {
        services.ConfigureDbContext<CoreDbContext>(o => o.AddInterceptors(counter));
        services.ConfigureDbContext<SubscriptionsDbContext>(o => o.AddInterceptors(counter));
        services.ConfigureDbContext<InvestmentsDbContext>(o => o.AddInterceptors(counter));
        services.ConfigureDbContext<IncomeDbContext>(o => o.AddInterceptors(counter));
        services.ConfigureDbContext<GoalsDbContext>(o => o.AddInterceptors(counter));
        services.ConfigureDbContext<RealEstateDbContext>(o => o.AddInterceptors(counter));
        services.ConfigureDbContext<PlatformDbContext>(o => o.AddInterceptors(counter));
    }

    /// <summary>The route read once to warm it (query compilation, first-request work), then counted on a second read.</summary>
    private static async Task<int> CommandsAsync(NidApp app, CommandCounter counter, string route)
    {
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Get, route)).StatusCode);
        counter.Reset();
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Get, route)).StatusCode);
        return counter.Count;
    }

    private static async Task RoundAsync(NidApp app, int i)
    {
        var envelope = await app.NewEnvelopeAsync($"Livret {i}", app.AdultA, Visibility.Shared);
        await PostAsync(app, $"/api/v1/investments/envelopes/{envelope.Value}/valuations",
            new { asOf = "2025-10-01", amount = Eur("1000.00"), note = (string?)null });
        await PostAsync(app, $"/api/v1/investments/envelopes/{envelope.Value}/flows",
            new { occurredOn = "2025-06-15", kind = "contribution", amount = Eur("100.00") });

        var property = await NewPropertyAsync(app, Home(app, $"Maison {i}"));
        await PostAsync(app, $"{Properties}/{property}/valuations", new { asOf = "2025-06-01", amount = "290000" });
        await PostAsync(app, $"{Properties}/{property}/recurring-costs",
            new { category = "property_tax", amount = "1200", every = new { count = 1, unit = "year" }, startsOn = "2020-10-20" });
        await PostAsync(app, $"{Properties}/{property}/tenancies",
            new { label = "T2", startsOn = "2021-01-01", endsOn = "2025-12-01", rentMonthly = "900" });
        var loan = Golden(app, $"Prêt {i}");
        loan["propertyId"] = property;
        var main = await NewAsync(app, loan);
        // 3F2: its bank rows come with the loans, in the same batch, and it reads them as its reference.
        await LoanApi.StoreTableAsync(app, main, ScheduleSource.Bank, LoanApi.Sixtieth);
        var dollars = Short(app, $"Prêt dollars {i}"); // converted at the stored rate
        dollars["currency"] = "USD";
        dollars["propertyId"] = property;
        await NewAsync(app, dollars);

        var closed = await app.NewEnvelopeAsync($"Clos {i}", app.AdultA, Visibility.Shared);
        await PostAsync(app, $"/api/v1/investments/envelopes/{closed.Value}/valuations",
            new { asOf = "2025-03-01", amount = Eur("500.00"), note = (string?)null });
        // Goals: one on the open envelope and the closed one (linked before it closed), an emergency fund on it, a free one with entries.
        await app.NewGoalAsync($"Voyage {i}", app.AdultA, funding: [(envelope, 5000), (closed, 10_000)]);
        await PostAsync(app, "/api/v1/goals", new
        {
            name = $"Précaution {i}", kind = "emergency_fund", months = 3, visibility = "shared",
            owners = new[] { new { personId = app.AdultA.Value, shareBp = 10_000 } }, funding = new[] { new { envelopeId = envelope.Value, shareBp = 2000 } },
        });
        var free = await app.NewGoalAsync($"Libre {i}", app.AdultA);
        foreach (var on in new[] { "2025-01-15", "2025-09-15" })
            Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Put, $"/api/v1/goals/{free.Value}/progress/{on}", new { amount = "100" })).StatusCode);
        await PostAsync(app, $"/api/v1/investments/envelopes/{closed.Value}/close", new { closedOn = "2025-06-01" }, HttpStatusCode.OK);

        await app.NewMonthlyAsync(app.AdultA, 10m + i);
        await app.NewIncomeSourceAsync($"Salaire {i}", Visibility.Shared, (app.AdultA, 10_000));
    }

    private static object Eur(string amount) => new { amount, currency = "EUR" };

    private static async Task PostAsync(NidApp app, string route, object body, HttpStatusCode expected = HttpStatusCode.Created)
        => Assert.Equal(expected, (await app.RawAsync(app.AdultA, HttpMethod.Post, route, body)).StatusCode);
}
