using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Nid.Banking.Application;
using Nid.Banking.Contracts;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.TestSupport;
using NodaTime;
using Duration = NodaTime.Duration;

namespace Nid.Banking.IntegrationTests;

/// <summary>Bank sync spec §5: the banking source tells a connection's creator, and only them, that a consent ends or a sync keeps failing (today: 2025-10-15).</summary>
public class BankingNotificationTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static readonly Instant Now = NidApp.GoldenCaseToday.AtMidnight().InUtc().ToInstant();

    private static async Task<Guid> ConnectionAsync(NidApp app, PersonId person, Duration validFor)
    {
        await using var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
        var store = scope.ServiceProvider.GetRequiredService<IConnectionStore>();
        var id = await store.CreatePendingAsync(person, "Boursorama", "FR", SHA256.HashData(Encoding.UTF8.GetBytes(Guid.NewGuid().ToString())), Now + Duration.FromMinutes(15), default);
        await store.ActivateAsync(id, "session", Now + validFor, default);
        return id;
    }

    private static Task<IReadOnlyList<Signal>> SignalsAsync(NidApp app, PersonId reader) => SignalsAsync(app, reader, NidApp.GoldenCaseToday);

    private static async Task<IReadOnlyList<Signal>> SignalsAsync(NidApp app, PersonId reader, LocalDate today)
    {
        await using var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.Notifier(reader);
        var source = scope.ServiceProvider.GetServices<INotificationSource>().Single(s => s.Name == "banking");
        var result = await source.SignalsAsync(reader, today, today.PlusDays(90), CancellationToken.None);
        Assert.True(result.TryGetValue(out var signals), result.TryGetFailure(out var f) ? f!.Message : "");
        return signals!;
    }

    [Fact]
    public async Task A_consent_ending_in_ten_days_tells_its_creator_only()
    {
        await using var app = await BankingApi.StartAsync(fixture);
        var id = await ConnectionAsync(app, app.AdultA, Duration.FromDays(10));

        var signal = Assert.Single(await SignalsAsync(app, app.AdultA));

        Assert.Equal("banking.consent_expiring", signal.Kind);
        Assert.Equal($"banking.consent_expiring:{id:D}:2025-10-25:14", signal.Key);
        Assert.Empty(signal.ItemIds);
        Assert.Equal(new KeyValuePair<string, string>("bank", "Boursorama"), Assert.Single(signal.Params));
        Assert.Empty(await SignalsAsync(app, app.AdultB));
    }

    [Fact]
    public async Task A_consent_far_from_its_end_says_nothing()
    {
        await using var app = await BankingApi.StartAsync(fixture);
        await ConnectionAsync(app, app.AdultA, Duration.FromDays(60));

        Assert.Empty(await SignalsAsync(app, app.AdultA));
    }

    [Fact]
    public async Task An_expired_consent_is_told()
    {
        await using var app = await BankingApi.StartAsync(fixture);
        var id = await ConnectionAsync(app, app.AdultA, Duration.FromDays(10));
        await using (var scope = app.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
            await scope.ServiceProvider.GetRequiredService<IConnectionStore>().SetStatusAsync(id, ConnectionStatus.Expired, null, TestContext.Current.CancellationToken);
        }

        var signal = Assert.Single(await SignalsAsync(app, app.AdultA));

        Assert.Equal($"banking.consent_expired:{id:D}:2025-10-25", signal.Key);
    }

    [Fact]
    public async Task A_consent_revoked_by_the_sync_is_told_as_expired_to_its_creator_only()
    {
        await using var app = await BankingApi.StartAsync(fixture);
        var id = await ConnectionAsync(app, app.AdultA, Duration.FromDays(60));
        await using (var scope = app.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
            await scope.ServiceProvider.GetRequiredService<IConnectionStore>().SetStatusAsync(id, ConnectionStatus.Revoked, null, TestContext.Current.CancellationToken);
        }

        var signal = Assert.Single(await SignalsAsync(app, app.AdultA));

        Assert.Equal($"banking.consent_expired:{id:D}:2025-12-14", signal.Key);
        Assert.Empty(await SignalsAsync(app, app.AdultB));
    }

    [Fact]
    public async Task Two_failing_nights_tell_a_failing_sync_and_one_does_not()
    {
        await using var app = await BankingApi.StartAsync(fixture);
        var id = await ConnectionAsync(app, app.AdultA, Duration.FromDays(60));
        await using var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
        var store = scope.ServiceProvider.GetRequiredService<IConnectionStore>();

        await store.RecordSyncAsync(id, Now, failed: true, TestContext.Current.CancellationToken);
        Assert.Empty(await SignalsAsync(app, app.AdultA));
        await store.RecordSyncAsync(id, Now, failed: true, TestContext.Current.CancellationToken);

        var signal = Assert.Single(await SignalsAsync(app, app.AdultA));
        Assert.Equal("banking.sync_failing", signal.Kind);
        Assert.StartsWith($"banking.sync_failing:{id:D}:", signal.Key);
    }

    [Fact]
    public async Task Banking_turned_off_gives_no_signal()
    {
        await using var app = await BankingApi.StartAsync(fixture, enable: false);
        await ConnectionAsync(app, app.AdultA, Duration.FromDays(10));

        Assert.Empty(await SignalsAsync(app, app.AdultA));
    }

    private static async Task<Guid> AccountAsync(NidApp app)
    {
        await BankingApi.ConnectAsync(app, app.AdultA, "code-a", BankingApi.Account("uid-1", "hash-1", null, "Courant A"));
        return Assert.Single((await BankingApi.AccountsAsync(app, app.AdultA)).EnumerateArray()).GetProperty("id").GetGuid();
    }

    /// <summary>A line of the account, linked to the item as the job would have (decided at noon UTC on <paramref name="decidedOn"/>).</summary>
    private static async Task<Guid> LinkedAsync(
        NidApp app, Guid account, ItemId item, string module, string key, LocalDate bookedOn, decimal amount, LinkOutcome outcome, decimal expected, LocalDate decidedOn)
    {
        await using var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
        var lines = scope.ServiceProvider.GetRequiredService<ITransactionStore>();
        var reference = Guid.NewGuid().ToString("N");
        await lines.AddAsync(account, [new NewBankTransaction(reference, bookedOn, null, amount, Currency.Eur, "PRLV SEPA NETFLIX 123", null, null, "api")], default);
        var line = (await lines.ListAsync([account], null, null, 0, 1000, default)).Single(t => t.ProviderRef == reference);
        await scope.ServiceProvider.GetRequiredService<ILinkStore>().SetAsync(
            new BankLink(line.Id, module, item, key, null, outcome, Money.Create(expected, Currency.Eur).TryGetValue(out var money) ? money : throw new InvalidOperationException(), "job", decidedOn.At(new LocalTime(12, 0)).InUtc().ToInstant()),
            TransactionLinkState.Linked, default);
        return line.Id;
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

    private static LocalDate D(int month, int day) => new(2026, month, day);

    private static IEnumerable<Signal> Findings(IEnumerable<Signal> signals, string kind) => signals.Where(s => s.Kind == kind);

    [Fact]
    public async Task A_loan_difference_is_a_finding_for_its_readers()
    {
        await using var app = await BankingApi.StartAsync(fixture);
        var account = await AccountAsync(app);
        var loan = new ItemId(await LoanApi.NewAsync(app, LoanApi.Golden(app)));
        var line = await LinkedAsync(app, account, loan, "realestate", "2025-10-15", new LocalDate(2025, 10, 10), -1200.5m, LinkOutcome.PriceDiffers, 1159.92m, new LocalDate(2025, 10, 11));

        var signal = Assert.Single(Findings(await SignalsAsync(app, app.AdultA), "banking.amount_differs"));

        Assert.Equal($"banking.amount_differs:{line:N}", signal.Key);
        Assert.Equal([loan], signal.ItemIds);
        Assert.Equal(new LocalDate(2025, 10, 10), signal.DueOn);
        Assert.Equal((1200.5m, 1159.92m, "EUR", "loans"), (decimal.Parse(signal.Params["amount"], CultureInfo.InvariantCulture),
            decimal.Parse(signal.Params["expected"], CultureInfo.InvariantCulture), signal.Params["currency"], signal.Params["module"]));
        Assert.Equal(4, signal.Params.Count); // no label, no account
        Assert.Empty(Findings(await SignalsAsync(app, app.AdultB), "banking.amount_differs")); // the loan is shared, the line is on A's account
    }

    [Fact]
    public async Task A_same_currency_subscription_difference_is_no_finding()
    {
        await using var app = await BankingApi.StartAsync(fixture);
        var account = await AccountAsync(app);
        var netflix = await MonthlyAsync(app, Visibility.Private); // its only reader reads the account: a price change was proposed
        await LinkedAsync(app, account, netflix, "subscriptions", "2025-10-05", new LocalDate(2025, 10, 5), -14.99m, LinkOutcome.PriceDiffers, 12.99m, new LocalDate(2025, 10, 6));

        Assert.Empty(Findings(await SignalsAsync(app, app.AdultA), "banking.amount_differs"));
    }

    [Fact]
    public async Task B_gets_no_finding_for_A_private_item()
    {
        await using var app = await BankingApi.StartAsync(fixture);
        var account = await AccountAsync(app);
        var secret = await MonthlyAsync(app, Visibility.Private);
        await LinkedAsync(app, account, secret, "realestate", "2025-10-05", new LocalDate(2025, 10, 5), -20m, LinkOutcome.PriceDiffers, 12.99m, new LocalDate(2025, 10, 6));
        await LinkedAsync(app, account, secret, "subscriptions", "2026-04-05", D(4, 5), -12.99m, LinkOutcome.AsExpected, 12.99m, D(4, 6));

        Assert.Single(Findings(await SignalsAsync(app, app.AdultA), "banking.amount_differs"));
        Assert.Empty(await SignalsAsync(app, app.AdultB, D(5, 13)));
        Assert.Single(Findings(await SignalsAsync(app, app.AdultA, D(5, 13)), "banking.charge_missing"));
    }

    [Fact]
    public async Task A_charge_not_taken_is_missing_after_its_tolerance()
    {
        await using var app = await BankingApi.StartAsync(fixture);
        var account = await AccountAsync(app);
        var netflix = await MonthlyAsync(app);
        await LinkedAsync(app, account, netflix, "subscriptions", "2026-04-05", D(4, 5), -12.99m, LinkOutcome.AsExpected, 12.99m, D(4, 6));

        Assert.Empty(Findings(await SignalsAsync(app, app.AdultA, D(5, 11)), "banking.charge_missing"));
        var signal = Assert.Single(Findings(await SignalsAsync(app, app.AdultA, D(5, 13)), "banking.charge_missing"));

        Assert.Equal($"banking.charge_missing:{netflix.Value:N}:2026-05-05", signal.Key);
        Assert.Equal([netflix], signal.ItemIds);
        Assert.Equal(D(5, 5), signal.DueOn);
        Assert.Equal((12.99m, "EUR", "subscriptions", 3), (decimal.Parse(signal.Params["amount"], CultureInfo.InvariantCulture), signal.Params["currency"], signal.Params["module"], signal.Params.Count));
    }

    [Fact]
    public async Task An_item_never_linked_has_no_missing_charge()
    {
        await using var app = await BankingApi.StartAsync(fixture);
        await AccountAsync(app);
        await MonthlyAsync(app);

        Assert.Empty(Findings(await SignalsAsync(app, app.AdultA, D(5, 13)), "banking.charge_missing"));
    }

    [Fact]
    public async Task A_charge_before_the_first_link_is_not_missing()
    {
        await using var app = await BankingApi.StartAsync(fixture);
        var account = await AccountAsync(app);
        var netflix = await MonthlyAsync(app);
        await LinkedAsync(app, account, netflix, "subscriptions", "2026-06-05", D(6, 5), -12.99m, LinkOutcome.AsExpected, 12.99m, D(6, 6));

        Assert.Empty(Findings(await SignalsAsync(app, app.AdultA, D(6, 20)), "banking.charge_missing"));
    }

    [Fact]
    public async Task A_late_link_clears_the_finding()
    {
        await using var app = await BankingApi.StartAsync(fixture);
        var account = await AccountAsync(app);
        var netflix = await MonthlyAsync(app);
        await LinkedAsync(app, account, netflix, "subscriptions", "2026-04-05", D(4, 5), -12.99m, LinkOutcome.AsExpected, 12.99m, D(4, 6));
        Assert.Single(Findings(await SignalsAsync(app, app.AdultA, D(5, 13)), "banking.charge_missing"));

        await LinkedAsync(app, account, netflix, "subscriptions", "2026-05-05", D(5, 12), -12.99m, LinkOutcome.AsExpected, 12.99m, D(5, 13));

        Assert.Empty(Findings(await SignalsAsync(app, app.AdultA, D(5, 13)), "banking.charge_missing"));
    }

    /// <summary>A's own account, which only A's connection sees.</summary>
    private static async Task<Guid> PrivateAccountAsync(NidApp app)
    {
        await BankingApi.ConnectAsync(app, app.AdultA, "code-a", BankingApi.Account("uid-p", "hash-p", null, "Perso A"));
        return (await BankingApi.AccountsAsync(app, app.AdultA)).EnumerateArray().Single().GetProperty("id").GetGuid();
    }

    [Fact]
    public async Task A_difference_on_A_private_account_is_no_finding_for_B_even_on_a_shared_item()
    {
        await using var app = await BankingApi.StartAsync(fixture);
        var account = await PrivateAccountAsync(app);
        var loan = new ItemId(await LoanApi.NewAsync(app, LoanApi.Golden(app)));
        await LinkedAsync(app, account, loan, "realestate", "2025-10-15", new LocalDate(2025, 10, 10), -1200.5m, LinkOutcome.PriceDiffers, 1159.92m, new LocalDate(2025, 10, 11));

        Assert.Single(Findings(await SignalsAsync(app, app.AdultA), "banking.amount_differs"));
        Assert.Empty(await SignalsAsync(app, app.AdultB));
    }

    [Fact]
    public async Task A_missing_charge_seen_through_A_private_account_is_no_finding_for_B()
    {
        await using var app = await BankingApi.StartAsync(fixture);
        var account = await PrivateAccountAsync(app);
        var netflix = await MonthlyAsync(app);
        await LinkedAsync(app, account, netflix, "subscriptions", "2026-04-05", D(4, 5), -12.99m, LinkOutcome.AsExpected, 12.99m, D(4, 6));

        Assert.Single(Findings(await SignalsAsync(app, app.AdultA, D(5, 13)), "banking.charge_missing"));
        Assert.Empty(await SignalsAsync(app, app.AdultB, D(5, 13)));
    }

    [Fact]
    public async Task A_shared_subscription_difference_on_A_private_account_is_a_finding_for_A_only()
    {
        await using var app = await BankingApi.StartAsync(fixture);
        var account = await PrivateAccountAsync(app);
        var netflix = await MonthlyAsync(app); // shared: no price change was proposed, B would have read it
        var line = await LinkedAsync(app, account, netflix, "subscriptions", "2025-10-05", new LocalDate(2025, 10, 5), -14.99m, LinkOutcome.PriceDiffers, 12.99m, new LocalDate(2025, 10, 6));

        Assert.Equal($"banking.amount_differs:{line:N}", Assert.Single(Findings(await SignalsAsync(app, app.AdultA), "banking.amount_differs")).Key);
        Assert.Empty(await SignalsAsync(app, app.AdultB));
    }

    [Fact]
    public async Task A_cost_line_does_not_cover_a_missing_rent()
    {
        await using var app = await BankingApi.StartAsync(fixture);
        var account = await AccountAsync(app);
        var property = new ItemId(await PropertyApi.NewPropertyAsync(app, PropertyApi.Home(app)));
        await LoanApi.CreatedAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, $"{PropertyApi.Properties}/{property.Value}/tenancies",
            new { label = "Bail", startsOn = "2026-01-01", rentMonthly = "750", chargesMonthly = "50" }));
        await LinkedAsync(app, account, property, "realestate", "rent:2026-04", D(4, 1), 800m, LinkOutcome.Income, 800m, D(4, 2));
        await LinkedAsync(app, account, property, "realestate", "cost:plumber", D(5, 2), -120m, LinkOutcome.AsExpected, 120m, D(5, 3));

        Assert.Equal($"banking.charge_missing:{property.Value:N}:rent:2026-05",
            Assert.Single(Findings(await SignalsAsync(app, app.AdultA, D(5, 20)), "banking.charge_missing")).Key);
    }

    [Fact]
    public async Task A_rent_not_received_is_a_missing_charge()
    {
        await using var app = await BankingApi.StartAsync(fixture);
        var account = await AccountAsync(app);
        var property = new ItemId(await PropertyApi.NewPropertyAsync(app, PropertyApi.Home(app)));
        await LoanApi.CreatedAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, $"{PropertyApi.Properties}/{property.Value}/tenancies",
            new { label = "Bail", startsOn = "2026-01-01", rentMonthly = "750", chargesMonthly = "50" }));
        await LinkedAsync(app, account, property, "realestate", "rent:2026-04", D(4, 1), 800m, LinkOutcome.Income, 800m, D(4, 2));

        var signal = Assert.Single(Findings(await SignalsAsync(app, app.AdultA, D(5, 20)), "banking.charge_missing"));

        Assert.Equal($"banking.charge_missing:{property.Value:N}:rent:2026-05", signal.Key);
        Assert.Equal("properties", signal.Params["module"]);
    }

    [Fact]
    public async Task A_card_under_its_minimum_usage_is_signalled_before_the_month_ends_to_whoever_sets_it()
    {
        await using var app = await BankingApi.StartAsync(fixture);
        await BankingApi.ConnectAsync(app, app.AdultA, "code-a", BankingApi.Account("uid-1", "hash-1", null, "Carte Visa", "CARD", numberLast4: "4321"));
        var card = Assert.Single((await BankingApi.AccountsAsync(app, app.AdultA)).EnumerateArray()).GetProperty("id").GetGuid();
        var lateOctober = new LocalDate(2025, 10, 28);
        async Task<IEnumerable<Signal>> UnusedAsync(PersonId reader, LocalDate today) => Findings(await SignalsAsync(app, reader, today), "card_unused");
        async Task AddAsync(params NewBankTransaction[] lines)
        {
            await using var scope = app.Services.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
            await scope.ServiceProvider.GetRequiredService<ITransactionStore>().AddAsync(card, lines, default);
        }
        NewBankTransaction Line(string reference, LocalDate on, decimal amount, string label) => new(reference, on, null, amount, Currency.Eur, label, null, null, "api");

        // A rule, the signal on, and the card's own lines already read in September.
        Assert.Equal(System.Net.HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Patch, $"{BankingApi.Root}/accounts/{card}", new { minPayments = 1 })).StatusCode);
        var (_, failure) = await app.InvokeAsync(app.AdultA, new Nid.Platform.Notifications.SaveNotificationPreferencesCommand(
            false, Nid.Platform.Notifications.ActionMode.Immediate, Nid.Platform.Notifications.InfoMode.Digest, Nid.Platform.Notifications.DigestFrequency.Weekly, 1,
            new Dictionary<string, int>(), new Dictionary<string, decimal> { ["card_unused"] = 5m }, 0), TestContext.Current.CancellationToken);
        Assert.Null(failure);
        await AddAsync(Line("s1", new LocalDate(2025, 9, 12), -30m, "CB BOULANGERIE"));

        // No read lately: Nid does not conclude « unused » on stale data.
        Assert.Empty(await UnusedAsync(app.AdultA, lateOctober));
        await using (var scope = app.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
            await scope.ServiceProvider.GetRequiredService<IAccountReadLog>().RecordAsync(
                new BankRead(card, SystemClock.Instance.GetCurrentInstant(), false, false), TestContext.Current.CancellationToken);
        }

        // A refund and a cash withdrawal are no payment; too early in the month, nothing yet.
        await AddAsync(Line("r1", new LocalDate(2025, 10, 3), 5m, "AVOIR CB"), Line("w1", new LocalDate(2025, 10, 4), -20m, "RETRAIT DAB 03/10"));
        Assert.Empty(await UnusedAsync(app.AdultA, new LocalDate(2025, 10, 20)));
        var signal = Assert.Single(await UnusedAsync(app.AdultA, lateOctober));
        Assert.Equal(($"card_unused:{card:D}:2025-10", new LocalDate(2025, 10, 31)), (signal.Key, signal.DueOn));
        Assert.Equal(("Carte Visa", "4321", "0", "1"), (signal.Params["card"], signal.Params["number"], signal.Params["count"], signal.Params["min"]));
        Assert.Empty(signal.ItemIds);
        // B does not set A's card.
        Assert.Empty(await UnusedAsync(app.AdultB, lateOctober));

        // Withdrawals counted, or a payment booked: nothing more.
        Assert.Equal(System.Net.HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Patch, $"{BankingApi.Root}/accounts/{card}", new { countWithdrawals = true })).StatusCode);
        Assert.Empty(await UnusedAsync(app.AdultA, lateOctober));
        Assert.Equal(System.Net.HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Patch, $"{BankingApi.Root}/accounts/{card}", new { countWithdrawals = false })).StatusCode);
        await AddAsync(Line("p1", new LocalDate(2025, 10, 27), -12m, "CB PHARMACIE"));
        Assert.Empty(await UnusedAsync(app.AdultA, lateOctober));
    }

    [Fact]
    public async Task Only_a_card_takes_a_minimum_usage_of_1_to_31_payments()
    {
        await using var app = await BankingApi.StartAsync(fixture);
        var checking = await AccountAsync(app);
        await BankingApi.AssertRefusedAsync(await app.RawAsync(app.AdultA, HttpMethod.Patch, $"{BankingApi.Root}/accounts/{checking}", new { minPayments = 1 }),
            System.Net.HttpStatusCode.UnprocessableEntity, BankingCodes.CardRule);
    }

    [Fact]
    public async Task A_shortfall_ahead_is_raised_once_turned_on_and_a_synced_balance_is_known()
    {
        await using var app = await BankingApi.StartAsync(fixture);
        var account = await AccountAsync(app);
        var netflix = await app.NewSubscriptionAsync("Netflix", app.AdultA, Visibility.Shared);
        (await app.RawAsync(app.AdultA, HttpMethod.Post, $"/api/v1/subscriptions/{netflix.Value}/phases", new
        {
            startsOn = "2025-01-05",
            price = new { amount = "12.99", currency = "EUR" },
            every = new { count = 1, unit = "month" },
            payer = new { type = "household" },
        })).EnsureSuccessStatusCode();
        Assert.Empty(Findings(await SignalsAsync(app, app.AdultA), "shortfall_ahead")); // off by default

        var (_, failure) = await app.InvokeAsync(app.AdultA, new Nid.Platform.Notifications.SaveNotificationPreferencesCommand(
            false, Nid.Platform.Notifications.ActionMode.Immediate, Nid.Platform.Notifications.InfoMode.Digest, Nid.Platform.Notifications.DigestFrequency.Weekly, 1,
            new Dictionary<string, int>(), new Dictionary<string, decimal> { ["shortfall_ahead"] = 0m }, 0), TestContext.Current.CancellationToken);
        Assert.Null(failure);
        Assert.Empty(Findings(await SignalsAsync(app, app.AdultA), "shortfall_ahead")); // no balance yet

        await using (var scope = app.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
            await scope.ServiceProvider.GetRequiredService<IBalanceStore>().UpsertAsync(
                new BankBalance(account, NidApp.GoldenCaseToday.PlusDays(-1), 10m, "CLBD", Now), TestContext.Current.CancellationToken);
        }

        // No income: the 31 days from today hold the 5 November charge, the only checking account's though never matched.
        var signal = Assert.Single(Findings(await SignalsAsync(app, app.AdultA), "shortfall_ahead"));
        Assert.Equal($"shortfall_ahead:{account:D}:2025-11-05", signal.Key);
        Assert.Empty(signal.ItemIds);
        Assert.Equal(new LocalDate(2025, 11, 5), signal.DueOn);
        Assert.Equal((10m, 12.99m, "EUR", "2025-11-05", "Netflix"), (decimal.Parse(signal.Params["balance_amount"], CultureInfo.InvariantCulture),
            decimal.Parse(signal.Params["charges_amount"], CultureInfo.InvariantCulture), signal.Params["currency"], signal.Params["short_on"], signal.Params["charges"]));
        Assert.Empty(Findings(await SignalsAsync(app, app.AdultB), "shortfall_ahead")); // A's own account
    }

    [Fact]
    public async Task Idle_cash_is_raised_once_a_month_once_turned_on_and_an_open_goal_is_behind()
    {
        await using var app = await BankingApi.StartAsync(fixture);
        var account = await AccountAsync(app);
        var netflix = await app.NewSubscriptionAsync("Netflix", app.AdultA, Visibility.Shared);
        (await app.RawAsync(app.AdultA, HttpMethod.Post, $"/api/v1/subscriptions/{netflix.Value}/phases", new
        {
            startsOn = "2025-01-05",
            price = new { amount = "12.99", currency = "EUR" },
            every = new { count = 1, unit = "month" },
            payer = new { type = "household" },
        })).EnsureSuccessStatusCode();
        await using (var scope = app.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
            // Before the 28 days, held by every one of them.
            await scope.ServiceProvider.GetRequiredService<IBalanceStore>().UpsertAsync(
                new BankBalance(account, NidApp.GoldenCaseToday.PlusDays(-40), 1_000m, "CLBD", Now), TestContext.Current.CancellationToken);
        }
        Assert.Empty(Findings(await SignalsAsync(app, app.AdultA), "idle_cash")); // off by default

        var (_, failure) = await app.InvokeAsync(app.AdultA, new Nid.Platform.Notifications.SaveNotificationPreferencesCommand(
            false, Nid.Platform.Notifications.ActionMode.Immediate, Nid.Platform.Notifications.InfoMode.Digest, Nid.Platform.Notifications.DigestFrequency.Weekly, 1,
            new Dictionary<string, int>(), new Dictionary<string, decimal> { ["idle_cash"] = 3m }, 0), TestContext.Current.CancellationToken);
        Assert.Null(failure);
        Assert.Empty(Findings(await SignalsAsync(app, app.AdultA), "idle_cash")); // above the buffer, but no livret with room and no goal behind

        // No progress towards a dated target: behind.
        (await app.RawAsync(app.AdultA, HttpMethod.Post, "/api/v1/goals", new
        {
            name = "Voyage", kind = "savings", targetAmount = "10000", targetDate = "2026-10-15", visibility = "shared",
            owners = new[] { new { personId = app.AdultA.Value, shareBp = 10_000 } }, funding = Array.Empty<object>(),
        })).EnsureSuccessStatusCode();

        var signal = Assert.Single(Findings(await SignalsAsync(app, app.AdultA), "idle_cash"));
        Assert.Equal("idle_cash:2025-10", signal.Key);
        Assert.Empty(signal.ItemIds);
        Assert.Null(signal.DueOn);
        // The buffer is 3 months of the 12,99 subscription, amortized as the emergency fund's target (39,14).
        Assert.Equal((960.86m, 39.14m, "EUR", "goal_behind"), (decimal.Parse(signal.Params["above_amount"], CultureInfo.InvariantCulture),
            decimal.Parse(signal.Params["buffer_amount"], CultureInfo.InvariantCulture), signal.Params["currency"], signal.Params["reason"]));
        Assert.False(signal.Params.ContainsKey("room_amount"));
        Assert.Empty(Findings(await SignalsAsync(app, app.AdultB), "idle_cash")); // A's own account
    }
}
