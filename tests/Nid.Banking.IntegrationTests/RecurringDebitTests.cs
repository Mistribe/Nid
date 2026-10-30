using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Nid.Banking.Application;
using Nid.Banking.Application.Reconciliation;
using Nid.Banking.Application.Sync;
using Nid.Banking.Contracts;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.TestSupport;
using NodaTime;
using UnambitiousFx.Synapse.Abstractions;
using static Nid.TestSupport.ProposalKindHelpers;

namespace Nid.Banking.IntegrationTests;

/// <summary>5C spec §6.4: unlinked debits that come back at a steady rhythm become a subscription proposal, once.</summary>
public class RecurringDebitTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string Iban = "FR7630006000011234567890189";
    private const string Kind = "banking.create_subscription";
    private static readonly LocalDate Today = new(2026, 10, 29);

    private sealed class FixedClock : IClock
    {
        public Instant GetCurrentInstant() => Today.At(new LocalTime(10, 0)).InUtc().ToInstant();
    }

    private static LocalDate D(int month, int day) => new(2026, month, day);

    private static Task<NidApp> StartAsync(PostgresFixture fixture)
        => BankingApi.StartAsync(fixture, overrides: s => s.AddSingleton<IClock>(new FixedClock()));

    private static async Task<Guid> AccountAsync(NidApp app, PersonId person, string name)
        => (await BankingApi.AccountsAsync(app, person)).EnumerateArray().Single(a => a.GetProperty("name").GetString() == name).GetProperty("id").GetGuid();

    /// <summary>A's « Courant A », IBAN ending 0189.</summary>
    private static async Task<Guid> CourantAsync(NidApp app)
    {
        await BankingApi.ConnectAsync(app, app.AdultA, "code-a", BankingApi.Account("uid-1", "hash-1", Iban, "Courant A"));
        return await AccountAsync(app, app.AdultA, "Courant A");
    }

    private static async Task<Guid> AddAsync(NidApp app, Guid account, string reference, LocalDate on, decimal amount, string label = "PRLV SEPA SPOTIFY 123")
    {
        await using var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
        var store = scope.ServiceProvider.GetRequiredService<ITransactionStore>();
        await store.AddAsync(account, [new NewBankTransaction(reference, on, null, amount, Currency.Eur, label, null, null, "api")], default);
        return (await store.ListAsync([account], null, null, 0, 1000, default)).Single(t => t.ProviderRef == reference).Id;
    }

    /// <summary>Three monthly debits of 10.99, the 10th of August to October.</summary>
    private static async Task<Guid[]> MonthlyLinesAsync(NidApp app, Guid account, string label = "PRLV SEPA SPOTIFY 123", decimal amount = -10.99m)
        => [await AddAsync(app, account, "m-08", D(8, 10), amount, label), await AddAsync(app, account, "m-09", D(9, 10), amount, label),
            await AddAsync(app, account, "m-10", D(10, 10), amount, label)];

    /// <summary>The nightly step, as the job runs it: the system, no HTTP request.</summary>
    private static async Task<int> ProposeAsync(NidApp app)
    {
        await using var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
        return await scope.ServiceProvider.GetRequiredService<RecurringDebits>().ProposeAsync(Today, default);
    }

    private static async Task<JsonElement[]> PendingAsync(NidApp app, PersonId person)
        => [.. (await JsonOf(await app.RawAsync(person, HttpMethod.Get, $"{Proposals}?status=pending"))).GetProperty("items").EnumerateArray()
            .Where(p => p.GetProperty("kind").GetString() == Kind)];

    private static async Task<BankLink?> LinkOfAsync(NidApp app, Guid line)
    {
        await using var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
        return (await scope.ServiceProvider.GetRequiredService<ILinkStore>().ForTransactionsAsync([line], default)).GetValueOrDefault(line);
    }

    [Fact]
    public async Task Three_monthly_debits_become_one_proposal()
    {
        await using var app = await StartAsync(fixture);
        await CourantAsync(app);
        app.Banks.Balances[("session-code-a", "uid-1")] = [new ProviderBalance("CLBD", 1000m, "EUR", Today)];
        app.Banks.Transactions[("session-code-a", "uid-1")] =
        [
            new ProviderTransaction("s-08", D(8, 10), null, -10.99m, "EUR", "PRLV SEPA SPOTIFY 123", null, null),
            new ProviderTransaction("s-09", D(9, 9), null, -10.99m, "EUR", "PRLV SEPA SPOTIFY 456", null, null),
            new ProviderTransaction("s-10", D(10, 10), null, -11.49m, "EUR", "PRLV SEPA SPOTIFY 789", null, null),
        ];

        // The nightly sync: the lines are read, reconciled, then their rhythm is proposed.
        await using (var scope = app.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
            await scope.ServiceProvider.GetRequiredService<IInvoker>().InvokeAsync(new SyncAllCommand());
        }

        var proposal = Assert.Single(await PendingAsync(app, app.AdultA));
        Assert.Equal(("Banque", JsonValueKind.Null), (proposal.GetProperty("sourceLabel").GetString(), proposal.GetProperty("targetItemId").ValueKind));
        var payload = proposal.GetProperty("payload");
        Assert.Equal(("SPOTIFY", 3), (payload.GetProperty("stem").GetString(), payload.GetProperty("lines").GetArrayLength()));
        var body = payload.GetProperty("subscription");
        Assert.Equal(("SPOTIFY", "private", true, "Compte payeur : Courant A ·0189"), (body.GetProperty("displayName").GetString(),
            body.GetProperty("visibility").GetString(), body.GetProperty("autoRenew").GetBoolean(), body.GetProperty("note").GetString()));
        var owner = Assert.Single(body.GetProperty("owners").EnumerateArray());
        Assert.Equal((app.AdultA.Value, 10_000), (owner.GetProperty("personId").GetGuid(), owner.GetProperty("shareBp").GetInt32()));
        var phase = Assert.Single(body.GetProperty("phases").EnumerateArray());
        Assert.Equal(("2026-08-10", "11.49", "EUR", 1, "month", "person", app.AdultA.Value),
            (phase.GetProperty("startsOn").GetString(), phase.GetProperty("price").GetProperty("amount").GetString(),
             phase.GetProperty("price").GetProperty("currency").GetString(), phase.GetProperty("every").GetProperty("count").GetInt32(),
             phase.GetProperty("every").GetProperty("unit").GetString(), phase.GetProperty("payer").GetProperty("type").GetString(),
             phase.GetProperty("payer").GetProperty("personId").GetGuid()));

        var preview = (await ReadAsync(app, proposal)).GetProperty("preview");
        Assert.Equal(("Courant A ·0189", "monthly"), (preview.GetProperty("account").GetString(), preview.GetProperty("cadence").GetString()));
        Assert.Equal(["2026-08-10 -10.99 EUR", "2026-09-09 -10.99 EUR", "2026-10-10 -11.49 EUR"], preview.GetProperty("lines").EnumerateArray().Select(l =>
            $"{l.GetProperty("bookedOn").GetString()} {l.GetProperty("amount").GetString()} {l.GetProperty("currency").GetString()}").ToArray());
    }

    [Fact]
    public async Task A_group_is_proposed_once_even_after_rejection()
    {
        await using var app = await StartAsync(fixture);
        await MonthlyLinesAsync(app, await CourantAsync(app));

        Assert.Equal(1, await ProposeAsync(app));
        var proposal = Assert.Single(await PendingAsync(app, app.AdultA));
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Proposals}/{proposal.GetProperty("id").GetGuid()}/reject",
            new { version = proposal.GetProperty("version").GetUInt32() })).StatusCode);

        Assert.Equal(0, await ProposeAsync(app));
        Assert.Empty(await PendingAsync(app, app.AdultA));
    }

    [Fact]
    public async Task Approval_creates_the_subscription_links_the_lines_and_the_rule()
    {
        await using var app = await StartAsync(fixture);
        var courant = await CourantAsync(app);
        var lines = await MonthlyLinesAsync(app, courant);
        await ProposeAsync(app);

        var approved = await ApproveAsync(app, Assert.Single(await PendingAsync(app, app.AdultA)));

        Assert.True(approved.StatusCode == HttpStatusCode.OK, await approved.Content.ReadAsStringAsync());
        var subscription = Assert.Single((await JsonOf(approved)).GetProperty("appliedRefs").EnumerateArray()).GetGuid();
        var detail = JsonDocument.Parse(await app.GetRawAsync(app.AdultA, $"/api/v1/subscriptions/{subscription}")).RootElement;
        Assert.Equal("SPOTIFY", detail.GetProperty("displayName").GetString());
        string[] keys = ["2026-08-10", "2026-09-10", "2026-10-10"];
        foreach (var (line, key) in lines.Zip(keys))
        {
            var link = (await LinkOfAsync(app, line))!;
            Assert.Equal(("subscriptions", subscription, key, LinkOutcome.AsExpected, $"person:{app.AdultA.Value:D}"),
                (link.TargetModule, link.TargetItem!.Value.Value, link.TargetKey, link.Outcome, link.DecidedBy));
        }
        await using var scope = app.Services.CreateAsyncScope();
        var rule = Assert.Single(await scope.ServiceProvider.GetRequiredService<IRuleStore>().ByAsync(app.AdultA, default));
        Assert.Equal((courant, "SPOTIFY", "subscriptions", subscription), (rule.Account, rule.LabelStem, rule.TargetModule, rule.TargetItem.Value));
    }

    [Fact]
    public async Task A_group_matching_a_known_charge_is_not_proposed()
    {
        await using var app = await StartAsync(fixture);
        var courant = await CourantAsync(app);
        var netflix = await app.NewSubscriptionAsync("Netflix", app.AdultA, Visibility.Shared);
        (await app.RawAsync(app.AdultA, HttpMethod.Post, $"/api/v1/subscriptions/{netflix.Value}/phases", new
        {
            startsOn = "2026-01-10", price = new { amount = "12.99", currency = "EUR" }, every = new { count = 1, unit = "month" }, payer = new { type = "household" },
        })).EnsureSuccessStatusCode();
        // Unlinked (never reconciled), but one of them is a known charge: the group is not new.
        await AddAsync(app, courant, "n-08", D(8, 10), -13.49m, "PRLV SEPA NETFLIX 1");
        await AddAsync(app, courant, "n-09", D(9, 10), -13.49m, "PRLV SEPA NETFLIX 2");
        await AddAsync(app, courant, "n-10", D(10, 10), -12.99m, "PRLV SEPA NETFLIX 3");

        Assert.Equal(0, await ProposeAsync(app));

        Assert.Empty(await PendingAsync(app, app.AdultA));
        await using var scope = app.Services.CreateAsyncScope();
        // Not remembered either: the group is free to be proposed once the charge is gone.
        Assert.True(await scope.ServiceProvider.GetRequiredService<IRuleStore>().TryRememberGroupAsync(courant, "NETFLIX", Instant.FromUnixTimeSeconds(0), default));
    }

    [Fact]
    public async Task A_joint_account_proposes_a_shared_subscription()
    {
        await using var app = await StartAsync(fixture);
        await BankingApi.ConnectAsync(app, app.AdultA, "code-a", BankingApi.Account("uid-1", "hash-1", Iban, "Joint"));
        var joint = await AccountAsync(app, app.AdultA, "Joint");
        var envelope = await app.NewEnvelopeAsync("Commun", Visibility.Shared, (app.AdultA, 5000), (app.AdultB, 5000));
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Patch, $"{BankingApi.Root}/accounts/{joint}",
            new { envelopeId = envelope.Value })).StatusCode);
        await MonthlyLinesAsync(app, joint);

        Assert.Equal(1, await ProposeAsync(app));

        var body = Assert.Single(await PendingAsync(app, app.AdultA)).GetProperty("payload").GetProperty("subscription");
        Assert.Equal("shared", body.GetProperty("visibility").GetString());
        Assert.Equal([(app.AdultA.Value, 5000), (app.AdultB.Value, 5000)], body.GetProperty("owners").EnumerateArray()
            .Select(o => (o.GetProperty("personId").GetGuid(), o.GetProperty("shareBp").GetInt32())).OrderBy(o => o.Item1 == app.AdultB.Value).ToArray());
        var payer = Assert.Single(body.GetProperty("phases").EnumerateArray()).GetProperty("payer");
        Assert.Equal("household", payer.GetProperty("type").GetString());
        Assert.Empty(await PendingAsync(app, app.AdultB));
    }
    [Fact]
    public async Task The_depositor_is_a_reader_who_owns_the_subscription()
    {
        await using var app = await StartAsync(fixture);
        // B's connection sees the account first (B owns it); A's too, and A puts it on A's own shared envelope.
        await BankingApi.ConnectAsync(app, app.AdultB, "code-b", BankingApi.Account("uid-b", "hash-b", Iban, "Joint"));
        await BankingApi.ConnectAsync(app, app.AdultA, "code-a", BankingApi.Account("uid-a", "hash-a", Iban, "Joint"));
        var joint = await AccountAsync(app, app.AdultA, "Joint");
        var envelope = await app.NewEnvelopeAsync("Commun", Visibility.Shared, (app.AdultA, 10_000));
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Patch, $"{BankingApi.Root}/accounts/{joint}",
            new { envelopeId = envelope.Value })).StatusCode);
        await MonthlyLinesAsync(app, joint);

        Assert.Equal(1, await ProposeAsync(app));

        var proposal = Assert.Single(await PendingAsync(app, app.AdultA));
        Assert.Equal(app.AdultA.Value, proposal.GetProperty("createdByPersonId").GetGuid());
        Assert.Empty(await PendingAsync(app, app.AdultB));
    }
}
