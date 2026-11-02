using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Nid.Banking.Application;
using Nid.Banking.Contracts;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.TestSupport;
using NodaTime;
using NodaTime.Text;

namespace Nid.Banking.IntegrationTests;

/// <summary>5C spec §8, D19: the routes that list, link, unlink and ignore lines, the candidates, the expected charges and the rules.</summary>
public class LinkRouteTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static readonly LocalDate Today = new(2026, 10, 29);

    private sealed class FixedClock : IClock
    {
        public Instant GetCurrentInstant() => Today.At(new LocalTime(10, 0)).InUtc().ToInstant();
    }

    private sealed record Setup(NidApp App, Guid Own, Guid Joint, ItemId Envelope) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => App.DisposeAsync();
    }

    /// <summary>A's own « Courant A » (B cannot read it) and « Joint », on an envelope A and B own and write.</summary>
    private static async Task<Setup> StartAsync(PostgresFixture fixture)
    {
        var app = await BankingApi.StartAsync(fixture, overrides: s => s.AddSingleton<IClock>(new FixedClock()));
        await BankingApi.ConnectAsync(app, app.AdultA, "code-a",
            BankingApi.Account("uid-1", "hash-1", null, "Courant A"), BankingApi.Account("uid-2", "hash-2", null, "Joint"));
        var accounts = (await BankingApi.AccountsAsync(app, app.AdultA)).EnumerateArray()
            .ToDictionary(a => a.GetProperty("name").GetString()!, a => a.GetProperty("id").GetGuid());
        var envelope = await app.NewEnvelopeAsync("Commun", Visibility.Shared, (app.AdultA, 5000), (app.AdultB, 5000));
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Patch, $"{BankingApi.Root}/accounts/{accounts["Joint"]}",
            new { envelopeId = envelope.Value })).StatusCode);
        return new Setup(app, accounts["Courant A"], accounts["Joint"], envelope);
    }

    private static async Task<Guid> AddAsync(NidApp app, Guid account, string reference, LocalDate on, decimal amount, string label = "PRLV SEPA NETFLIX 123")
    {
        await using var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
        var store = scope.ServiceProvider.GetRequiredService<ITransactionStore>();
        await store.AddAsync(account, [new NewBankTransaction(reference, on, null, amount, Currency.Eur, label, null, null, "api")], default);
        return (await store.ListAsync([account], null, null, 0, 100, default)).Single(t => t.ProviderRef == reference).Id;
    }

    /// <summary>A subscription of <paramref name="owner"/>'s charged <paramref name="price"/> every <paramref name="unit"/> from <paramref name="startsOn"/>.</summary>
    private static async Task<ItemId> SubscriptionAsync(
        NidApp app, string name, PersonId owner, Visibility visibility, string price = "12.99", string startsOn = "2026-01-05", string unit = "month")
    {
        var id = await app.NewSubscriptionAsync(name, owner, visibility);
        (await app.RawAsync(owner, HttpMethod.Post, $"/api/v1/subscriptions/{id.Value}/phases", new
        {
            startsOn,
            price = new { amount = price, currency = "EUR" },
            every = new { count = 1, unit },
            payer = new { type = "household" },
        })).EnsureSuccessStatusCode();
        return id;
    }

    private static Task<HttpResponseMessage> PostAsync(NidApp app, PersonId person, Guid line, string action, object? body = null)
        => app.RawAsync(person, HttpMethod.Post, $"{BankingApi.Root}/transactions/{line}/{action}", body ?? new { });

    private static async Task<JsonElement> LinkAsync(NidApp app, PersonId person, Guid line, ItemId item)
    {
        var response = await PostAsync(app, person, line, "link", new { module = "subscriptions", itemId = item.Value });
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await BankingApi.JsonAsync(response);
    }

    /// <summary>A link set as the job or an older version would, through the store.</summary>
    private static async Task SetLinkAsync(NidApp app, BankLink link, TransactionLinkState state)
    {
        await using var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
        await scope.ServiceProvider.GetRequiredService<ILinkStore>().SetAsync(link, state, default);
    }

    private static async Task<(string Body, JsonElement[] Items)> LinesAsync(NidApp app, PersonId person, string query = "")
    {
        var response = await app.RawAsync(person, HttpMethod.Get, $"{BankingApi.Root}/transactions{query}");
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, body);
        return (body, [.. JsonDocument.Parse(body).RootElement.GetProperty("items").EnumerateArray()]);
    }

    private static async Task<JsonElement> LineAsync(NidApp app, PersonId person, Guid line)
        => (await LinesAsync(app, person, "?state=all")).Items.Single(i => i.GetProperty("id").GetGuid() == line);

    private static string State(JsonElement line) => line.GetProperty("linkState").GetString()!;

    [Fact]
    public async Task A_private_link_is_only_linked_for_the_other_adult()
    {
        await using var s = await StartAsync(fixture);
        var app = s.App;
        var secret = await SubscriptionAsync(app, "Secret plan", app.AdultA, Visibility.Private);
        var line = await AddAsync(app, s.Joint, "a", new LocalDate(2026, 10, 5), -12.99m);

        var linked = await LinkAsync(app, app.AdultA, line, secret);

        Assert.Equal(("linked", "Secret plan"), (State(linked), linked.GetProperty("link").GetProperty("itemName").GetString()));
        var ofA = (await LineAsync(app, app.AdultA, line)).GetProperty("link");
        Assert.Equal(("as_expected", "subscriptions", secret.Value, "2026-10-05", "12.99", "EUR"), (ofA.GetProperty("outcome").GetString(),
            ofA.GetProperty("module").GetString(), ofA.GetProperty("itemId").GetGuid(), ofA.GetProperty("key").GetString(),
            ofA.GetProperty("expectedAmount").GetString(), ofA.GetProperty("expectedCurrency").GetString()));

        var (body, items) = await LinesAsync(app, app.AdultB, "?state=all");
        var ofB = Assert.Single(items);
        Assert.Equal(("linked", JsonValueKind.Null, "Joint"), (State(ofB), ofB.GetProperty("link").ValueKind, ofB.GetProperty("accountName").GetString()));
        Assert.DoesNotContain("Secret plan", body, StringComparison.Ordinal);
        Assert.DoesNotContain(secret.Value.ToString(), body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task B_cannot_unlink_A_private_link()
    {
        await using var s = await StartAsync(fixture);
        var app = s.App;
        var secret = await SubscriptionAsync(app, "Secret plan", app.AdultA, Visibility.Private);
        var line = await AddAsync(app, s.Joint, "a", new LocalDate(2026, 10, 5), -12.99m);
        await LinkAsync(app, app.AdultA, line, secret);

        var refused = await PostAsync(app, app.AdultB, line, "unlink");

        Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);
        Assert.DoesNotContain(secret.Value.ToString(), await refused.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        Assert.Equal("linked", State(await LineAsync(app, app.AdultA, line)));
    }

    [Fact]
    public async Task Ignoring_a_linked_line_is_refused()
    {
        await using var s = await StartAsync(fixture);
        var app = s.App;
        var netflix = await SubscriptionAsync(app, "Netflix", app.AdultA, Visibility.Shared);
        var line = await AddAsync(app, s.Joint, "a", new LocalDate(2026, 10, 5), -12.99m);
        await LinkAsync(app, app.AdultA, line, netflix);

        await BankingApi.AssertRefusedAsync(await PostAsync(app, app.AdultA, line, "ignore"), HttpStatusCode.Conflict, BankingCodes.TransactionLinked);
        await BankingApi.AssertRefusedAsync(await PostAsync(app, app.AdultA, line, "link", new { module = "subscriptions", itemId = netflix.Value }),
            HttpStatusCode.Conflict, BankingCodes.TransactionLinked);
    }

    [Fact]
    public async Task Unlink_then_ignore()
    {
        await using var s = await StartAsync(fixture);
        var app = s.App;
        var netflix = await SubscriptionAsync(app, "Netflix", app.AdultA, Visibility.Shared);
        var line = await AddAsync(app, s.Joint, "a", new LocalDate(2026, 10, 5), -12.99m);
        await LinkAsync(app, app.AdultA, line, netflix);

        var unlinked = await BankingApi.JsonAsync(await PostAsync(app, app.AdultA, line, "unlink"));
        Assert.Equal(("unlinked", JsonValueKind.Null), (State(unlinked), unlinked.GetProperty("link").ValueKind));
        Assert.Equal("ignored", State(await BankingApi.JsonAsync(await PostAsync(app, app.AdultB, line, "ignore"))));
        Assert.Equal("ignored", State(await LineAsync(app, app.AdultA, line)));
        // Unlinking an ignored line takes it back.
        Assert.Equal("unlinked", State(await BankingApi.JsonAsync(await PostAsync(app, app.AdultA, line, "unlink"))));
    }

    [Fact]
    public async Task Unlinked_lines_of_every_readable_account()
    {
        await using var s = await StartAsync(fixture);
        var app = s.App;
        var netflix = await SubscriptionAsync(app, "Netflix", app.AdultA, Visibility.Shared);
        await AddAsync(app, s.Own, "own", new LocalDate(2026, 10, 3), -5m, "CB BOULANGERIE");
        await AddAsync(app, s.Joint, "joint", new LocalDate(2026, 10, 4), -40m, "CB SUPERMARCHE");
        var linked = await AddAsync(app, s.Joint, "linked", new LocalDate(2026, 10, 5), -12.99m);
        await LinkAsync(app, app.AdultA, linked, netflix);

        var (_, ofA) = await LinesAsync(app, app.AdultA);
        Assert.Equal([("CB SUPERMARCHE", "Joint"), ("CB BOULANGERIE", "Courant A")],
            ofA.Select(i => (i.GetProperty("label").GetString(), i.GetProperty("accountName").GetString())));
        var (body, ofB) = await LinesAsync(app, app.AdultB);
        Assert.Equal(["CB SUPERMARCHE"], ofB.Select(i => i.GetProperty("label").GetString()));
        Assert.DoesNotContain("BOULANGERIE", body, StringComparison.Ordinal);
        Assert.Equal([linked], (await LinesAsync(app, app.AdultB, "?state=linked")).Items.Select(i => i.GetProperty("id").GetGuid()));
        Assert.Equal(2, (await LinesAsync(app, app.AdultB, "?state=all")).Items.Length);
        await BankingApi.AssertRefusedAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, $"{BankingApi.Root}/transactions?state=done"),
            HttpStatusCode.UnprocessableEntity, FieldErrors.ShapeCode);
    }

    [Fact]
    public async Task Candidates_are_writable_charges_nearest_amount_first()
    {
        await using var s = await StartAsync(fixture);
        var app = s.App;
        var line = await AddAsync(app, s.Joint, "a", new LocalDate(2026, 10, 5), -12.99m);
        await SubscriptionAsync(app, "Netflix", app.AdultA, Visibility.Shared);
        await SubscriptionAsync(app, "Spotify", app.AdultA, Visibility.Shared, "10.99", "2026-01-10");
        // Every Monday: nine charges within ±30 days, so the list is cut.
        await SubscriptionAsync(app, "Weekly", app.AdultA, Visibility.Shared, "3.00", "2026-01-05", "week");
        await SubscriptionAsync(app, "Weekly two", app.AdultA, Visibility.Shared, "4.00", "2026-01-06", "week");
        await SubscriptionAsync(app, "Weekly three", app.AdultA, Visibility.Shared, "5.00", "2026-01-07", "week");
        // B's: A reads it, but does not write it.
        await SubscriptionAsync(app, "Theirs", app.AdultB, Visibility.Shared);

        var response = await app.RawAsync(app.AdultA, HttpMethod.Get, $"{BankingApi.Root}/transactions/{line}/candidates");
        var candidates = (await BankingApi.JsonAsync(response)).EnumerateArray().ToList();

        Assert.Equal(20, candidates.Count);
        Assert.Equal(("Netflix", "2026-10-05", "0.00", "subscription"), (candidates[0].GetProperty("itemName").GetString(),
            candidates[0].GetProperty("dueOn").GetString(), candidates[0].GetProperty("gap").GetString(), candidates[0].GetProperty("kind").GetString()));
        // The same gap: the nearest date first.
        Assert.Equal(("Netflix", "2026-09-05"), (candidates[1].GetProperty("itemName").GetString(), candidates[1].GetProperty("dueOn").GetString()));
        Assert.Equal(("Spotify", "2026-10-10", "2.00"), (candidates[2].GetProperty("itemName").GetString(), candidates[2].GetProperty("dueOn").GetString(),
            candidates[2].GetProperty("gap").GetString()));
        Assert.DoesNotContain(candidates, c => c.GetProperty("itemName").GetString() == "Theirs");
        Assert.All(candidates, c => Assert.InRange(LocalDatePattern.Iso.Parse(c.GetProperty("dueOn").GetString()!).Value,
            new LocalDate(2026, 9, 5), new LocalDate(2026, 11, 4)));
        var gaps = candidates.Select(c => Math.Abs(decimal.Parse(c.GetProperty("gap").GetString()!, System.Globalization.CultureInfo.InvariantCulture))).ToList();
        Assert.Equal(gaps.Order(), gaps);

        Assert.Equal(HttpStatusCode.NotFound, (await app.RawAsync(app.AdultB, HttpMethod.Get,
            $"{BankingApi.Root}/transactions/{await AddAsync(app, s.Own, "own", new LocalDate(2026, 10, 5), -12.99m)}/candidates")).StatusCode);
    }

    [Fact]
    public async Task Expected_charges_window_is_capped()
    {
        await using var s = await StartAsync(fixture);
        var app = s.App;
        await SubscriptionAsync(app, "Netflix", app.AdultA, Visibility.Shared);
        await SubscriptionAsync(app, "Secret plan", app.AdultA, Visibility.Private);

        await BankingApi.AssertRefusedAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, $"{BankingApi.Root}/expected-charges?from=2026-10-01&to=2027-01-02"),
            HttpStatusCode.UnprocessableEntity, FieldErrors.ShapeCode);
        await BankingApi.AssertRefusedAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, $"{BankingApi.Root}/expected-charges?from=2026-10-02&to=2026-10-01"),
            HttpStatusCode.UnprocessableEntity, FieldErrors.ShapeCode);
        var ofB = await BankingApi.JsonAsync(await app.RawAsync(app.AdultB, HttpMethod.Get, $"{BankingApi.Root}/expected-charges?from=2026-10-01&to=2027-01-01"));
        Assert.Equal(["2026-10-05", "2026-11-05", "2026-12-05"], ofB.EnumerateArray().Select(c => c.GetProperty("dueOn").GetString()));
        Assert.All(ofB.EnumerateArray(), c => Assert.Equal(("Netflix", JsonValueKind.Null), (c.GetProperty("itemName").GetString(), c.GetProperty("gap").ValueKind)));
    }

    [Fact]
    public async Task Rules_are_their_creator_only()
    {
        await using var s = await StartAsync(fixture);
        var app = s.App;
        var netflix = await SubscriptionAsync(app, "Netflix", app.AdultA, Visibility.Shared);
        var line = await AddAsync(app, s.Joint, "a", new LocalDate(2026, 10, 5), -12.99m);
        await LinkAsync(app, app.AdultA, line, netflix);

        var rule = Assert.Single((await BankingApi.JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, $"{BankingApi.Root}/rules"))).EnumerateArray());
        Assert.Equal(("NETFLIX", "Joint", "Netflix", netflix.Value), (rule.GetProperty("labelStem").GetString(), rule.GetProperty("accountName").GetString(),
            rule.GetProperty("itemName").GetString(), rule.GetProperty("itemId").GetGuid()));
        Assert.Empty((await BankingApi.JsonAsync(await app.RawAsync(app.AdultB, HttpMethod.Get, $"{BankingApi.Root}/rules"))).EnumerateArray());

        var id = rule.GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.NotFound, (await app.RawAsync(app.AdultB, HttpMethod.Delete, $"{BankingApi.Root}/rules/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await app.RawAsync(app.AdultA, HttpMethod.Delete, $"{BankingApi.Root}/rules/{id}")).StatusCode);
        Assert.Empty((await BankingApi.JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, $"{BankingApi.Root}/rules"))).EnumerateArray());
    }

    [Fact]
    public async Task An_internal_link_to_an_account_B_cannot_read_is_only_linked_for_B()
    {
        await using var s = await StartAsync(fixture);
        var app = s.App;
        var line = await AddAsync(app, s.Joint, "a", new LocalDate(2026, 10, 5), -100m, "VIR SEPA EPARGNE");
        await SetLinkAsync(app, new BankLink(line, null, null, null, s.Own, LinkOutcome.InternalTransfer, null, "job",
            Today.AtMidnight().InUtc().ToInstant()), TransactionLinkState.Internal);
        Guid[] Ids(JsonElement[] items) => [.. items.Select(i => i.GetProperty("id").GetGuid())];

        var ofA = await LineAsync(app, app.AdultA, line);
        Assert.Equal(("internal", "internal_transfer", "Courant A"), (State(ofA), ofA.GetProperty("link").GetProperty("outcome").GetString(),
            ofA.GetProperty("link").GetProperty("counterpartName").GetString()));
        Assert.Equal([line], Ids((await LinesAsync(app, app.AdultA, "?state=internal")).Items));

        var (body, all) = await LinesAsync(app, app.AdultB, "?state=all");
        var ofB = Assert.Single(all);
        Assert.Equal(("linked", JsonValueKind.Null), (State(ofB), ofB.GetProperty("link").ValueKind));
        Assert.DoesNotContain("Courant A", body, StringComparison.Ordinal);
        Assert.DoesNotContain("internal", body, StringComparison.Ordinal);
        Assert.Empty((await LinesAsync(app, app.AdultB, "?state=internal")).Items);
        Assert.Equal([line], Ids((await LinesAsync(app, app.AdultB, "?state=linked")).Items));
        // The envelope's lines say the same.
        async Task<JsonElement[]> EnvelopeAsync(string state) => [.. (await BankingApi.JsonAsync(await app.RawAsync(app.AdultB, HttpMethod.Get,
            $"{BankingApi.Root}/envelopes/{s.Envelope.Value}/transactions?state={state}"))).GetProperty("items").EnumerateArray()];
        Assert.Empty(await EnvelopeAsync("internal"));
        Assert.Equal(("linked", JsonValueKind.Null), (State(Assert.Single(await EnvelopeAsync("linked"))),
            (await EnvelopeAsync("linked"))[0].GetProperty("link").ValueKind));

        Assert.Equal(HttpStatusCode.NotFound, (await PostAsync(app, app.AdultB, line, "unlink")).StatusCode);
        Assert.Equal("internal", State(await LineAsync(app, app.AdultA, line)));
    }

    [Fact]
    public async Task A_link_to_a_deleted_item_is_unlinked_with_write_on_the_account()
    {
        await using var s = await StartAsync(fixture);
        var app = s.App;
        var line = await AddAsync(app, s.Joint, "a", new LocalDate(2026, 10, 5), -12.99m);
        await SetLinkAsync(app, new BankLink(line, "subscriptions", new ItemId(Guid.NewGuid()), "2026-10-05", null, LinkOutcome.AsExpected, null,
            $"person:{app.AdultA.Value:D}", Today.AtMidnight().InUtc().ToInstant()), TransactionLinkState.Linked);

        var unlinked = await PostAsync(app, app.AdultB, line, "unlink");

        Assert.True(unlinked.StatusCode == HttpStatusCode.OK, await unlinked.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("unlinked", State(await BankingApi.JsonAsync(unlinked)));
    }
}
