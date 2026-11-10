using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Nid.Core.Contracts;
using Nid.TestSupport;
using NodaTime;
using UnambitiousFx.Functional;

namespace Nid.Core.IntegrationTests.Dashboard;

public class DashboardAggregationTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string Url = "/api/v1/dashboard";
    private static readonly LocalDate Today = new(2025, 10, 15); // NidApp's fixed clock

    private static IReadOnlyDictionary<Currency, decimal> Sums(params (Currency Currency, decimal Amount)[] sums)
        => sums.ToDictionary(s => s.Currency, s => s.Amount);
    private static readonly IReadOnlyDictionary<Currency, decimal> None = Sums();

    private sealed class Worth(NetWorthPart part) : INetWorthContribution
    {
        public Task<Result<NetWorthPart>> NetWorthAsync(PersonId reader, VisibilityScope scope, LocalDate today, CancellationToken ct) => Task.FromResult(Result.Success(part));
        public Task<Result<IReadOnlyList<NetWorthPoint>>> NetWorthHistoryAsync(PersonId reader, VisibilityScope scope, IReadOnlyList<LocalDate> dates, CancellationToken ct)
            => Task.FromResult(Result.Success<IReadOnlyList<NetWorthPoint>>([]));
        public Task<Result<IReadOnlyDictionary<Currency, decimal>>> NetContributionsAsync(PersonId reader, VisibilityScope scope, LocalDate from, LocalDate to, CancellationToken ct)
            => Task.FromResult(Result.Success<IReadOnlyDictionary<Currency, decimal>>(new Dictionary<Currency, decimal>()));
    }
    private sealed class Commitments(CommitmentsPart part) : ICommitmentsContribution
    {
        public Task<Result<CommitmentsPart>> CommitmentsAsync(PersonId reader, VisibilityScope scope, LocalDate today, CancellationToken ct) => Task.FromResult(Result.Success(part));
    }
    private sealed class Income(IncomePart part) : IIncomeContribution
    {
        public Task<Result<IncomePart>> IncomeAsync(PersonId reader, VisibilityScope scope, LocalDate today, CancellationToken ct) => Task.FromResult(Result.Success(part));
        public Task<Result<IncomeTimeline>> TimelineAsync(PersonId reader, VisibilityScope scope, LocalDate today, int months, IReadOnlyDictionary<PersonId, int> switches, CancellationToken ct)
            => Task.FromResult(Result.Success(new IncomeTimeline([], [])));
    }
    private sealed class Rates(RateTable table, List<Currency> asked) : IFxRates
    {
        public Task<RateTable> LoadAsync(IReadOnlyCollection<Currency> quotes, LocalDate from, LocalDate to, CancellationToken ct)
        {
            asked.AddRange(quotes);
            return Task.FromResult(table);
        }
    }

    private readonly List<Currency> quotesAsked = [];

    private static RateTable UsdToEur(decimal eurPerUsd)
        => new([new FxRate(Today, Currency.Eur, eurPerUsd, FxRateSource.Ecb)], Today, maxGapDays: 7);

    private Task<NidApp> StartAsync(NetWorthPart? worth = null, IReadOnlyList<CommitmentsPart>? commitments = null, IncomePart? income = null, RateTable? rates = null)
        => NidApp.StartAsync(fixture, services =>
        {
            if (worth is not null) services.AddScoped<INetWorthContribution>(_ => new Worth(worth));
            foreach (var part in commitments ?? [])
                services.AddScoped<ICommitmentsContribution>(_ => new Commitments(part));
            if (income is not null) services.AddScoped<IIncomeContribution>(_ => new Income(income));
            services.AddScoped<IFxRates>(_ => new Rates(rates ?? RateTable.Empty(Today, 7), quotesAsked));
        });

    private static async Task<JsonElement> ReadAsync(NidApp app, string query = "")
    {
        var response = await app.RawAsync(app.AdultA, HttpMethod.Get, Url + query);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
    }

    private static string? Total(JsonElement figure)
        => figure.GetProperty("total").ValueKind == JsonValueKind.Null ? null : figure.GetProperty("total").GetProperty("amount").GetString();

    [Fact]
    public async Task Sums_converts_and_derives_margin_share_and_change()
    {
        var usd = Currency.Usd;
        await using var app = await StartAsync(
            new NetWorthPart(2, 1, 1, new LocalDate(2025, 9, 30), Sums((Currency.Eur, 12000m), (usd, 1000m)),
                Sums((Currency.Eur, 10000m)), Sums((Currency.Eur, 1000m)), Sums((Currency.Eur, 1000m)), [], 0),
            [new CommitmentsPart(CommitmentSources.Subscriptions, 1, Sums((Currency.Eur, 250.555m)), Sums((Currency.Eur, 120m)))],
            new IncomePart(1, Sums((Currency.Eur, 2500m))),
            UsdToEur(0.9m));

        var view = await ReadAsync(app);

        Assert.Equal("EUR", view.GetProperty("currency").GetString());
        Assert.Equal("12900.00", Total(view.GetProperty("netWorth")));
        Assert.Equal(2, view.GetProperty("netWorth").GetProperty("perCurrency").GetArrayLength());
        Assert.Equal("2025-09-30", view.GetProperty("valuesAsOf").GetString());
        Assert.Equal(1, view.GetProperty("envelopesWithoutValue").GetInt32());
        Assert.Equal(1, view.GetProperty("staleEnvelopes").GetInt32());
        Assert.Equal("250.56", Total(view.GetProperty("commitments")));
        Assert.Equal("120.00", Total(view.GetProperty("cashThisMonth")));
        Assert.Equal("2500.00", Total(view.GetProperty("income")));
        Assert.Equal("2249.45", view.GetProperty("margin").GetProperty("amount").GetString()); // 2500 − 250.555, half away from zero
        Assert.Equal(10.0m, view.GetProperty("committedPct").GetDecimal());
        Assert.Equal(90.0m, view.GetProperty("marginPct").GetDecimal()); // 2249.445 ÷ 2500, the margin before rounding
        Assert.Equal(10.0m, view.GetProperty("commitmentSources")[0].GetProperty("pct").GetDecimal());
        Assert.Equal("1000.00", Total(view.GetProperty("change")));
        Assert.Equal("1000.00", Total(view.GetProperty("changeContributions")));
        Assert.Equal(9.1m, view.GetProperty("changePct").GetDecimal()); // 1000 ÷ (10000 + 1000)
        Assert.Contains(Currency.Eur, quotesAsked); // the reference's own quote is loaded too
    }

    [Fact]
    public async Task A_missing_rate_makes_the_figure_unknown_and_names_the_currency()
    {
        await using var app = await StartAsync(
            new NetWorthPart(1, 0, 0, Today, Sums((Currency.Usd, 1000m)), None, None, None, [], 0));

        var worth = (await ReadAsync(app)).GetProperty("netWorth");

        Assert.Null(Total(worth));
        Assert.Equal("USD", worth.GetProperty("missingCurrency").GetString());
        Assert.Equal(1, worth.GetProperty("perCurrency").GetArrayLength());
    }

    [Fact]
    public async Task No_income_declared_leaves_income_margin_and_share_unknown()
    {
        await using var app = await StartAsync(
            commitments: [new CommitmentsPart(CommitmentSources.Subscriptions, 1, Sums((Currency.Eur, 30m)), Sums((Currency.Eur, 30m)))],
            income: new IncomePart(0, None));

        var view = await ReadAsync(app);

        Assert.Null(Total(view.GetProperty("income")));
        Assert.Equal(JsonValueKind.Null, view.GetProperty("margin").ValueKind);
        Assert.Equal(JsonValueKind.Null, view.GetProperty("committedPct").ValueKind);
        Assert.Equal("30.00", Total(view.GetProperty("commitments")));
    }

    [Fact]
    public async Task A_zero_amount_needs_no_rate()
    {
        await using var app = await StartAsync(income: new IncomePart(1, Sums((Currency.Usd, 0m))));

        var income = (await ReadAsync(app)).GetProperty("income");

        Assert.Equal("0.00", Total(income));
        Assert.Equal(JsonValueKind.Null, income.GetProperty("missingCurrency").ValueKind);
    }

    [Fact]
    public async Task A_change_base_of_zero_gives_the_gain_without_a_percent()
    {
        await using var app = await StartAsync(
            new NetWorthPart(1, 0, 0, Today, Sums((Currency.Eur, 50m)), Sums((Currency.Eur, 0m)), Sums((Currency.Eur, 0m)), Sums((Currency.Eur, 50m)), [], 0));

        var view = await ReadAsync(app);

        Assert.Equal("50.00", Total(view.GetProperty("change")));
        Assert.Equal(JsonValueKind.Null, view.GetProperty("changePct").ValueKind);
    }

    [Fact]
    public async Task Nothing_at_all_reads_zero_counts_and_no_totals_in_either_scope()
    {
        await using var app = await StartAsync();

        foreach (var query in new[] { "", "?scope=me", "?scope=household" })
        {
            var view = await ReadAsync(app, query);
            Assert.Equal(0, view.GetProperty("envelopes").GetInt32());
            Assert.Equal(0, view.GetProperty("subscriptions").GetInt32());
            Assert.Equal(0, view.GetProperty("incomeSources").GetInt32());
            Assert.Equal(0, view.GetProperty("commitmentSources").GetArrayLength());
            Assert.Null(Total(view.GetProperty("netWorth")));
            Assert.Equal(JsonValueKind.Null, view.GetProperty("netWorth").GetProperty("missingCurrency").ValueKind);
        }
    }

    [Fact]
    public async Task Each_source_counting_items_gets_its_amortized_month_converted_as_the_total()
    {
        await using var app = await StartAsync(
            commitments:
            [
                new CommitmentsPart(CommitmentSources.Subscriptions, 2, Sums((Currency.Eur, 30m)), Sums((Currency.Eur, 30m))),
                new CommitmentsPart(CommitmentSources.Loans, 1, Sums((Currency.Usd, 1000m)), Sums((Currency.Usd, 1000m))),
                new CommitmentsPart(CommitmentSources.PropertyCosts, 1, None, None),
            ],
            rates: UsdToEur(0.9m));

        var view = await ReadAsync(app);

        Assert.Equal("930.00", Total(view.GetProperty("commitments")));
        Assert.Equal(2, view.GetProperty("subscriptions").GetInt32()); // the subscriptions' own count, as the welcome reads it
        var sources = view.GetProperty("commitmentSources").EnumerateArray().ToList();
        Assert.Equal(new[] { "subscriptions", "loans", "property_costs" }, sources.Select(s => s.GetProperty("source").GetString()));
        // A source counting items that cost nothing this month reads 0, never « unknown ».
        Assert.Equal(new[] { "30.00", "900.00", "0.00" }, sources.Select(s => s.GetProperty("amortized").GetProperty("amount").GetString()));
        // No income declared: no share of it, for a source as for the margin.
        Assert.All(sources, s => Assert.Equal(JsonValueKind.Null, s.GetProperty("pct").ValueKind));
        Assert.Equal(JsonValueKind.Null, view.GetProperty("marginPct").ValueKind);
    }

    [Fact]
    public async Task A_source_whose_rate_is_missing_is_unknown_as_the_total()
    {
        await using var app = await StartAsync(commitments:
        [
            new CommitmentsPart(CommitmentSources.Subscriptions, 1, Sums((Currency.Eur, 30m)), Sums((Currency.Eur, 30m))),
            new CommitmentsPart(CommitmentSources.Loans, 1, Sums((Currency.Usd, 1000m)), Sums((Currency.Usd, 1000m))),
        ]);

        var view = await ReadAsync(app);

        Assert.Equal("USD", view.GetProperty("commitments").GetProperty("missingCurrency").GetString());
        var sources = view.GetProperty("commitmentSources").EnumerateArray().ToList();
        Assert.Equal("30.00", sources[0].GetProperty("amortized").GetProperty("amount").GetString());
        Assert.Equal(JsonValueKind.Null, sources[1].GetProperty("amortized").ValueKind);
    }

    [Fact]
    public async Task An_unknown_scope_is_a_bad_request()
    {
        await using var app = await StartAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await app.RawAsync(app.AdultA, HttpMethod.Get, Url + "?scope=everyone")).StatusCode);
    }
}
