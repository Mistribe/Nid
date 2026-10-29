using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.TestSupport;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;
using static Nid.TestSupport.LoanApi;
using static Nid.TestSupport.PropertyApi;
using Estimates = Nid.TestSupport.EnvelopeEstimateApi;

namespace Nid.Core.IntegrationTests.TaxEstimate;

/// <summary>Sale tax spec §6: what RealEstate and Investments supply about the reader's visible items. The host's today is 2025-10-15.</summary>
public class SaleContributionTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static readonly LocalDate Today = new(2025, 10, 15);
    internal const string Envelopes = "/api/v1/investments/envelopes";

    internal static object Eur(string amount) => new { amount, currency = "EUR" };

    /// <summary>Every module's assets, read as <paramref name="reader"/>.</summary>
    private static async Task<SaleAssets> AssetsAsync(NidApp app, PersonId reader, VisibilityScope scope = VisibilityScope.Me, LocalDate? today = null)
    {
        await using var services = app.Services.CreateAsyncScope();
        services.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.Browser(reader);
        List<PropertyForSale> properties = [];
        List<EnvelopeForSale> envelopes = [];
        foreach (var contribution in services.ServiceProvider.GetServices<ISaleContribution>())
        {
            Assert.True((await contribution.SaleAssetsAsync(reader, scope, today ?? Today, default)).TryGetValue(out var assets));
            properties.AddRange(assets!.Properties);
            envelopes.AddRange(assets.Envelopes);
        }
        return new SaleAssets(properties, envelopes);
    }

    internal static async Task<ItemId> EnvelopeAsync(
        NidApp app, string name, string kind, string? openedOn = null, PersonId[]? owners = null, string visibility = "shared",
        string? premiumsBefore2017 = null, object? opening = null)
    {
        var who = owners ?? [app.AdultA];
        return new ItemId(await CreatedAsync(await app.RawAsync(who[0], HttpMethod.Post, Envelopes, new
        {
            displayName = name, kind, currency = "EUR", visibility, openedOn, premiumsBefore2017, opening,
            owners = who.Select(p => new { personId = p.Value, shareBp = 10_000 / who.Length }).ToArray(),
        })));
    }

    internal static async Task PostAsync(NidApp app, string url, object body, PersonId? actor = null)
    {
        var response = await app.RawAsync(actor ?? app.AdultA, HttpMethod.Post, url, body);
        Assert.True(response.StatusCode == System.Net.HttpStatusCode.Created, $"{url}: {await response.Content.ReadAsStringAsync()}");
    }

    internal static Task FlowAsync(NidApp app, ItemId envelope, string on, string kind, string amount, PersonId? actor = null)
        => PostAsync(app, $"{Envelopes}/{envelope.Value}/flows", new { occurredOn = on, kind, amount = Eur(amount) }, actor);

    internal static Task ValueAsync(NidApp app, ItemId envelope, string on, string amount)
        => PostAsync(app, $"{Envelopes}/{envelope.Value}/valuations", new { asOf = on, amount = Eur(amount) });

    internal static Task TransferAsync(NidApp app, ItemId from, ItemId to, string on, string amount, PersonId? actor = null)
        => PostAsync(app, "/api/v1/investments/transfers", new { from = from.Value, to = to.Value, occurredOn = on, amount = Eur(amount) }, actor);

    [Fact]
    public async Task A_property_comes_with_its_fees_its_works_until_today_and_its_value()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var body = Home(app, "Locatif");
        body["type"] = "rental";
        body["purchaseDate"] = "2018-03-01";
        body["purchasePrice"] = "200000";
        body["notaryFees"] = "15000";
        body["agencyFees"] = "5000";
        body["otherAcquisitionCosts"] = "1000";
        var rental = await NewPropertyAsync(app, body);
        var url = $"{Properties}/{rental}";
        await PostAsync(app, $"{url}/flows", new { occurredOn = "2020-05-01", kind = "expense", category = "works", amount = "8000" });
        await PostAsync(app, $"{url}/flows", new { occurredOn = "2025-10-15", kind = "expense", category = "works", amount = "1500.50" });
        await PostAsync(app, $"{url}/flows", new { occurredOn = "2024-04-01", kind = "expense", category = "maintenance", amount = "500" });
        await PostAsync(app, $"{url}/valuations", new { asOf = "2025-06-01", amount = "260000" });

        var property = Assert.Single((await AssetsAsync(app, app.AdultA)).Properties);

        Assert.Equal(
            new PropertyForSale(new ItemId(rental), "Locatif", "EUR", property.Owners, "rental", new LocalDate(2018, 3, 1), 200000m, 21000m,
                9500.50m, 260000m, new LocalDate(2025, 6, 1), false),
            property);
        Assert.Equal([(app.AdultA, 10_000)], property.Owners.Select(o => (o.PersonId, o.ShareBp)));
        // Works dated after the day asked for are left out.
        Assert.Equal(8000m, Assert.Single((await AssetsAsync(app, app.AdultA, today: Today.PlusDays(-1))).Properties).ActualWorks);
    }

    [Fact]
    public async Task A_life_insurance_comes_with_its_payments_withdrawals_values_and_premiums_before_2017()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var life = await EnvelopeAsync(app, "AV", "life_insurance", "2012-05-01", premiumsBefore2017: "30000",
            opening: new { occurredOn = "2020-01-01", amount = Eur("50000") });
        await FlowAsync(app, life, "2021-03-01", "contribution", "10000");
        await FlowAsync(app, life, "2022-02-01", "income", "120"); // neither a payment nor a withdrawal
        await FlowAsync(app, life, "2023-06-01", "withdrawal", "5000");
        await ValueAsync(app, life, "2022-12-31", "62000");
        await ValueAsync(app, life, "2024-12-31", "70000");

        var envelope = Assert.Single((await AssetsAsync(app, app.AdultA)).Envelopes);

        Assert.Equal((life, "AV", "EUR", "life_insurance", new LocalDate(2012, 5, 1), 30000m),
            (envelope.Item, envelope.Name, envelope.Currency, envelope.Kind, envelope.OpenedOn, envelope.PremiumsBefore2017));
        // Valued monthly by default: a value of last December is stale, and used.
        Assert.Equal((70000m, new LocalDate(2024, 12, 31), true, false), (envelope.Value, envelope.ValueOn, envelope.Stale, envelope.Estimated));
        Assert.Equal(
            [
                new SaleFlow(new LocalDate(2020, 1, 1), "opening", 50000m, null, null),
                new SaleFlow(new LocalDate(2021, 3, 1), "contribution", 10000m, null, null),
                new SaleFlow(new LocalDate(2023, 6, 1), "withdrawal", 5000m, null, null),
            ],
            envelope.Flows);
        Assert.Equal([new SaleValue(new LocalDate(2022, 12, 31), 62000m), new SaleValue(new LocalDate(2024, 12, 31), 70000m)], envelope.Values);
        Assert.Equal((null, false), (envelope.Lines, envelope.LinesComplete));
        Assert.Equal([(app.AdultA, 10_000)], envelope.Owners.Select(o => (o.PersonId, o.ShareBp)));
    }

    [Fact]
    public async Task A_transfer_leg_names_the_other_legs_kind_only_when_the_reader_sees_it()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var first = await EnvelopeAsync(app, "PEA banque 1", "pea", "2015-01-01");
        var second = await EnvelopeAsync(app, "PEA banque 2", "pea", "2024-01-01", [app.AdultA, app.AdultB]); // both may transfer into it
        var hidden = await EnvelopeAsync(app, "PEA de B", "pea", "2019-01-01", [app.AdultB], "private");
        await FlowAsync(app, first, "2015-01-01", "opening", "10000");
        await FlowAsync(app, hidden, "2019-01-01", "opening", "4000", app.AdultB);
        await TransferAsync(app, first, second, "2025-01-10", "12000");
        await TransferAsync(app, hidden, second, "2025-02-10", "4000", app.AdultB);

        var envelopes = (await AssetsAsync(app, app.AdultA)).Envelopes;
        // B sees both legs of their own transfer.
        var mine = (await AssetsAsync(app, app.AdultB)).Envelopes;
        var theirs = Assert.Single(mine.Single(e => e.Item == hidden).Flows, f => f.Kind == "withdrawal");

        Assert.Equal(["PEA banque 1", "PEA banque 2"], envelopes.Select(e => e.Name).Order());
        var withdrawal = Assert.Single(envelopes.Single(e => e.Item == first).Flows, f => f.Kind == "withdrawal");
        var received = envelopes.Single(e => e.Item == second).Flows;
        Assert.Equal(
            [
                new SaleFlow(new LocalDate(2025, 1, 10), "contribution", 12000m, withdrawal.TransferRef, "pea"),
                new SaleFlow(new LocalDate(2025, 2, 10), "contribution", 4000m, theirs.TransferRef, null),
            ],
            received);
        Assert.Equal((new LocalDate(2025, 1, 10), 12000m, "pea"), (withdrawal.On, withdrawal.Amount, withdrawal.CounterpartKind));
        Assert.StartsWith("transfer:", withdrawal.TransferRef);
        Assert.NotEqual(withdrawal.TransferRef, theirs.TransferRef);
        Assert.Equal("pea", theirs.CounterpartKind);
    }

    [Fact]
    public async Task A_closed_transfer_source_is_supplied_and_named_as_the_counterpart()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var source = await EnvelopeAsync(app, "PEA ancienne banque", "pea", "2015-01-01");
        var destination = await EnvelopeAsync(app, "PEA nouvelle banque", "pea", "2025-01-01");
        await FlowAsync(app, source, "2015-01-01", "opening", "10000");
        await ValueAsync(app, source, "2024-12-31", "12000");
        await TransferAsync(app, source, destination, "2025-01-10", "12000");
        (await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Envelopes}/{source.Value}/close", new { closedOn = "2025-01-31" })).EnsureSuccessStatusCode();

        var envelopes = (await AssetsAsync(app, app.AdultA)).Envelopes;

        var received = Assert.Single(envelopes.Single(e => e.Item == destination).Flows);
        Assert.Equal(("contribution", "pea"), (received.Kind, received.CounterpartKind));
        var closed = envelopes.Single(e => e.Item == source);
        Assert.Equal((new LocalDate(2025, 1, 31), (decimal?)null, (LocalDate?)null, (IReadOnlyList<SaleLine>?)null),
            (closed.ClosedOn, closed.Value, closed.ValueOn, closed.Lines));
        Assert.Equal(
            [
                new SaleFlow(new LocalDate(2015, 1, 1), "opening", 10000m, null, null),
                new SaleFlow(new LocalDate(2025, 1, 10), "withdrawal", 12000m, received.TransferRef, "pea"),
            ],
            closed.Flows);
        Assert.Equal(new SaleValue(new LocalDate(2024, 12, 31), 12000m), closed.Values[0]);
        Assert.Null(envelopes.Single(e => e.Item == destination).ClosedOn);
    }

    [Fact]
    public async Task A_household_read_names_a_counterpart_the_reader_sees_only_in_their_own_scope()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var mine = await EnvelopeAsync(app, "PEA perso", "pea", "2015-01-01", visibility: "private");
        var shared = await EnvelopeAsync(app, "PEA commun", "pea", "2024-01-01");
        await FlowAsync(app, mine, "2015-01-01", "opening", "5000");
        await TransferAsync(app, mine, shared, "2025-01-10", "5000");

        var envelopes = (await AssetsAsync(app, app.AdultA, VisibilityScope.Household)).Envelopes;

        Assert.Equal([shared], envelopes.Select(e => e.Item));
        Assert.Equal("pea", Assert.Single(envelopes[0].Flows).CounterpartKind);
        // B never sees A's private PEA: the leg stays without counterpart for them.
        Assert.Null(Assert.Single(Assert.Single((await AssetsAsync(app, app.AdultB)).Envelopes).Flows).CounterpartKind);
    }

    /// <summary>Forwards to the real IValuations, but every batched series fails: a block of facts that cannot be read.</summary>
    public class FailingSeries : DispatchProxy
    {
        public IValuations? Inner;

        protected override object? Invoke(MethodInfo? method, object?[]? args)
            => method!.Name == nameof(IValuations.SeriesManyAsync)
                ? Task.FromResult(Result.Failure<IReadOnlyDictionary<ItemId, IReadOnlyList<Valuation>>>(new Failure("test.broken", "Series unreadable.", null)))
                : method.Invoke(Inner, args);
    }

    [Fact]
    public async Task Facts_that_cannot_be_read_leave_values_and_lines_out()
    {
        await using var app = await NidApp.StartAsync(fixture, services => services.AddScoped<IValuations>(sp =>
        {
            var proxy = DispatchProxy.Create<IValuations, FailingSeries>();
            ((FailingSeries)(object)proxy).Inner = ActivatorUtilities.CreateInstance<Nid.Core.Infrastructure.Persistence.Valuations>(sp);
            return proxy;
        }));
        var property = await NewPropertyAsync(app, Home(app, "Maison"));
        await PostAsync(app, $"{Properties}/{property}/valuations", new { asOf = "2025-06-01", amount = "290000" });
        var account = await EnvelopeAsync(app, "CTO", "brokerage", "2020-01-01");
        var url = $"{Envelopes}/{account.Value}";
        var etf = await CreatedAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, $"{url}/holdings", new { name = "ETF", kind = "etf", isin = "IE00B4L5Y983" }));
        await PostAsync(app, $"{url}/holding-snapshots", new
        {
            asOf = "2025-09-30", recordValuation = true,
            lines = new object[] { new { holdingId = etf, units = "10", unitPrice = "100", value = Eur("1000"), costBasisPerUnit = "80" } },
        });

        var assets = await AssetsAsync(app, app.AdultA); // a success: never a failure for the whole read

        var house = Assert.Single(assets.Properties);
        Assert.Equal(((decimal?)null, (LocalDate?)null), (house.Value, house.ValueOn));
        var envelope = Assert.Single(assets.Envelopes);
        Assert.Equal(((decimal?)null, (LocalDate?)null, (IReadOnlyList<SaleLine>?)null, false),
            (envelope.Value, envelope.ValueOn, envelope.Lines, envelope.LinesComplete));
    }

    [Fact]
    public async Task A_brokerage_comes_with_its_latest_snapshot_lines()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var account = await EnvelopeAsync(app, "CTO", "brokerage", "2020-01-01");
        var url = $"{Envelopes}/{account.Value}";
        var etf = await CreatedAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, $"{url}/holdings", new { name = "ETF", kind = "etf", isin = "IE00B4L5Y983" }));
        var cash = await CreatedAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, $"{url}/holdings", new { name = "Espèces", kind = "cash" }));
        await PostAsync(app, $"{url}/holding-snapshots", new
        {
            asOf = "2024-12-31", recordValuation = true,
            lines = new object[] { new { holdingId = etf, units = "5", unitPrice = "90", value = Eur("450"), costBasisPerUnit = "70" } },
        });
        await PostAsync(app, $"{url}/holding-snapshots", new
        {
            asOf = "2025-09-30", recordValuation = true, complete = false,
            lines = new object[]
            {
                new { holdingId = etf, units = "10", unitPrice = "100", value = Eur("1000"), costBasisPerUnit = "80.5" },
                new { holdingId = cash, value = Eur("200") },
            },
        });

        var envelope = Assert.Single((await AssetsAsync(app, app.AdultA)).Envelopes);

        Assert.Equal(("brokerage", 1200m, new LocalDate(2025, 9, 30), false), (envelope.Kind, envelope.Value, envelope.ValueOn, envelope.Estimated));
        Assert.Equal([new SaleLine(200m, null, null), new SaleLine(1000m, 10m, 80.5m)], envelope.Lines!.OrderBy(l => l.Value));
        Assert.Equal((false, (LocalDate?)new LocalDate(2025, 9, 30)), (envelope.LinesComplete, envelope.LinesOn));
    }

    [Fact]
    public async Task An_estimated_value_comes_with_the_estimated_lines()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var (id, _) = await Estimates.EnvelopeAsync(app);
        await Estimates.PriceAsync(app, Estimates.EtfIsin, 110m, Estimates.Day);

        var envelope = Assert.Single((await AssetsAsync(app, app.AdultA)).Envelopes);

        Assert.Equal((id, 1600m, Estimates.Day, true), (envelope.Item, envelope.Value, envelope.ValueOn, envelope.Estimated));
        Assert.Equal([new SaleLine(500m, null, null), new SaleLine(1100m, 10m, null)], envelope.Lines!.OrderBy(l => l.Value));
        Assert.True(envelope.LinesComplete);
    }

    [Fact]
    public async Task Another_adults_private_property_and_envelope_are_absent()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await NewPropertyAsync(app, Home(app, "Maison de B", "private", app.AdultB), app.AdultB);
        await EnvelopeAsync(app, "AV de B", "life_insurance", "2010-01-01", [app.AdultB], "private");
        var shared = await EnvelopeAsync(app, "Livret", "savings");

        var assets = await AssetsAsync(app, app.AdultA);

        Assert.Empty(assets.Properties);
        Assert.Equal([shared], assets.Envelopes.Select(e => e.Item));
        var theirs = await AssetsAsync(app, app.AdultB);
        Assert.Equal(["Maison de B"], theirs.Properties.Select(p => p.Name));
        Assert.Equal(["AV de B", "Livret"], theirs.Envelopes.Select(e => e.Name).Order());
        Assert.Empty((await AssetsAsync(app, app.AdultB, VisibilityScope.Household)).Properties);
    }
}
