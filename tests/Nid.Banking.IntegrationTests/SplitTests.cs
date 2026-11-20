using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nid.Banking.Infrastructure.Persistence;
using Nid.Banking.Application;
using Nid.Banking.Contracts;
using Nid.Banking.Domain;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.TestSupport;
using NodaTime;

namespace Nid.Banking.IntegrationTests;

/// <summary>#84: a note on a bank line, and a split into parts the category passes leave alone.</summary>
public class SplitTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static readonly Instant At = new LocalDate(2026, 10, 29).At(new LocalTime(10, 0)).InUtc().ToInstant();

    private sealed class FixedClock : IClock
    {
        public Instant GetCurrentInstant() => At;
    }

    private static Guid Seed(string key) => CategorySeed.Find(key)!.Id;

    private sealed record Setup(NidApp App, Guid Courant, Guid Livret) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => App.DisposeAsync();
    }

    /// <summary>A's « Courant A », on an envelope of A's that B reads but does not write, and A's « Livret A », which B cannot read.</summary>
    private static async Task<Setup> StartAsync(PostgresFixture fixture)
    {
        var app = await BankingApi.StartAsync(fixture, overrides: s => s.AddSingleton<IClock>(new FixedClock()));
        await BankingApi.ConnectAsync(app, app.AdultA, "code-a",
            BankingApi.Account("uid-1", "hash-1", null, "Courant A"), BankingApi.Account("uid-2", "hash-2", null, "Livret A", "SVGS"));
        var accounts = (await BankingApi.AccountsAsync(app, app.AdultA)).EnumerateArray()
            .ToDictionary(a => a.GetProperty("name").GetString()!, a => a.GetProperty("id").GetGuid());
        var envelope = await app.NewEnvelopeAsync("Courant", app.AdultA, Visibility.Shared);
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Patch, $"{BankingApi.Root}/accounts/{accounts["Courant A"]}",
            new { envelopeId = envelope.Value })).StatusCode);
        return new Setup(app, accounts["Courant A"], accounts["Livret A"]);
    }

    private static async Task<Guid> AddAsync(NidApp app, Guid account, string reference, decimal amount, string label = "CARREFOUR CITY 12", LocalDate? on = null)
    {
        await using var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
        var store = scope.ServiceProvider.GetRequiredService<ITransactionStore>();
        await store.AddAsync(account, [new NewBankTransaction(reference, on ?? new LocalDate(2026, 10, 5), null, amount, Currency.Eur, label, null, null, "api")], default);
        return (await store.ListAsync([account], null, null, 0, 100, default)).Single(t => t.ProviderRef == reference).Id;
    }

    private static string Url(Guid line, string tail) => $"{BankingApi.Root}/transactions/{line}/{tail}";

    private static object Part(decimal amount, string? key = null, string? note = null) => new { amount = amount.ToString(System.Globalization.CultureInfo.InvariantCulture), categoryId = key is null ? (Guid?)null : Seed(key), note };

    private static Task<HttpResponseMessage> SplitAsync(NidApp app, PersonId person, Guid line, params object[] parts)
        => app.RawAsync(person, HttpMethod.Put, Url(line, "split"), new { parts });

    private static async Task<JsonElement> OkAsync(HttpResponseMessage response, HttpStatusCode status = HttpStatusCode.OK)
    {
        Assert.True(response.StatusCode == status, await response.Content.ReadAsStringAsync());
        return await BankingApi.JsonAsync(response);
    }

    private static async Task<JsonElement> LineAsync(NidApp app, PersonId person, Guid line)
        => JsonDocument.Parse(await app.GetRawAsync(person, $"{BankingApi.Root}/transactions?state=all")).RootElement.GetProperty("items")
            .EnumerateArray().Single(i => i.GetProperty("id").GetGuid() == line);

    private static (string? Source, bool HasCategory, int Parts) Of(JsonElement line) => (line.GetProperty("categorySource").GetString(),
        line.GetProperty("category").ValueKind != JsonValueKind.Null, line.GetProperty("parts").ValueKind == JsonValueKind.Null ? 0 : line.GetProperty("parts").GetArrayLength());

    [Fact]
    public async Task A_split_shows_its_parts_in_order_and_a_resplit_replaces_them()
    {
        await using var s = await StartAsync(fixture);
        var app = s.App;
        var line = await AddAsync(app, s.Courant, "a", -100m);

        var split = await OkAsync(await SplitAsync(app, app.AdultA, line, Part(-60m, "food.groceries", "pain"), Part(-40m, null, null)));

        Assert.Equal(("split", false, 2), Of(split));
        var parts = split.GetProperty("parts").EnumerateArray().ToList();
        Assert.Equal(("-60.00", "Courses", "pain", "-40.00"), (parts[0].GetProperty("amount").GetString(), parts[0].GetProperty("category").GetProperty("name").GetString(),
            parts[0].GetProperty("note").GetString(), parts[1].GetProperty("amount").GetString()));
        Assert.Equal(JsonValueKind.Null, parts[1].GetProperty("category").ValueKind);
        // Another adult who reads the account reads the parts; the list shows them too.
        Assert.Equal(("split", false, 2), Of(await LineAsync(app, app.AdultB, line)));

        var again = await OkAsync(await SplitAsync(app, app.AdultA, line, Part(-10m, "food.restaurants"), Part(-20m), Part(-70m, "home.upkeep")));
        Assert.Equal(("split", false, 3), Of(again));
        Assert.Equal(3, Of(await LineAsync(app, app.AdultA, line)).Parts);
    }

    [Fact]
    public async Task A_split_that_does_not_add_up_says_by_how_much()
    {
        await using var s = await StartAsync(fixture);
        var app = s.App;
        var line = await AddAsync(app, s.Courant, "a", -100m);

        var response = await SplitAsync(app, app.AdultA, line, Part(-60m), Part(-39.99m));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await BankingApi.JsonAsync(response);
        Assert.Equal("-99.99", body.GetProperty("partsSum").GetString());
        Assert.Equal("-100.00", body.GetProperty("lineAmount").GetString());
        Assert.Equal(BankingCodes.SplitSum, body.GetProperty("code").GetString());
    }

    [Fact]
    public async Task A_split_that_does_not_add_up_or_has_bad_parts_is_refused()
    {
        await using var s = await StartAsync(fixture);
        var app = s.App;
        var line = await AddAsync(app, s.Courant, "a", -100m);

        await BankingApi.AssertRefusedAsync(await SplitAsync(app, app.AdultA, line, Part(-60m), Part(-39.99m)), HttpStatusCode.UnprocessableEntity, BankingCodes.SplitSum);
        await BankingApi.AssertRefusedAsync(await SplitAsync(app, app.AdultA, line, Part(-100m), Part(0m)), HttpStatusCode.UnprocessableEntity, BankingCodes.SplitPart);
        await BankingApi.AssertRefusedAsync(await SplitAsync(app, app.AdultA, line, Part(-100m)), HttpStatusCode.UnprocessableEntity, BankingCodes.SplitPart);
        await BankingApi.AssertRefusedAsync(await SplitAsync(app, app.AdultA, line, Part(-60m, note: new string('x', 501)), Part(-40m)),
            HttpStatusCode.UnprocessableEntity, BankingCodes.SplitPart);
        Assert.Equal(HttpStatusCode.NotFound, (await SplitAsync(app, app.AdultA, line, Part(-60m), new { amount = "-40", categoryId = Guid.NewGuid() })).StatusCode);
        Assert.Equal((null, false, 0), Of(await LineAsync(app, app.AdultA, line)));
    }

    [Fact]
    public async Task A_part_takes_only_a_category_in_use()
    {
        await using var s = await StartAsync(fixture);
        var app = s.App;
        var line = await AddAsync(app, s.Courant, "a", -100m);
        await OkAsync(await app.RawAsync(app.AdultA, HttpMethod.Patch, $"{BankingApi.Root}/categories/{Seed("food.delivery")}", new { archived = true }));

        await BankingApi.AssertRefusedAsync(await SplitAsync(app, app.AdultA, line, Part(-60m, "food.delivery"), Part(-40m)),
            HttpStatusCode.UnprocessableEntity, BankingCodes.CategoryArchived);
    }

    [Fact]
    public async Task A_reader_cannot_split_or_note()
    {
        await using var s = await StartAsync(fixture);
        var app = s.App;
        var readable = await AddAsync(app, s.Courant, "a", -100m);
        var hidden = await AddAsync(app, s.Livret, "b", -100m);

        Assert.Equal(HttpStatusCode.Forbidden, (await SplitAsync(app, app.AdultB, readable, Part(-60m), Part(-40m))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await app.RawAsync(app.AdultB, HttpMethod.Delete, Url(readable, "split"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await app.RawAsync(app.AdultB, HttpMethod.Put, Url(readable, "note"), new { note = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SplitAsync(app, app.AdultB, hidden, Part(-60m), Part(-40m))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await app.RawAsync(app.AdultB, HttpMethod.Put, Url(hidden, "note"), new { note = "x" })).StatusCode);
        Assert.Equal((null, false, 0), Of(await LineAsync(app, app.AdultA, readable)));
    }

    [Fact]
    public async Task A_rule_and_a_link_leave_a_split_line_alone_and_a_hand_category_is_refused()
    {
        await using var s = await StartAsync(fixture);
        var app = s.App;
        var line = await AddAsync(app, s.Courant, "a", -100m);
        await OkAsync(await SplitAsync(app, app.AdultA, line, Part(-60m, "food.groceries"), Part(-40m)));
        var rule = (await OkAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, $"{BankingApi.Root}/category-rules",
            new { pattern = "CARREFOUR", categoryId = Seed("food.restaurants") }), HttpStatusCode.Created)).GetProperty("id").GetGuid();

        var applied = await OkAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, $"{BankingApi.Root}/category-rules/{rule}/apply"));

        Assert.Equal(0, applied.GetProperty("changed").GetInt32());
        await using (var scope = app.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
            await scope.ServiceProvider.GetRequiredService<ILinkStore>().SetAsync(new BankLink(line, null, null, null, s.Livret,
                LinkOutcome.InternalTransfer, null, "job", At), TransactionLinkState.Internal, TestContext.Current.CancellationToken, "internal.transfer");
        }
        Assert.Equal(("split", false, 2), Of(await LineAsync(app, app.AdultA, line)));
        await BankingApi.AssertRefusedAsync(await app.RawAsync(app.AdultA, HttpMethod.Put, Url(line, "category"), new { categoryId = Seed("food.groceries") }),
            HttpStatusCode.Conflict, BankingCodes.LineSplit);
    }

    [Fact]
    public async Task Unsplitting_deletes_the_parts_and_lets_a_matching_rule_categorise_again()
    {
        await using var s = await StartAsync(fixture);
        var app = s.App;
        var line = await AddAsync(app, s.Courant, "a", -100m);
        var plain = await AddAsync(app, s.Courant, "b", -10m, "BOULANGERIE");
        await OkAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, $"{BankingApi.Root}/category-rules",
            new { pattern = "CARREFOUR", categoryId = Seed("food.groceries") }), HttpStatusCode.Created);
        await OkAsync(await SplitAsync(app, app.AdultA, line, Part(-60m, "food.restaurants"), Part(-40m)));
        await OkAsync(await SplitAsync(app, app.AdultA, plain, Part(-6m), Part(-4m)));

        var back = await OkAsync(await app.RawAsync(app.AdultA, HttpMethod.Delete, Url(line, "split")));

        Assert.Equal(("rule", true, 0), Of(back));
        Assert.Equal("Courses", back.GetProperty("category").GetProperty("name").GetString());
        Assert.Equal((null, false, 0), Of(await OkAsync(await app.RawAsync(app.AdultA, HttpMethod.Delete, Url(plain, "split")))));
    }

    [Fact]
    public async Task A_note_is_set_trimmed_cleared_and_bounded()
    {
        await using var s = await StartAsync(fixture);
        var app = s.App;
        var line = await AddAsync(app, s.Courant, "a", -100m);

        var set = await OkAsync(await app.RawAsync(app.AdultA, HttpMethod.Put, Url(line, "note"), new { note = "  repas de famille  " }));

        Assert.Equal("repas de famille", set.GetProperty("note").GetString());
        Assert.Equal("repas de famille", (await LineAsync(app, app.AdultB, line)).GetProperty("note").GetString());
        Assert.Equal(JsonValueKind.Null, (await OkAsync(await app.RawAsync(app.AdultA, HttpMethod.Put, Url(line, "note"), new { note = "" }))).GetProperty("note").ValueKind);
        await OkAsync(await app.RawAsync(app.AdultA, HttpMethod.Put, Url(line, "note"), new { note = new string('x', 500) }));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await app.RawAsync(app.AdultA, HttpMethod.Put, Url(line, "note"), new { note = new string('x', 501) })).StatusCode);
    }

    [Fact]
    public async Task Retention_keeps_a_noted_and_a_split_line_and_purges_a_plain_one()
    {
        await using var s = await StartAsync(fixture);
        var app = s.App;
        var old = new LocalDate(2020, 1, 5);
        var noted = await AddAsync(app, s.Courant, "n", -10m, "LIGNE N", old);
        var split = await AddAsync(app, s.Courant, "s", -10m, "LIGNE S", old);
        var plain = await AddAsync(app, s.Courant, "p", -10m, "LIGNE P", old);
        await OkAsync(await app.RawAsync(app.AdultA, HttpMethod.Put, Url(noted, "note"), new { note = "garder" }));
        await OkAsync(await SplitAsync(app, app.AdultA, split, Part(-6m), Part(-4m)));

        await using var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
        var store = scope.ServiceProvider.GetRequiredService<ITransactionStore>();
        Assert.Equal(1, await store.PurgeBookedBeforeAsync(new LocalDate(2025, 1, 1), TestContext.Current.CancellationToken));

        Assert.Equal(new[] { noted, split }.Order(), (await store.ListAsync([s.Courant], null, null, 0, 10, TestContext.Current.CancellationToken)).Select(t => t.Id).Order());
        Assert.DoesNotContain(plain, (await store.ListAsync([s.Courant], null, null, 0, 10, TestContext.Current.CancellationToken)).Select(t => t.Id));
    }

    [Fact]
    public async Task A_null_part_a_missing_or_malformed_amount_and_an_unknown_key_are_refused()
    {
        await using var s = await StartAsync(fixture);
        var app = s.App;
        var line = await AddAsync(app, s.Courant, "a", -100m);
        Task<HttpResponseMessage> Put(object body) => app.RawAsync(app.AdultA, HttpMethod.Put, Url(line, "split"), body);

        await BankingApi.AssertRefusedAsync(await Put(new { parts = new object?[] { null, Part(-100m) } }), HttpStatusCode.UnprocessableEntity, BankingCodes.SplitPart);
        await BankingApi.AssertRefusedAsync(await Put(new { parts = new object[] { new { note = "x" }, Part(-100m) } }), HttpStatusCode.UnprocessableEntity, BankingCodes.SplitPart);
        await BankingApi.AssertRefusedAsync(await Put(new { parts = new object[] { new { amount = "abc" }, Part(-100m) } }), HttpStatusCode.UnprocessableEntity, BankingCodes.SplitPart);
        await BankingApi.AssertRefusedAsync(await Put(new { }), HttpStatusCode.UnprocessableEntity, BankingCodes.SplitPart);
        Assert.Equal(HttpStatusCode.BadRequest, (await Put(new { parts = new object[] { Part(-60m), Part(-40m) }, extra = 1 })).StatusCode);
        Assert.Equal((null, false, 0), Of(await LineAsync(app, app.AdultA, line)));
    }

    [Fact]
    public async Task Unsplitting_a_linked_line_gives_back_its_link_category()
    {
        await using var s = await StartAsync(fixture);
        var app = s.App;
        var line = await AddAsync(app, s.Courant, "a", -500m, "VIR VERS LIVRET");
        await using (var scope = app.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
            await scope.ServiceProvider.GetRequiredService<ILinkStore>().SetAsync(new BankLink(line, null, null, null, s.Livret,
                LinkOutcome.InternalTransfer, null, "job", At), TransactionLinkState.Internal, TestContext.Current.CancellationToken, "internal.transfer");
        }
        await OkAsync(await app.RawAsync(app.AdultA, HttpMethod.Put, Url(line, "category"), new { categoryId = Seed("food.groceries") }));
        await OkAsync(await SplitAsync(app, app.AdultA, line, Part(-300m, "food.groceries"), Part(-200m)));

        var back = await OkAsync(await app.RawAsync(app.AdultA, HttpMethod.Delete, Url(line, "split")));

        Assert.Equal(("link", true, 0), Of(back));
        Assert.Equal("Virement entre comptes", back.GetProperty("category").GetProperty("name").GetString());
    }

    [Fact]
    public async Task Deleting_an_account_removes_its_lines_parts()
    {
        await using var s = await StartAsync(fixture);
        var app = s.App;
        var line = await AddAsync(app, s.Courant, "a", -100m);
        await OkAsync(await SplitAsync(app, app.AdultA, line, Part(-60m), Part(-40m)));

        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BankingDbContext>();
        Assert.Equal(2, await db.TransactionParts.CountAsync(TestContext.Current.CancellationToken));
        await db.Accounts.Where(a => a.Id == s.Courant).ExecuteDeleteAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, await db.TransactionParts.CountAsync(TestContext.Current.CancellationToken));
    }
}
