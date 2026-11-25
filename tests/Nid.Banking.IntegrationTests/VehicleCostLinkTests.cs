using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Nid.Banking.Application;
using Nid.Banking.Contracts;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.TestSupport;
using NodaTime;
using static Nid.TestSupport.ProposalKindHelpers;

namespace Nid.Banking.IntegrationTests;

/// <summary>#649: an expense line pays a running cost of a vehicle: offered as a candidate, linked, and recorded once as a Core flow.</summary>
public class VehicleCostLinkTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static readonly LocalDate Today = new(2026, 10, 29);

    private sealed class FixedClock : IClock
    {
        public Instant GetCurrentInstant() => Today.At(new LocalTime(10, 0)).InUtc().ToInstant();
    }

    private static LocalDate D(int month, int day) => new(2026, month, day);

    private static async Task<(NidApp App, Guid Account)> StartAsync(PostgresFixture fixture)
    {
        var app = await BankingApi.StartAsync(fixture, overrides: s => s.AddSingleton<IClock>(new FixedClock()));
        await BankingApi.ConnectAsync(app, app.AdultA, "code-a", BankingApi.Account("uid-1", "hash-1", null, "Courant A"));
        return (app, Assert.Single((await BankingApi.AccountsAsync(app, app.AdultA)).EnumerateArray()).GetProperty("id").GetGuid());
    }

    private static async Task<Guid> AddAsync(NidApp app, Guid account, string reference, LocalDate on, decimal amount, string label = "CB GARAGE DUPONT")
    {
        await using var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
        var store = scope.ServiceProvider.GetRequiredService<ITransactionStore>();
        await store.AddAsync(account, [new NewBankTransaction(reference, on, null, amount, Currency.Eur, label, null, null, "api")], default);
        return (await store.ListAsync([account], null, null, 0, 100, default)).Single(t => t.ProviderRef == reference).Id;
    }

    private static async Task<Guid> VehicleAsync(NidApp app, string name = "Citadine", string kind = "vehicle", string visibility = "private", PersonId? owner = null)
    {
        var response = await app.RawAsync(owner ?? app.AdultA, HttpMethod.Post, "/api/v1/assets", new
        {
            name, kind, purchasePrice = "10000", purchasedOn = "2024-01-10", visibility,
            owners = new[] { new { personId = (owner ?? app.AdultA).Value, shareBp = 10_000 } },
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await JsonOf(response)).GetProperty("id").GetGuid();
    }

    private static Task<HttpResponseMessage> LinkAsync(NidApp app, Guid line, Guid item, string? category = null)
        => app.RawAsync(app.AdultA, HttpMethod.Post, $"{BankingApi.Root}/transactions/{line}/link", new { module = "assets", itemId = item, category });

    private static async Task<JsonElement[]> PendingAsync(NidApp app)
        => [.. (await JsonOf(await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Proposals}?status=pending"))).GetProperty("items").EnumerateArray()];

    private static async Task<BankLink?> LinkOfAsync(NidApp app, Guid line)
    {
        await using var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
        return (await scope.ServiceProvider.GetRequiredService<ILinkStore>().ForTransactionsAsync([line], default)).GetValueOrDefault(line);
    }

    private static async Task<JsonElement> CostsAsync(NidApp app, Guid vehicle)
        => await JsonOf(await app.RawAsync(app.AdultA, HttpMethod.Get, $"/api/v1/assets/{vehicle}/costs?year=2026"));

    [Fact]
    public async Task An_expense_line_offers_the_vehicles_the_actor_writes()
    {
        var (app, account) = await StartAsync(fixture);
        await using var _ = app;
        await VehicleAsync(app);
        await VehicleAsync(app, "Montre", kind: "art");
        await VehicleAsync(app, "Scooter de B", visibility: "shared", owner: app.AdultB);
        var expense = await AddAsync(app, account, "a", D(10, 20), -85.40m);
        var income = await AddAsync(app, account, "b", D(10, 21), 85.40m, "VIR REMBOURSEMENT");

        var candidates = (await BankingApi.JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, $"{BankingApi.Root}/transactions/{expense}/candidates"))).EnumerateArray().ToList();

        var vehicle = Assert.Single(candidates);
        Assert.Equal(("assets", "Citadine", "vehicle_cost", "85.40"), (vehicle.GetProperty("module").GetString(), vehicle.GetProperty("itemName").GetString(),
            vehicle.GetProperty("kind").GetString(), vehicle.GetProperty("amount").GetString()));
        Assert.Empty((await BankingApi.JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, $"{BankingApi.Root}/transactions/{income}/candidates"))).EnumerateArray());
    }

    [Fact]
    public async Task Linking_proposes_the_cost_with_its_bank_reference_and_the_approval_records_it_once()
    {
        var (app, account) = await StartAsync(fixture);
        await using var _ = app;
        var car = await VehicleAsync(app);
        var line = await AddAsync(app, account, "a", D(10, 20), -85.40m);

        Assert.Equal(HttpStatusCode.OK, (await LinkAsync(app, line, car, "tyres")).StatusCode);

        Assert.Equal(("assets", car), ((await LinkOfAsync(app, line))!.TargetModule, (await LinkOfAsync(app, line))!.TargetItem!.Value.Value));
        var cost = Assert.Single(await PendingAsync(app));
        Assert.Equal(("assets.add_asset_cost", car), (cost.GetProperty("kind").GetString(), cost.GetProperty("targetItemId").GetGuid()));
        var body = cost.GetProperty("payload").GetProperty("body");
        Assert.Equal(($"bank:{line:D}", "2026-10-20", "85.40", "tyres"), (body.GetProperty("externalRef").GetString(),
            body.GetProperty("occurredOn").GetString(), body.GetProperty("amount").GetString(), body.GetProperty("category").GetString()));

        Assert.Equal(HttpStatusCode.OK, (await ApproveAsync(app, cost)).StatusCode);
        var costs = await CostsAsync(app, car);
        var row = Assert.Single(costs.GetProperty("rows").EnumerateArray());
        Assert.Equal(("tyres", "85.40", true), (row.GetProperty("category").GetString(), row.GetProperty("amount").GetProperty("amount").GetString(), row.GetProperty("fromBank").GetBoolean()));

        // The same reference refuses a second cost.
        var again = await app.RawAsync(app.AdultA, HttpMethod.Post, $"/api/v1/assets/{car}/costs", System.Text.Json.Nodes.JsonNode.Parse(body.GetRawText()));
        await BankingApi.AssertRefusedAsync(again, HttpStatusCode.Conflict, CoreFactCodes.ExternalRefTaken);
    }

    [Fact]
    public async Task The_category_defaults_to_other_and_a_wrong_one_is_refused()
    {
        var (app, account) = await StartAsync(fixture);
        await using var _ = app;
        var car = await VehicleAsync(app);
        var line = await AddAsync(app, account, "a", D(10, 20), -85.40m);

        await BankingApi.AssertRefusedAsync(await LinkAsync(app, line, car, "fuel"), HttpStatusCode.UnprocessableEntity, FieldErrors.ShapeCode);
        Assert.Null(await LinkOfAsync(app, line));
        Assert.Equal(HttpStatusCode.OK, (await LinkAsync(app, line, car)).StatusCode);

        Assert.Equal("other", Assert.Single(await PendingAsync(app)).GetProperty("payload").GetProperty("body").GetProperty("category").GetString());
    }

    [Fact]
    public async Task A_cost_already_recorded_is_only_linked()
    {
        var (app, account) = await StartAsync(fixture);
        await using var _ = app;
        var car = await VehicleAsync(app);
        var typed = await app.RawAsync(app.AdultA, HttpMethod.Post, $"/api/v1/assets/{car}/costs", new { occurredOn = "2026-10-18", amount = "85.40", category = "service" });
        var flow = (await JsonOf(typed)).GetProperty("id").GetGuid();
        var line = await AddAsync(app, account, "a", D(10, 20), -85.40m);

        Assert.Equal(HttpStatusCode.OK, (await LinkAsync(app, line, car)).StatusCode);

        Assert.Equal($"flow:{flow:N}", (await LinkOfAsync(app, line))!.TargetKey);
        Assert.Empty(await PendingAsync(app));
        // The recorded cost is taken: a second line of the same amount proposes its own.
        var second = await AddAsync(app, account, "b", D(10, 21), -85.40m);
        Assert.Equal(HttpStatusCode.OK, (await LinkAsync(app, second, car)).StatusCode);
        Assert.Single(await PendingAsync(app));
    }

    [Fact]
    public async Task Only_a_vehicle_the_actor_writes_is_linkable_and_only_by_an_expense()
    {
        var (app, account) = await StartAsync(fixture);
        await using var _ = app;
        var art = await VehicleAsync(app, "Montre", kind: "art");
        var theirs = await VehicleAsync(app, "Scooter de B", visibility: "shared", owner: app.AdultB);
        var car = await VehicleAsync(app);
        var expense = await AddAsync(app, account, "a", D(10, 20), -85.40m);
        var income = await AddAsync(app, account, "b", D(10, 21), 85.40m, "VIR REMBOURSEMENT");

        await BankingApi.AssertRefusedAsync(await LinkAsync(app, expense, art), HttpStatusCode.UnprocessableEntity, BankingCodes.LinkTarget);
        Assert.Contains((await LinkAsync(app, expense, theirs)).StatusCode, new[] { HttpStatusCode.Forbidden, HttpStatusCode.NotFound });
        await BankingApi.AssertRefusedAsync(await LinkAsync(app, income, car), HttpStatusCode.UnprocessableEntity, BankingCodes.LinkTarget);
        Assert.Null(await LinkOfAsync(app, expense));
        Assert.Empty(await PendingAsync(app));
    }

    [Fact]
    public async Task A_vehicle_link_makes_no_rule_and_gives_the_line_its_category()
    {
        var (app, account) = await StartAsync(fixture);
        await using var _ = app;
        var car = await VehicleAsync(app);
        var line = await AddAsync(app, account, "a", D(10, 20), -85.40m);
        var other = await AddAsync(app, account, "b", D(9, 20), -60m);

        Assert.Equal(HttpStatusCode.OK, (await LinkAsync(app, line, car)).StatusCode);

        await using var scope = app.Services.CreateAsyncScope();
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<IRuleStore>().ByAsync(app.AdultA, TestContext.Current.CancellationToken));
        Assert.Null(await LinkOfAsync(app, other));
        var view = await JsonOf(await app.RawAsync(app.AdultA, HttpMethod.Get, $"{BankingApi.Root}/transactions?state=linked"));
        Assert.Contains("Entretien véhicule", view.ToString());
    }
}
