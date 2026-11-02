using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Nid.Banking.Application;
using Nid.Banking.Contracts;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.TestSupport;
using NodaTime;
using static Nid.TestSupport.ProposalKindHelpers;

namespace Nid.Banking.IntegrationTests;

/// <summary>5C spec §6.2, D20: a line linked to the charge it pays, through `banking.link_transaction`, with its rule and its follow-up.</summary>
public class LinkTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static readonly LocalDate Today = new(2026, 10, 29);

    private sealed class FixedClock : IClock
    {
        public Instant GetCurrentInstant() => Today.At(new LocalTime(10, 0)).InUtc().ToInstant();
    }

    private static LocalDate D(int month, int day) => new(2026, month, day);

    /// <summary>A host on <see cref="Today"/>, and A's « Courant A » account.</summary>
    private static async Task<(NidApp App, Guid Account)> StartAsync(PostgresFixture fixture)
    {
        var app = await BankingApi.StartAsync(fixture, overrides: s => s.AddSingleton<IClock>(new FixedClock()));
        await BankingApi.ConnectAsync(app, app.AdultA, "code-a", BankingApi.Account("uid-1", "hash-1", null, "Courant A"));
        return (app, Assert.Single((await BankingApi.AccountsAsync(app, app.AdultA)).EnumerateArray()).GetProperty("id").GetGuid());
    }

    private static async Task<Guid> AddAsync(NidApp app, Guid account, string reference, LocalDate on, decimal amount, string label = "PRLV SEPA NETFLIX 123")
    {
        await using var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
        var store = scope.ServiceProvider.GetRequiredService<ITransactionStore>();
        await store.AddAsync(account, [new NewBankTransaction(reference, on, null, amount, Currency.Eur, label, null, null, "api")], default);
        return (await store.ListAsync([account], null, null, 0, 100, default)).Single(t => t.ProviderRef == reference).Id;
    }

    private static async Task<ItemId> MonthlyAsync(NidApp app, Visibility visibility = Visibility.Shared)
    {
        var id = await app.NewSubscriptionAsync("Netflix", app.AdultA, visibility);
        (await app.RawAsync(app.AdultA, HttpMethod.Post, $"/api/v1/subscriptions/{id.Value}/phases", new
        {
            startsOn = "2026-01-05",
            price = new { amount = "12.99", currency = "EUR" },
            every = new { count = 1, unit = "month" },
            payer = new { type = "household" },
        })).EnsureSuccessStatusCode();
        return id;
    }

    /// <summary>A's property rented 750 + 50 of charges a month since 2025-01-01, private: its only reader reads « Courant A ».</summary>
    private static async Task<Guid> RentedAsync(NidApp app)
    {
        var id = await PropertyApi.NewPropertyAsync(app, PropertyApi.Home(app, visibility: "private"));
        await LoanApi.CreatedAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, $"{PropertyApi.Properties}/{id}/tenancies",
            new { label = "Bail", startsOn = "2025-01-01", rentMonthly = "750", chargesMonthly = "50" }));
        return id;
    }

    private static Task<HttpResponseMessage> ProposeAsync(NidApp app, PersonId person, Guid line, string module, Guid item)
        => app.RawAsync(person, HttpMethod.Post, Proposals, new
        {
            kind = "banking.link_transaction", targetItemId = item, payload = new { transactionId = line, module, itemId = item },
        });

    /// <summary>A proposes the link and approves it.</summary>
    private static async Task LinkAsync(NidApp app, Guid line, string module, Guid item)
    {
        var proposed = await ProposeAsync(app, app.AdultA, line, module, item);
        Assert.True(proposed.StatusCode == HttpStatusCode.Created, await proposed.Content.ReadAsStringAsync());
        var approved = await ApproveAsync(app, await JsonOf(proposed));
        Assert.True(approved.StatusCode == HttpStatusCode.OK, await approved.Content.ReadAsStringAsync());
    }

    private static async Task<BankLink?> LinkOfAsync(NidApp app, Guid line)
    {
        await using var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
        return (await scope.ServiceProvider.GetRequiredService<ILinkStore>().ForTransactionsAsync([line], default)).GetValueOrDefault(line);
    }

    private static async Task<JsonElement[]> PendingAsync(NidApp app)
        => [.. (await JsonOf(await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Proposals}?status=pending"))).GetProperty("items").EnumerateArray()];

    [Fact]
    public async Task An_approved_link_creates_the_rule_and_links_the_older_lines()
    {
        var (app, account) = await StartAsync(fixture);
        await using var _ = app;
        var older = await AddAsync(app, account, "a", D(3, 5), -12.99m);
        var line = await AddAsync(app, account, "b", D(4, 5), -12.99m);
        var netflix = await MonthlyAsync(app);

        await LinkAsync(app, line, "subscriptions", netflix.Value);

        await using var scope = app.Services.CreateAsyncScope();
        var rule = Assert.Single(await scope.ServiceProvider.GetRequiredService<IRuleStore>().ByAsync(app.AdultA, TestContext.Current.CancellationToken));
        Assert.Equal(("NETFLIX", account, netflix), (rule.LabelStem, rule.Account, rule.TargetItem));
        var linked = (await LinkOfAsync(app, line))!;
        Assert.Equal((LinkOutcome.AsExpected, "2026-04-05", $"person:{app.AdultA.Value:D}"), (linked.Outcome, linked.TargetKey, linked.DecidedBy));
        var byRule = (await LinkOfAsync(app, older))!;
        Assert.Equal((LinkOutcome.AsExpected, "2026-03-05", $"rule:{rule.Id:D}", netflix), (byRule.Outcome, byRule.TargetKey, byRule.DecidedBy, byRule.TargetItem));
    }

    [Fact]
    public async Task A_different_price_proposes_a_change_price()
    {
        var (app, account) = await StartAsync(fixture);
        await using var _ = app;
        var line = await AddAsync(app, account, "a", D(10, 5), -13.99m);
        var netflix = await MonthlyAsync(app, Visibility.Private); // its only reader reads the account

        await LinkAsync(app, line, "subscriptions", netflix.Value);

        Assert.Equal(LinkOutcome.PriceDiffers, (await LinkOfAsync(app, line))!.Outcome);
        var change = Assert.Single(await PendingAsync(app)).GetProperty("payload");
        Assert.Equal(("2026-10-05", "13.99", "EUR"), (change.GetProperty("chargedOn").GetString(),
            change.GetProperty("amount").GetProperty("amount").GetString(), change.GetProperty("amount").GetProperty("currency").GetString()));
    }

    [Fact]
    public async Task A_different_price_on_a_shared_item_from_a_private_account_proposes_nothing()
    {
        var (app, account) = await StartAsync(fixture);
        await using var _ = app;
        var line = await AddAsync(app, account, "a", D(10, 5), -13.99m);
        var netflix = await MonthlyAsync(app); // shared: B reads it, not « Courant A »

        await LinkAsync(app, line, "subscriptions", netflix.Value);

        Assert.Equal(LinkOutcome.PriceDiffers, (await LinkOfAsync(app, line))!.Outcome);
        Assert.Empty(await PendingAsync(app));
    }

    [Fact]
    public async Task A_loan_difference_proposes_nothing()
    {
        var (app, account) = await StartAsync(fixture);
        await using var _ = app;
        var body = LoanApi.Short(app); // 9 840 over 12 payments of 820 from 2026-01-15, no interest
        body["principal"] = "9840";
        body["disbursedOn"] = "2026-01-01";
        body["firstPaymentDate"] = "2026-01-15";
        var loan = await LoanApi.NewAsync(app, body);
        var line = await AddAsync(app, account, "a", D(10, 15), -850m, "PRLV SEPA CREDIT IMMO");

        await LinkAsync(app, line, "realestate", loan);

        var link = (await LinkOfAsync(app, line))!;
        Assert.Equal((LinkOutcome.PriceDiffers, 820m, "EUR"), (link.Outcome, link.Expected!.Value.Amount, link.Expected.Value.Currency.Code));
        Assert.Empty(await PendingAsync(app));
    }

    [Fact]
    public async Task A_rent_already_recorded_is_only_linked()
    {
        var (app, account) = await StartAsync(fixture);
        await using var _ = app;
        var property = await RentedAsync(app);
        var flow = await LoanApi.CreatedAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, $"{PropertyApi.Properties}/{property}/flows",
            new { occurredOn = "2026-10-03", kind = "income", amount = "800", category = "rent" }));
        var line = await AddAsync(app, account, "a", D(10, 5), 800m, "VIR SEPA DUPONT LOYER");

        await LinkAsync(app, line, "realestate", property);

        var link = (await LinkOfAsync(app, line))!;
        Assert.Equal((LinkOutcome.Income, $"flow:{flow:N}"), (link.Outcome, link.TargetKey));
        Assert.Empty(await PendingAsync(app));
    }

    [Fact]
    public async Task A_rent_without_flow_proposes_a_cash_in()
    {
        var (app, account) = await StartAsync(fixture);
        await using var _ = app;
        var property = await RentedAsync(app);
        var line = await AddAsync(app, account, "a", D(10, 5), 800m, "VIR SEPA DUPONT LOYER");

        await LinkAsync(app, line, "realestate", property);

        Assert.Equal((LinkOutcome.Income, "rent:2026-10"), ((await LinkOfAsync(app, line))!.Outcome, (await LinkOfAsync(app, line))!.TargetKey));
        var cashIn = Assert.Single(await PendingAsync(app));
        Assert.Equal(("realestate.add_property_flow", property), (cashIn.GetProperty("kind").GetString(), cashIn.GetProperty("targetItemId").GetGuid()));
        var body = cashIn.GetProperty("payload").GetProperty("body");
        Assert.Equal(($"bank:{line:D}", "2026-10-05", "income", "800.00", "rent"), (body.GetProperty("externalRef").GetString(),
            body.GetProperty("occurredOn").GetString(), body.GetProperty("kind").GetString(), body.GetProperty("amount").GetString(),
            body.GetProperty("category").GetString()));
    }

    [Fact]
    public async Task A_cash_in_cannot_be_recorded_twice()
    {
        var (app, account) = await StartAsync(fixture);
        await using var _ = app;
        var property = await RentedAsync(app);
        var line = await AddAsync(app, account, "a", D(10, 5), 800m, "VIR SEPA DUPONT LOYER");
        await LinkAsync(app, line, "realestate", property);
        var cashIn = Assert.Single(await PendingAsync(app));

        Assert.Equal(HttpStatusCode.OK, (await ApproveAsync(app, cashIn)).StatusCode);
        var again = await app.RawAsync(app.AdultA, HttpMethod.Post, $"{PropertyApi.Properties}/{property}/flows",
            JsonNode.Parse(cashIn.GetProperty("payload").GetProperty("body").GetRawText()));

        await BankingApi.AssertRefusedAsync(again, HttpStatusCode.Conflict, CoreFactCodes.ExternalRefTaken);
    }

    [Fact]
    public async Task A_corrected_cash_in_keeps_its_bank_reference()
    {
        var (app, account) = await StartAsync(fixture);
        await using var _ = app;
        var property = await RentedAsync(app);
        var line = await AddAsync(app, account, "a", D(10, 5), 800m, "VIR SEPA DUPONT LOYER");
        await LinkAsync(app, line, "realestate", property);
        var body = Assert.Single(await PendingAsync(app)).GetProperty("payload").GetProperty("body").GetRawText();
        var flow = await LoanApi.CreatedAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, $"{PropertyApi.Properties}/{property}/flows", JsonNode.Parse(body)));

        // The web's correction sends no reference.
        var corrected = await app.RawAsync(app.AdultA, HttpMethod.Put, $"{PropertyApi.Properties}/{property}/flows/{flow}",
            new { occurredOn = "2026-10-05", kind = "income", amount = "810", category = "rent" });
        Assert.Equal(HttpStatusCode.OK, corrected.StatusCode);

        await using (var scope = app.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
            var current = Assert.Single(await scope.ServiceProvider.GetRequiredService<IFlows>().FindManyAsync([(await JsonOf(corrected)).GetProperty("id").GetGuid()], TestContext.Current.CancellationToken));
            Assert.Equal($"bank:{line:D}", current.ExternalRef);
        }
        var again = await app.RawAsync(app.AdultA, HttpMethod.Post, $"{PropertyApi.Properties}/{property}/flows", JsonNode.Parse(body));
        await BankingApi.AssertRefusedAsync(again, HttpStatusCode.Conflict, CoreFactCodes.ExternalRefTaken);
    }

    [Fact]
    public async Task A_cash_in_of_another_line_is_not_this_line_s_rent()
    {
        var (app, account) = await StartAsync(fixture);
        await using var _ = app;
        var property = await RentedAsync(app);
        var late = await AddAsync(app, account, "a", D(10, 1), 800m, "VIR SEPA DUPONT LOYER"); // September's, paid late
        await LinkAsync(app, late, "realestate", property);
        Assert.Equal(HttpStatusCode.OK, (await ApproveAsync(app, Assert.Single(await PendingAsync(app)))).StatusCode);
        var line = await AddAsync(app, account, "b", D(10, 5), 800m, "VIR SEPA DUPONT LOYER");

        await LinkAsync(app, line, "realestate", property);

        Assert.Equal("rent:2026-10", (await LinkOfAsync(app, line))!.TargetKey);
        var cashIn = Assert.Single(await PendingAsync(app));
        Assert.Equal($"bank:{line:D}", cashIn.GetProperty("payload").GetProperty("body").GetProperty("externalRef").GetString());
    }

    [Fact]
    public async Task An_old_line_is_linked_without_follow_up()
    {
        var (app, account) = await StartAsync(fixture);
        await using var _ = app;
        var line = await AddAsync(app, account, "a", D(9, 5), -13.99m); // 54 days before today
        var netflix = await MonthlyAsync(app);

        await LinkAsync(app, line, "subscriptions", netflix.Value);

        Assert.Equal(LinkOutcome.PriceDiffers, (await LinkOfAsync(app, line))!.Outcome);
        Assert.Empty(await PendingAsync(app));
    }

    [Fact]
    public async Task Linking_needs_write_on_the_target()
    {
        var (app, account) = await StartAsync(fixture);
        await using var _ = app;
        var line = await AddAsync(app, account, "a", D(10, 5), -12.99m);
        var secret = await MonthlyAsync(app, Visibility.Private);

        var proposed = await ProposeAsync(app, app.AdultB, line, "subscriptions", secret.Value);

        Assert.Equal(HttpStatusCode.NotFound, proposed.StatusCode);
        Assert.Null(await LinkOfAsync(app, line));
    }

    [Fact]
    public async Task Linking_needs_write_on_the_account()
    {
        var (app, account) = await StartAsync(fixture);
        await using var _ = app;
        // « Courant A » on A's shared envelope: B reads it, only A writes it.
        var envelope = await app.NewEnvelopeAsync("Commun A", Visibility.Shared, (app.AdultA, 10_000));
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Patch, $"{BankingApi.Root}/accounts/{account}", new { envelopeId = envelope.Value })).StatusCode);
        var line = await AddAsync(app, account, "a", D(10, 5), -12.99m);
        var ofB = await app.NewSubscriptionAsync("Netflix B", app.AdultB, Visibility.Private);

        var proposed = await ProposeAsync(app, app.AdultB, line, "subscriptions", ofB.Value);

        Assert.Equal(HttpStatusCode.Forbidden, proposed.StatusCode);
        Assert.Null(await LinkOfAsync(app, line));
    }

    [Fact]
    public async Task An_edit_is_refused()
    {
        var (app, account) = await StartAsync(fixture);
        await using var _ = app;
        var line = await AddAsync(app, account, "a", D(10, 5), -12.99m);
        var netflix = await MonthlyAsync(app);
        var proposed = await ProposeAsync(app, app.AdultA, line, "subscriptions", netflix.Value);
        Assert.Equal(HttpStatusCode.Created, proposed.StatusCode);
        var proposal = await JsonOf(proposed);
        var preview = (await ReadAsync(app, proposal)).GetProperty("preview");
        Assert.Equal(("-12.99", "Netflix", "2026-10-05", "12.99", "0.00"), (preview.GetProperty("transaction").GetProperty("amount").GetString(),
            preview.GetProperty("charge").GetProperty("itemName").GetString(), preview.GetProperty("charge").GetProperty("dueOn").GetString(),
            preview.GetProperty("charge").GetProperty("amount").GetString(), preview.GetProperty("gap").GetString()));

        var approved = await ApproveAsync(app, proposal, JsonNode.Parse(proposal.GetProperty("payload").GetRawText()));

        await BankingApi.AssertRefusedAsync(approved, HttpStatusCode.UnprocessableEntity, BankingCodes.ProposalNotEditable);
        Assert.Null(await LinkOfAsync(app, line));
    }
}
