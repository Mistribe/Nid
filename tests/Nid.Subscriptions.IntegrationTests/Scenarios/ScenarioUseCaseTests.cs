using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nid.Core.Application.Scenarios;
using Nid.Core.Application.Tax;
using Nid.Core.Contracts;
using Nid.Core.Domain.Scenarios;
using Nid.Core.Domain.Tax;
using Nid.Host.Api;
using Nid.Investments.Application;
using Nid.Platform.Contracts;
using Nid.TestSupport;
using NodaTime;
using UnambitiousFx.Functional.Failures;
using static Nid.Subscriptions.IntegrationTests.RealEstate.LoanApi;
using static Nid.Subscriptions.IntegrationTests.RealEstate.PropertyApi;

namespace Nid.Subscriptions.IntegrationTests.Scenarios;

/// <summary>Property purchase scenario spec §4 and §5, through the real pipeline. Today is 2025-10-15.</summary>
public class ScenarioUseCaseTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web)
    {
        Converters = { new ItemIdJsonConverter(), new LocalDateJsonConverter() },
    };

    private static PropertyPurchase Doc(
        IReadOnlyList<FundingLine>? funding = null, PurchaseUse use = PurchaseUse.Primary, decimal expectedRent = 0m,
        string? regime = null, LocalDate? on = null) => new(
        1, use, on ?? new LocalDate(2025, 12, 1), 200_000m, 15_000m, 0m, 0m, 100m, use == PurchaseUse.Primary ? 800m : 0m,
        expectedRent, 0, regime, funding ?? [],
        [new ScenarioLoan("Banque", 200_000m, 0.035m, 240, null, new ScenarioInsurance(ScenarioInsuranceKind.RateOnInitial, 0.003m), 1_000m, 0m)],
        new ScenarioAssumptions(10, ScenarioRules.Defaults, null, 500m, AllocationClasses.Cash));

    private static async Task<T> OkAsync<T>(NidApp app, PersonId actor, object request)
    {
        var (response, failure) = await app.InvokeAsync(actor, request);
        Assert.True(failure is null, $"{request.GetType().Name} failed: {failure?.Code} {failure?.Message}");
        return (T)response!;
    }

    private static async Task<ItemId> ValuedEnvelopeAsync(NidApp app, PersonId owner, string name, decimal value, string kind = "savings")
    {
        var response = await app.RawAsync(owner, HttpMethod.Post, "/api/v1/investments/envelopes", new
        {
            displayName = name, kind, currency = "EUR", visibility = "shared",
            owners = new[] { new { personId = owner.Value, shareBp = 10_000 } },
        });
        var id = new ItemId(await CreatedAsync(response));
        await OkAsync<Guid>(app, owner, new RecordValuationCommand(id, new LocalDate(2025, 9, 30), Money.Create(value, Currency.Eur).Ok(), null));
        return id;
    }

    private static CreateScenarioCommand Create(NidApp app, PersonId owner, PropertyPurchase doc, Visibility visibility = Visibility.Shared, string name = "Appartement")
        => new(name, visibility, [new OwnerShare(owner, 10_000)], VisibilityScope.Household, doc);

    private static decimal D(string amount) => decimal.Parse(amount, CultureInfo.InvariantCulture);

    [Fact]
    public async Task Month_zero_is_todays_dashboard_net_worth() // Review Focus 1
    {
        await using var app = await NidApp.StartAsync(fixture);
        await ValuedEnvelopeAsync(app, app.AdultA, "Livret", 40_000m);
        var home = await NewPropertyAsync(app, Home(app));
        await CreatedAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Properties}/{home}/valuations", new { asOf = "2025-06-01", amount = "300000" }));
        var loan = Golden(app);
        loan["propertyId"] = home;
        await NewAsync(app, loan);

        var result = await OkAsync<ScenarioResultView>(app, app.AdultA, new SimulateScenarioQuery(VisibilityScope.Household, Doc()));

        var dashboard = JsonDocument.Parse(await app.GetRawAsync(app.AdultA, "/api/v1/dashboard?scope=household")).RootElement
            .GetProperty("netWorth").GetProperty("total").GetProperty("amount").GetString()!;
        Assert.Equal(dashboard, result.Points[0].Without.Amount);
        Assert.Equal(dashboard, result.Points[0].With.Amount);
    }

    [Fact]
    public async Task A_funding_envelope_made_private_by_the_other_adult_disappears() // Review Focus 2
    {
        await using var app = await NidApp.StartAsync(fixture);
        var theirs = await ValuedEnvelopeAsync(app, app.AdultB, "Livret de Bérénice", 30_000m);
        var id = await OkAsync<ItemId>(app, app.AdultA, Create(app, app.AdultA, Doc([new FundingLine(theirs, 16_000m)])));

        await using (var scope = app.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.Browser(app.AdultB);
            var replaced = await scope.ServiceProvider.GetRequiredService<IItemRegistry>().ReplaceAsync(
                theirs, "Livret de Bérénice", Visibility.Private, [new OwnerShare(app.AdultB, 10_000)], CancellationToken.None);
            Assert.True(replaced.IsSuccess);
        }

        var (response, failure) = await app.InvokeAsync(app.AdultA, new GetScenarioQuery(id));

        Assert.Null(failure); // the 200, not the output guard's refusal
        var view = (ScenarioView)response!;
        Assert.Contains(view.Result.Warnings, w => w is { Code: "funding_source_hidden", Value: "1" });
        Assert.Empty(view.Document.Funding);
        var body = JsonSerializer.Serialize(view, Wire);
        Assert.DoesNotContain(theirs.Value.ToString(), body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Bérénice", body, StringComparison.Ordinal);

        // A's edit sends back the document without B's line: it is kept in the store, still hidden from A.
        var mine = await ValuedEnvelopeAsync(app, app.AdultA, "Livret d'Alice", 10_000m);
        var edited = await OkAsync<ScenarioView>(app, app.AdultA, new ReplaceScenarioCommand(
            id, "Appartement", Visibility.Shared, VisibilityScope.Household, Doc([new FundingLine(mine, 5_000m)])));
        Assert.Equal([new FundingLine(mine, 5_000m)], edited.Document.Funding.Select(f => new FundingLine(f.Item, D(f.Amount))));
        Assert.DoesNotContain(theirs.Value.ToString(), JsonSerializer.Serialize(edited, Wire), StringComparison.OrdinalIgnoreCase);
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var stored = await scope.ServiceProvider.GetRequiredService<IScenarioStore>().FindAsync(id, CancellationToken.None);
            Assert.Equal([new FundingLine(mine, 5_000m), new FundingLine(theirs, 16_000m)], stored!.Document.Funding);
        }
    }

    [Fact]
    public async Task A_locked_envelope_is_refused_as_funding()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var per = await ValuedEnvelopeAsync(app, app.AdultA, "PER", 30_000m, kind: "per");

        var (_, failure) = await app.InvokeAsync(app.AdultA, Create(app, app.AdultA, Doc([new FundingLine(per, 10_000m)])));

        Assert.Equal("scenario.funding_locked", failure?.Code);
        Assert.Equal("funding[0].item", Assert.Single(FieldErrors.Of(failure!)!).Path);
    }

    [Fact]
    public async Task Funding_is_refused_beyond_the_envelope_or_from_one_the_reader_cannot_see()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var livret = await ValuedEnvelopeAsync(app, app.AdultA, "Livret", 10_000m);
        var hidden = await ValuedEnvelopeAsync(app, app.AdultB, "Livret B", 10_000m);
        await using (var scope = app.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.Browser(app.AdultB);
            await scope.ServiceProvider.GetRequiredService<IItemRegistry>().ReplaceAsync(
                hidden, "Livret B", Visibility.Private, [new OwnerShare(app.AdultB, 10_000)], CancellationToken.None);
        }

        var (_, above) = await app.InvokeAsync(app.AdultA, Create(app, app.AdultA, Doc([new FundingLine(livret, 10_000.01m)])));
        var (_, unseen) = await app.InvokeAsync(app.AdultA, Create(app, app.AdultA, Doc([new FundingLine(hidden, 1_000m)])));

        Assert.Equal(("scenario.invalid_field", "funding[0].amount"), (above?.Code, FieldErrors.Of(above!)![0].Path));
        Assert.Equal(("scenario.funding_not_found", "funding[0].item"), (unseen?.Code, FieldErrors.Of(unseen!)![0].Path));
    }

    [Fact]
    public async Task A_rental_raises_the_tax_as_the_engine_says()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var salary = await app.NewIncomeSourceAsync("Salaire", app.AdultA);
        (await app.RawAsync(app.AdultA, HttpMethod.Post, $"/api/v1/income/sources/{salary.Value}/phases", new
        {
            label = "Salaire", startsOn = "2025-01-01", netAmount = new { amount = "39000", currency = "EUR" },
            every = new { count = 1, unit = "year" }, grossAmount = new { amount = "50000", currency = "EUR" },
        })).EnsureSuccessStatusCode();

        var result = await OkAsync<ScenarioResultView>(app, app.AdultA,
            new SimulateScenarioQuery(VisibilityScope.Household, Doc(use: PurchaseUse.Rental, expectedRent: 900m, regime: "micro")));

        TaxInputs inputs;
        await using (var scope = app.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.Browser(app.AdultA);
            var read = await scope.ServiceProvider.GetRequiredService<TaxEstimates>()
                .InputsAsync(2025, VisibilityScope.Household, NidApp.GoldenCaseToday, CancellationToken.None);
            Assert.True(read.TryGetValue(out inputs!));
        }
        var replayed = TaxCalculator.Compute(inputs with { RentalGross = inputs.RentalGross + 10_800m, RentalRegime = RentalRegime.Micro });
        var expected = Math.Round((replayed.TotalTax - TaxCalculator.Compute(inputs).TotalTax) / 12m, 2, MidpointRounding.AwayFromZero);
        Assert.True(expected > 0m);
        Assert.Equal(expected, D(result.Capacity.MonthlyTaxIncrease.Amount));
    }

    [Fact]
    public async Task An_existing_loan_still_running_at_a_retained_month_past_the_horizon_counts()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await NewAsync(app, Golden(app)); // paid until 2040
        // A PTZ deferred two years then 216 even payments of 250: its first payment, the retained month, falls after the one-year horizon.
        var ptz = new ScenarioLoan("PTZ", 54_000m, 0m, 240, new ScenarioDeferral(DeferralKind.Total, 24),
            new ScenarioInsurance(ScenarioInsuranceKind.FixedMonthly, 0m), 0m, 0m);
        var doc = Doc() with { Loans = [ptz], Assumptions = Doc().Assumptions with { HorizonYears = 1 } };

        var result = await OkAsync<ScenarioResultView>(app, app.AdultA, new SimulateScenarioQuery(VisibilityScope.Household, doc));

        Assert.Equal("2028-01", result.Capacity.RetainedMonth);
        // The golden loan still runs then: nothing of today's payment is given back.
        Assert.Equal(D(result.Capacity.LeftBefore.Amount) - D(result.Capacity.NewPayment.Amount) - 100m + 800m, D(result.Capacity.LeftAfter.Amount));
    }

    [Fact]
    public async Task A_purchase_in_the_past_is_bought_today_with_its_full_loan_as_debt() // Review Focus 3
    {
        await using var app = await NidApp.StartAsync(fixture);

        var past = await OkAsync<ScenarioResultView>(app, app.AdultA,
            new SimulateScenarioQuery(VisibilityScope.Household, Doc(on: new LocalDate(2024, 3, 1))));
        var today = await OkAsync<ScenarioResultView>(app, app.AdultA,
            new SimulateScenarioQuery(VisibilityScope.Household, Doc(on: NidApp.GoldenCaseToday)));

        Assert.Contains(past.Warnings, w => w.Code == "purchase_in_past");
        Assert.Equal(new LocalDate(2025, 11, 15), past.Loans[0].Schedule.Rows[0].DueDate);
        // Its first month, October 2025, has no payment yet: the whole 200 000 is owed, exactly as a purchase made today.
        Assert.Equal(today.Points.Select(p => p.With.Amount), past.Points.Select(p => p.With.Amount));
    }

    [Fact]
    public async Task A_gap_in_the_balance_is_warned_with_its_signed_amount()
    {
        await using var app = await NidApp.StartAsync(fixture);

        var result = await OkAsync<ScenarioResultView>(app, app.AdultA, new SimulateScenarioQuery(VisibilityScope.Household, Doc()));

        // 200 000 + 15 000 + 1 000 of file fees, against 200 000 borrowed and nothing put in.
        Assert.Contains(result.Warnings, w => w is { Code: "funding_gap", Value: "16000.00" });
    }

    [Fact]
    public async Task Simulate_writes_nothing()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await ValuedEnvelopeAsync(app, app.AdultA, "Livret", 40_000m);
        await using var core = PostgresFixture.NewCoreContext(app.ConnectionString);
        var before = await core.Items.CountAsync();

        await OkAsync<ScenarioResultView>(app, app.AdultA, new SimulateScenarioQuery(VisibilityScope.Household, Doc()));

        Assert.Equal(before, await core.Items.CountAsync());
    }

    [Fact]
    public async Task Delete_archives_and_the_list_forgets_it()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await OkAsync<ItemId>(app, app.AdultA, Create(app, app.AdultA, Doc()));
        var listed = await OkAsync<ScenarioList>(app, app.AdultA, new ListScenariosQuery(VisibilityScope.Household));
        Assert.Equal(id, Assert.Single(listed.Items).Id);

        await OkAsync<ItemId>(app, app.AdultA, new DeleteScenarioCommand(id));

        Assert.Empty((await OkAsync<ScenarioList>(app, app.AdultA, new ListScenariosQuery(VisibilityScope.Household))).Items);
        await using var core = PostgresFixture.NewCoreContext(app.ConnectionString);
        Assert.NotNull((await core.Items.SingleAsync(i => i.Id == id.Value)).ArchivedAt);
        var (_, gone) = await app.InvokeAsync(app.AdultA, new GetScenarioQuery(id));
        Assert.IsType<NotFoundFailure>(gone);
    }

    [Fact]
    public async Task The_other_adults_private_scenario_is_not_listed()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var theirs = await OkAsync<ItemId>(app, app.AdultB, Create(app, app.AdultB, Doc(), Visibility.Private, "Secret de B"));
        var shared = await OkAsync<ItemId>(app, app.AdultA, Create(app, app.AdultA, Doc()));

        var mine = await OkAsync<ScenarioList>(app, app.AdultA, new ListScenariosQuery(VisibilityScope.Me));
        var bs = await OkAsync<ScenarioList>(app, app.AdultB, new ListScenariosQuery(VisibilityScope.Me));

        Assert.Equal([shared], mine.Items.Select(i => i.Id));
        Assert.Equal(new HashSet<ItemId> { shared, theirs }, bs.Items.Select(i => i.Id).ToHashSet());
    }

    [Fact]
    public async Task Replace_rewrites_the_document_and_answers_the_new_result()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await OkAsync<ItemId>(app, app.AdultA, Create(app, app.AdultA, Doc()));

        var view = await OkAsync<ScenarioView>(app, app.AdultA,
            new ReplaceScenarioCommand(id, "Maison", Visibility.Shared, VisibilityScope.Household, Doc() with { Price = 250_000m }));

        Assert.Equal(("Maison", "250000.00", true), (view.Name, view.Document.Price, view.CanWrite));
        var read = await OkAsync<ScenarioView>(app, app.AdultB, new GetScenarioQuery(id));
        Assert.Equal(("250000.00", false), (read.Document.Price, read.CanWrite));
    }

    [Fact]
    public async Task Defaults_list_the_class_rates_and_the_usable_holdings()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var livret = await ValuedEnvelopeAsync(app, app.AdultA, "Livret", 40_000m);
        var per = await ValuedEnvelopeAsync(app, app.AdultA, "PER", 10_000m, kind: "per");

        var defaults = await OkAsync<ScenarioDefaultsView>(app, app.AdultA, new GetScenarioDefaultsQuery(VisibilityScope.Household));

        Assert.Equal("6", defaults.ClassRates[AllocationClasses.Equity]);
        Assert.Null(defaults.MonthlyIncome);
        Assert.Equal("locked", defaults.Holdings.Single(h => h.Id == per).Liquidity);
        Assert.Equal("40000.00", defaults.Holdings.Single(h => h.Id == livret).Value.Amount);
    }
}
