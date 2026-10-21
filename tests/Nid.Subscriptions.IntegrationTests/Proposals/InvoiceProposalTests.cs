using System.Globalization;
using System.Net;
using System.Text.Json;
using Nid.Core.Contracts;
using Nid.TestSupport;
using static Nid.Subscriptions.IntegrationTests.Proposals.ProposalKindHelpers;

namespace Nid.Subscriptions.IntegrationTests.Proposals;

/// <summary>Spec 4D1 §4: an invoice compared with the price of the phase open that day, to the cent.</summary>
public class InvoiceProposalTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string Kind = "subscriptions.change_price";

    private static object Invoice(Guid subscription, string amount, string chargedOn = "2025-09-01", string currency = "EUR")
        => new { kind = Kind, targetItemId = subscription, sourceLabel = "Facture septembre", payload = new { chargedOn, amount = new { amount, currency } } };

    private static Task<HttpResponseMessage> PostAsync(NidApp app, object body) => app.RawAsync(app.AdultA, HttpMethod.Post, ProposalRouteTests.Proposals, body);

    private static async Task<List<JsonElement>> PhasesAsync(NidApp app, ItemId subscription)
        => [.. JsonDocument.Parse(await app.GetRawAsync(app.AdultA, $"/api/v1/subscriptions/{subscription.Value}")).RootElement.GetProperty("phases").EnumerateArray()];

    private static async Task<int> ProposalCountAsync(NidApp app)
        => JsonDocument.Parse(await app.GetRawAsync(app.AdultA, ProposalRouteTests.Proposals)).RootElement.GetProperty("items").GetArrayLength();

    private static decimal Amount(JsonElement money) => decimal.Parse(money.GetProperty("amount").GetString()!, CultureInfo.InvariantCulture);

    private static async Task AssertRefusedAtAsync(HttpResponseMessage response, string path, string code)
    {
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var error = (await JsonOf(response)).GetProperty("errors")[0];
        Assert.Equal((path, code), (error.GetProperty("path").GetString(), error.GetProperty("code").GetString()));
    }

    [Fact]
    public async Task The_current_price_is_conforme_and_creates_nothing()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var subscription = await app.NewMonthlyAsync(app.AdultA, 9.99m); // from 2025-01-01

        foreach (var amount in new[] { "9.99", "9.9900" })
            Assert.Equal(HttpStatusCode.NoContent, (await PostAsync(app, Invoice(subscription.Value, amount))).StatusCode);

        Assert.Equal(0, await ProposalCountAsync(app));
        var phases = await PhasesAsync(app, subscription);
        Assert.Single(phases);
        Assert.Equal(9.99m, Amount(phases[0].GetProperty("price")));
    }

    [Fact]
    public async Task Another_price_is_previewed_then_applied_from_chargedOn_keeping_the_cadence_and_is_visible_after_approval()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var subscription = await app.NewMonthlyAsync(app.AdultA, 9.99m);

        var proposal = await SubmitAsync(app, Invoice(subscription.Value, "10.99"));
        Assert.Equal("2025-09-01", proposal.GetProperty("subjectKey").GetString());
        var detail = await ReadAsync(app, proposal);
        var preview = detail.GetProperty("preview");
        Assert.Equal(9.99m, Amount(preview.GetProperty("current").GetProperty("price")));
        Assert.Equal("month", preview.GetProperty("current").GetProperty("every").GetProperty("unit").GetString());
        Assert.Equal(10.99m, Amount(preview.GetProperty("proposed").GetProperty("price")));
        Assert.Equal("2025-09-01", preview.GetProperty("startsOn").GetString());

        Assert.Equal(HttpStatusCode.OK, (await ApproveAsync(app, proposal)).StatusCode);

        // ChangePriceHandler's own unit of work joins the approval's: the new phase is durable.
        var phases = await PhasesAsync(app, subscription);
        Assert.Equal(2, phases.Count);
        Assert.Equal(("2025-09-01", 10.99m, "month"), (phases[1].GetProperty("startsOn").GetString(), Amount(phases[1].GetProperty("price")),
            phases[1].GetProperty("every").GetProperty("unit").GetString()));
    }

    [Fact]
    public async Task No_open_phase_is_refused_and_phase_range_only_for_a_different_price()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var bare = await app.NewSubscriptionAsync("Sans phase", app.AdultA, Visibility.Shared);
        var monthly = await app.NewMonthlyAsync(app.AdultA, 9.99m);

        await AssertRefusedAtAsync(await PostAsync(app, Invoice(bare.Value, "9.99")), "payload.chargedOn", "subscriptions.no_open_phase");
        await AssertRefusedAtAsync(await PostAsync(app, Invoice(monthly.Value, "10.99", chargedOn: "2025-01-01")), "payload.chargedOn", "subscriptions.phase_range");
        // The same price on the phase's first day is simply « conforme ».
        Assert.Equal(HttpStatusCode.NoContent, (await PostAsync(app, Invoice(monthly.Value, "9.99", chargedOn: "2025-01-01"))).StatusCode);
    }

    [Fact]
    public async Task An_invoice_just_applied_sent_again_is_conforme()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var subscription = await app.NewMonthlyAsync(app.AdultA, 9.99m);
        Assert.Equal(HttpStatusCode.OK, (await ApproveAsync(app, await SubmitAsync(app, Invoice(subscription.Value, "10.99")))).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await PostAsync(app, Invoice(subscription.Value, "10.99"))).StatusCode);
        Assert.Equal(2, (await PhasesAsync(app, subscription)).Count);
    }

    [Fact]
    public async Task A_subscription_can_become_free()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var subscription = await app.NewMonthlyAsync(app.AdultA, 9.99m);

        Assert.Equal(HttpStatusCode.OK, (await ApproveAsync(app, await SubmitAsync(app, Invoice(subscription.Value, "0.00")))).StatusCode);

        Assert.Equal(0m, Amount((await PhasesAsync(app, subscription))[1].GetProperty("price")));
        await AssertRefusedAtAsync(await PostAsync(app, Invoice(subscription.Value, "-1.00")), "payload.amount.amount", "request.invalid");
    }

    [Fact]
    public async Task Invoices_the_subscription_refuses_answer_at_their_path()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var monthly = await app.NewMonthlyAsync(app.AdultA, 9.99m);

        await AssertRefusedAtAsync(await PostAsync(app, Invoice(monthly.Value, "10.99", currency: "USD")), "payload.amount.currency", CoreFactCodes.CurrencyMismatch);
        await AssertRefusedAtAsync(await PostAsync(app, Invoice(monthly.Value, "10,99")), "payload.amount.amount", "request.invalid");
        await AssertRefusedAtAsync(await PostAsync(app, Invoice(monthly.Value, "10.99", chargedOn: "01/09/2025")), "payload.chargedOn", "request.invalid");
    }

    [Fact]
    public async Task An_edit_is_what_is_applied()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var subscription = await app.NewMonthlyAsync(app.AdultA, 9.99m);
        var proposal = await SubmitAsync(app, Invoice(subscription.Value, "10.99"));

        var approved = await ApproveAsync(app, proposal, System.Text.Json.Nodes.JsonNode.Parse(
            """{ "chargedOn": "2025-10-01", "amount": { "amount": "11.49", "currency": "EUR" } }"""));

        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        var phases = await PhasesAsync(app, subscription);
        Assert.Equal(("2025-10-01", 11.49m), (phases[1].GetProperty("startsOn").GetString(), Amount(phases[1].GetProperty("price"))));
    }

    [Fact]
    public async Task Another_invoice_of_the_same_date_supersedes_the_pending_one()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var subscription = await app.NewMonthlyAsync(app.AdultA, 9.99m);

        var first = await SubmitAsync(app, Invoice(subscription.Value, "10.99"));
        var second = await SubmitAsync(app, Invoice(subscription.Value, "11.99"));

        var old = (await ReadAsync(app, first)).GetProperty("proposal");
        Assert.Equal("superseded", old.GetProperty("status").GetString());
        Assert.Equal(second.GetProperty("id").GetGuid(), old.GetProperty("supersededById").GetGuid());
    }

    private static async Task ChangePriceDirectlyAsync(NidApp app, ItemId subscription, string amount, string startsOn = "2025-09-01")
        => (await app.RawAsync(app.AdultA, HttpMethod.Post, $"/api/v1/subscriptions/{subscription.Value}/price-changes",
            new { startsOn, price = new { amount, currency = "EUR" } })).EnsureSuccessStatusCode();

    [Fact]
    public async Task A_phase_that_charged_all_its_cycles_is_not_open()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var subscription = await app.NewSubscriptionAsync("Trois mois", app.AdultA, Visibility.Shared);
        (await app.RawAsync(app.AdultA, HttpMethod.Post, $"/api/v1/subscriptions/{subscription.Value}/phases", new
        {
            startsOn = "2025-01-01", price = new { amount = "9.99", currency = "EUR" }, every = new { count = 1, unit = "month" },
            maxCycles = 3, payer = new { type = "household" },
        })).EnsureSuccessStatusCode();

        foreach (var amount in new[] { "9.99", "10.99" })
            await AssertRefusedAtAsync(await PostAsync(app, Invoice(subscription.Value, amount)), "payload.chargedOn", "subscriptions.no_open_phase");
    }

    [Fact]
    public async Task Approving_a_proposal_now_conforme_decides_it_and_writes_nothing()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var subscription = await app.NewMonthlyAsync(app.AdultA, 9.99m);
        var proposal = await SubmitAsync(app, Invoice(subscription.Value, "10.99"));
        await ChangePriceDirectlyAsync(app, subscription, "10.99");

        var approved = await ApproveAsync(app, proposal);

        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        var view = await JsonOf(approved);
        Assert.Equal("approved", view.GetProperty("status").GetString());
        Assert.Equal(0, view.GetProperty("appliedRefs").GetArrayLength());
        Assert.Equal(2, (await PhasesAsync(app, subscription)).Count);
    }

    [Fact]
    public async Task An_edit_back_to_the_current_price_is_approved_without_a_new_phase()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var subscription = await app.NewMonthlyAsync(app.AdultA, 9.99m);
        var proposal = await SubmitAsync(app, Invoice(subscription.Value, "10.99"));

        var approved = await ApproveAsync(app, proposal, System.Text.Json.Nodes.JsonNode.Parse(
            """{ "chargedOn": "2025-01-01", "amount": { "amount": "9.99", "currency": "EUR" } }"""));

        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        Assert.Equal(0, (await JsonOf(approved)).GetProperty("appliedRefs").GetArrayLength());
        Assert.Single(await PhasesAsync(app, subscription));
    }

    [Fact]
    public async Task An_invalid_edit_is_refused_at_its_path_and_the_proposal_stays_pending()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var subscription = await app.NewMonthlyAsync(app.AdultA, 9.99m);
        var proposal = await SubmitAsync(app, Invoice(subscription.Value, "10.99"));

        var refused = await ApproveAsync(app, proposal, System.Text.Json.Nodes.JsonNode.Parse(
            """{ "chargedOn": "2025-01-01", "amount": { "amount": "11.00", "currency": "EUR" } }"""));

        await AssertRefusedAtAsync(refused, "payload.chargedOn", "subscriptions.phase_range");
        Assert.Equal("pending", (await ReadAsync(app, proposal)).GetProperty("proposal").GetProperty("status").GetString());
        Assert.Single(await PhasesAsync(app, subscription));
    }
}
