using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nid.Banking.Infrastructure.Persistence;
using Nid.Childcare.Infrastructure.Persistence;
using Nid.Core.Application;
using Nid.Core.Contracts;
using Nid.Core.Infrastructure.Persistence;
using Nid.Documents.Infrastructure.Persistence;
using Nid.Goals.Infrastructure.Persistence;
using Nid.Income.Infrastructure.Persistence;
using Nid.Investments.Infrastructure.Persistence;
using Nid.Platform.Infrastructure.Persistence;
using Nid.RealEstate.Application;
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
/// cost and a tenancy, a loan linked to it reading its bank's table (3F2) and one in USD converted at a stored rate, a closed envelope with a value, a monthly subscription, an income source with a phase, three goals (on the envelopes, an emergency
/// fund, a free one with entries) and a childcare arrangement running this month (#644): the same shapes each
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
    [InlineData("/api/v1/assets?scope=me")]
    [InlineData("/api/v1/calendar?scope=me&from=2025-10-01&to=2025-11-30")] // #239: the round's tax (20 October) and every module
    [InlineData("/api/v1/calendar?scope=me&from=2025-10-01&to=2025-11-30&amounts=true")] // #241: the instalments, costs, rents and subscriptions
    [InlineData("/api/v1/calendar/timeline?scope=me&horizon=10")] // #240: the loans' ends, the tenancies' and the envelopes' tax dates
    [InlineData("/api/v1/realestate/routine?on=2025-10-15")] // 3E2: the round's tax (due 20 October) and tenancy are both proposed
    public async Task A_route_issues_as_many_commands_for_2N_items_as_for_N(string route)
    {
        var counter = new CommandCounter();
        await using var app = await NidApp.StartAsync(fixture, services => Count(services, counter));
        await using (var scope = app.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<IFxRateStore>()
                .WriteAsync([new FxRate(NidApp.GoldenCaseToday, Currency.Eur, 0.9m, FxRateSource.Ecb)], TestContext.Current.CancellationToken); // 1 USD = 0.90 EUR
        await AssertFixedAsync(counter, i => RoundAsync(app, i),
            async () => Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Get, route)).StatusCode));
    }

    /// <summary>#518: the assistant's rent arrears (no route of their own) read every rented property's flows at once.</summary>
    [Fact]
    public async Task Rent_arrears_issue_as_many_commands_for_2N_properties_as_for_N()
    {
        var counter = new CommandCounter();
        await using var app = await NidApp.StartAsync(fixture, services => Count(services, counter));
        var rented = 0;
        await AssertFixedAsync(counter, async i =>
        {
            var property = await NewPropertyAsync(app, Home(app, $"Maison {i}"));
            await PostAsync(app, $"{Properties}/{property}/tenancies", new { label = "T2", startsOn = "2025-01-01", rentMonthly = "900" });
            rented++;
        }, async () =>
        {
            var (response, failure) = await app.InvokeAsync(app.AdultA, new GetRentArrearsQuery(VisibilityScope.Me), TestContext.Current.CancellationToken);
            Assert.Null(failure);
            Assert.Equal(rented, ((RentArrearsList)response!).Items.Count); // every month since January is due
        });
    }

    /// <summary>
    /// #518: the bank account list reads its rights, balances and sources at once. Each round, B's account A's connection sees too (A
    /// owns it by its source), an account only B sees, and A's own account on a shared envelope with a card attached to it.
    /// </summary>
    [Fact]
    public async Task The_bank_account_list_issues_as_many_commands_for_2N_accounts_as_for_N()
    {
        var counter = new CommandCounter();
        await using var app = await BankingApi.StartAsync(fixture, overrides: services => Count(services, counter));
        var readable = 0;
        await AssertFixedAsync(counter, async i =>
        {
            await BankingApi.ConnectAsync(app, app.AdultB, $"b-{i}", BankingApi.Account($"uid-b{i}", $"hash-j{i}", null, $"Joint {i}"),
                BankingApi.Account($"uid-p{i}", $"hash-p{i}", null, $"Privé {i}"));
            await BankingApi.ConnectAsync(app, app.AdultA, $"a-{i}", BankingApi.Account($"uid-j{i}", $"hash-j{i}", null, $"Joint {i}"),
                BankingApi.Account($"uid-a{i}", $"hash-a{i}", null, $"Courant {i}"),
                BankingApi.Account($"uid-c{i}", $"hash-c{i}", null, $"Carte {i}", "CARD"));
            var ids = (await BankingApi.AccountsAsync(app, app.AdultA)).EnumerateArray()
                .ToDictionary(a => a.GetProperty("name").GetString()!, a => a.GetProperty("id").GetGuid());
            var envelope = await app.NewEnvelopeAsync($"Courant {i}", app.AdultA, Visibility.Shared);
            await PatchAccountAsync(app, ids[$"Courant {i}"], new { envelopeId = envelope.Value });
            await PatchAccountAsync(app, ids[$"Carte {i}"], new { cardOfAccountId = ids[$"Courant {i}"] });
            readable += 3;
        }, async () => Assert.Equal(readable, (await BankingApi.AccountsAsync(app, app.AdultA)).GetArrayLength()));
    }

    /// <summary>The read counted after <see cref="N"/> rounds and after <c>2N</c>: as many commands each time.</summary>
    private static async Task AssertFixedAsync(CommandCounter counter, Func<int, Task> round, Func<Task> read)
    {
        for (var i = 0; i < N; i++)
            await round(i);
        var once = await CommandsAsync(counter, read);
        for (var i = N; i < 2 * N; i++)
            await round(i);
        var twice = await CommandsAsync(counter, read);

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
        services.ConfigureDbContext<Nid.Assets.Infrastructure.Persistence.AssetsDbContext>(o => o.AddInterceptors(counter));
        services.ConfigureDbContext<Nid.Tax.Infrastructure.Persistence.TaxDbContext>(o => o.AddInterceptors(counter));
        services.ConfigureDbContext<RealEstateDbContext>(o => o.AddInterceptors(counter));
        services.ConfigureDbContext<PlatformDbContext>(o => o.AddInterceptors(counter));
        services.ConfigureDbContext<BankingDbContext>(o => o.AddInterceptors(counter));
        services.ConfigureDbContext<ChildcareDbContext>(o => o.AddInterceptors(counter));
        services.ConfigureDbContext<DocumentsDbContext>(o => o.AddInterceptors(counter));
    }

    /// <summary>The read made once to warm it (query compilation, first-request work), then counted on a second read.</summary>
    private static async Task<int> CommandsAsync(CommandCounter counter, Func<Task> read)
    {
        await read();
        counter.Reset();
        await read();
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

        // Assets: one valued at its purchase, one with a valuation of its own, both listed together.
        var owners = new[] { new { personId = app.AdultA.Value, shareBp = 10_000 } };
        await PostAsync(app, "/api/v1/assets", new { name = $"Tableau {i}", kind = "art", purchasePrice = "1000", purchasedOn = "2025-01-10", visibility = "shared", owners });
        await PostAsync(app, "/api/v1/assets", new { name = $"Voiture {i}", kind = "vehicle", currentValue = "8000", visibility = "shared", owners });

        await app.NewMonthlyAsync(app.AdultA, 10m + i);
        await app.NewIncomeSourceAsync($"Salaire {i}", Visibility.Shared, (app.AdultA, 10_000));

        // #644: the dashboard's commitments and the tax lines estimate its month, October, from the contract.
        await PostAsync(app, "/api/v1/childcare", new
        {
            name = $"Garde {i}", childId = app.Dependent.Value,
            carer = new { name = $"Assistante {i}", pajemploi = (string?)null, approval = (string?)null },
            terms = new
            {
                from = "2025-09-01", netHourlyRate = "4.30", grossHourlyRate = (string?)null, overtimePct = "10", upkeepPer9h = "3.80", lunchPrice = (string?)null,
                weekTypes = new[] { new { name = "Semaine type", weeks = 46, schedule = new[] { new { day = 1, arrival = "08:00", departure = "17:30", lunch = false } } } },
            },
            gaps = Array.Empty<object>(), visibility = "shared",
            owners = new[] { new { personId = app.AdultA.Value, shareBp = 10_000 } },
        });
    }

    private static async Task PatchAccountAsync(NidApp app, Guid account, object body)
        => Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Patch, $"{BankingApi.Root}/accounts/{account}", body)).StatusCode);

    private static object Eur(string amount) => new { amount, currency = "EUR" };

    private static async Task PostAsync(NidApp app, string route, object body, HttpStatusCode expected = HttpStatusCode.Created)
        => Assert.Equal(expected, (await app.RawAsync(app.AdultA, HttpMethod.Post, route, body)).StatusCode);
}
