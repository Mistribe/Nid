using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Nid.Banking.Application;
using Nid.Banking.Contracts;
using Nid.Banking.Domain;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.TestSupport;
using NodaTime;

namespace Nid.Banking.IntegrationTests;

/// <summary>Transaction categories spec §By hand, decision 6: a line's category chosen by a person, given back to Nid, and shown in the line views.</summary>
public class TransactionCategoryTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static readonly LocalDate Today = new(2026, 10, 29);
    private static readonly Instant At = Today.At(new LocalTime(10, 0)).InUtc().ToInstant();

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

    private static async Task<Guid> AddAsync(
        NidApp app, Guid account, string reference, decimal amount, string label = "PRLV SEPA NETFLIX 123", string? bankCode = null)
    {
        await using var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
        var store = scope.ServiceProvider.GetRequiredService<ITransactionStore>();
        await store.AddAsync(account, [new NewBankTransaction(reference, new LocalDate(2026, 10, 5), null, amount, Currency.Eur, label, null, null, "api") { BankCode = bankCode }], default);
        return (await store.ListAsync([account], null, null, 0, 100, default)).Single(t => t.ProviderRef == reference).Id;
    }

    /// <summary>The line linked to its counterpart account as the internal pass does, with its link category.</summary>
    private static async Task InternalAsync(NidApp app, Guid line, Guid counterpart)
    {
        await using var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
        await scope.ServiceProvider.GetRequiredService<ILinkStore>().SetAsync(new BankLink(line, null, null, null, counterpart,
            LinkOutcome.InternalTransfer, null, "job", At), TransactionLinkState.Internal, default, "internal.transfer");
    }

    private static string Category(Guid line) => $"{BankingApi.Root}/transactions/{line}/category";

    private static Task<HttpResponseMessage> PutAsync(NidApp app, PersonId person, Guid line, Guid? categoryId)
        => app.RawAsync(person, HttpMethod.Put, Category(line), new { categoryId });

    private static async Task<JsonElement> OkAsync(HttpResponseMessage response)
    {
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await BankingApi.JsonAsync(response);
    }

    private static async Task<(string Body, JsonElement Line)> LineAsync(NidApp app, PersonId person, Guid line)
    {
        var body = await app.GetRawAsync(person, $"{BankingApi.Root}/transactions?state=all");
        return (body, JsonDocument.Parse(body).RootElement.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("id").GetGuid() == line));
    }

    private static (string? Name, string? Source) Of(JsonElement line) => (
        line.GetProperty("category").ValueKind == JsonValueKind.Null ? null : line.GetProperty("category").GetProperty("name").GetString(),
        line.GetProperty("categorySource").GetString());

    [Fact]
    public async Task Hand_category_is_set_and_shown_with_source_hand()
    {
        await using var s = await StartAsync(fixture);
        var app = s.App;
        var line = await AddAsync(app, s.Courant, "a", -30m, "CARREFOUR CITY");

        var set = await OkAsync(await PutAsync(app, app.AdultA, line, Seed("food.groceries")));

        var category = set.GetProperty("category");
        Assert.Equal((Seed("food.groceries"), "Courses", Seed("food"), "Alimentation", "expense", "hand"), (category.GetProperty("id").GetGuid(),
            category.GetProperty("name").GetString(), category.GetProperty("parentId").GetGuid(), category.GetProperty("parentName").GetString(),
            category.GetProperty("kind").GetString(), set.GetProperty("categorySource").GetString()));
        Assert.Equal(("Courses", "hand"), Of((await LineAsync(app, app.AdultB, line)).Line));
        Assert.Equal(("Courses", "hand"), Of(Assert.Single((await BankingApi.JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get,
            $"{BankingApi.Root}/transactions?state=unlinked"))).GetProperty("items").EnumerateArray())));
    }

    [Fact]
    public async Task Same_label_lists_the_other_lines_nothing_classified_on_the_accounts_the_reader_writes()
    {
        await using var s = await StartAsync(fixture);
        var app = s.App;
        var line = await AddAsync(app, s.Courant, "a", -12.99m);
        var twin = await AddAsync(app, s.Courant, "b", -12.99m);
        var saved = await AddAsync(app, s.Livret, "c", -12.99m);
        await AddAsync(app, s.Courant, "d", -9.99m, "PRLV SEPA MUSIQUE 123");
        // Classified by hand, a category or « Sans catégorie »: never offered again.
        await OkAsync(await PutAsync(app, app.AdultA, await AddAsync(app, s.Courant, "e", -12.99m), Seed("leisure.subscriptions")));
        await OkAsync(await PutAsync(app, app.AdultA, await AddAsync(app, s.Courant, "f", -12.99m), null));

        static Guid[] Ids(string body) => [.. JsonDocument.Parse(body).RootElement.EnumerateArray().Select(e => e.GetGuid()).Order()];
        var url = $"{BankingApi.Root}/transactions/{line}/same-label";
        Assert.Equal(new[] { twin, saved }.Order(), Ids(await app.GetRawAsync(app.AdultA, url)));
        // B reads « Courant A » but writes no account: nothing to classify.
        Assert.Empty(Ids(await app.GetRawAsync(app.AdultB, url)));
    }

    [Fact]
    public async Task Null_category_by_hand_means_none_and_is_kept()
    {
        await using var s = await StartAsync(fixture);
        var app = s.App;
        var line = await AddAsync(app, s.Courant, "a", -12.99m);
        await OkAsync(await PutAsync(app, app.AdultA, line, Seed("food.groceries")));

        Assert.Equal((null, "hand"), Of(await OkAsync(await PutAsync(app, app.AdultA, line, null))));

        // A link writes below a person's choice: « Sans catégorie » stays.
        await using (var scope = app.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
            Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<ICategoryStore>()
                .SetForLinesAsync([line], Seed("leisure.subscriptions"), CategorySource.Link, "job", At, false, TestContext.Current.CancellationToken));
        }
        Assert.Equal((null, "hand"), Of((await LineAsync(app, app.AdultA, line)).Line));
    }

    [Fact]
    public async Task Archived_category_is_refused()
    {
        await using var s = await StartAsync(fixture);
        var app = s.App;
        var line = await AddAsync(app, s.Courant, "a", -30m, "CARREFOUR CITY");
        await OkAsync(await app.RawAsync(app.AdultA, HttpMethod.Patch, $"{BankingApi.Root}/categories/{Seed("food.delivery")}", new { archived = true }));
        await OkAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, $"{BankingApi.Root}/categories/{Seed("food.restaurants")}/merge",
            new { into = Seed("food.groceries") }));

        await BankingApi.AssertRefusedAsync(await PutAsync(app, app.AdultA, line, Seed("food.delivery")), HttpStatusCode.UnprocessableEntity,
            BankingCodes.CategoryArchived);
        await BankingApi.AssertRefusedAsync(await PutAsync(app, app.AdultA, line, Seed("food.restaurants")), HttpStatusCode.UnprocessableEntity,
            BankingCodes.CategoryArchived);
        Assert.Equal(HttpStatusCode.NotFound, (await PutAsync(app, app.AdultA, line, Guid.NewGuid())).StatusCode);
        Assert.Equal((null, null), Of((await LineAsync(app, app.AdultA, line)).Line));
    }

    [Fact]
    public async Task A_reader_without_write_access_cannot_set_it()
    {
        await using var s = await StartAsync(fixture);
        var app = s.App;
        var readable = await AddAsync(app, s.Courant, "a", -30m, "CARREFOUR CITY");
        var hidden = await AddAsync(app, s.Livret, "b", -30m, "CARREFOUR CITY");

        Assert.Equal(HttpStatusCode.Forbidden, (await PutAsync(app, app.AdultB, readable, Seed("food.groceries"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await app.RawAsync(app.AdultB, HttpMethod.Delete, Category(readable))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PutAsync(app, app.AdultB, hidden, Seed("food.groceries"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await app.RawAsync(app.AdultB, HttpMethod.Delete, Category(hidden))).StatusCode);
        Assert.Equal((null, null), Of((await LineAsync(app, app.AdultA, readable)).Line));
    }

    private static Task<HttpResponseMessage> BulkAsync(NidApp app, PersonId person, Guid? categoryId, params Guid[] lines)
        => app.RawAsync(person, HttpMethod.Post, $"{BankingApi.Root}/transactions/category", new { transactionIds = lines, categoryId });

    private static (Guid[] Changed, Dictionary<Guid, string> Refused) Outcome(JsonElement body) => (
        [.. body.GetProperty("changed").EnumerateArray().Select(c => c.GetGuid())],
        body.GetProperty("refused").EnumerateArray().ToDictionary(r => r.GetProperty("id").GetGuid(), r => r.GetProperty("code").GetString()!));

    [Fact]
    public async Task Bulk_sets_the_lines_it_may_and_lists_the_others_with_the_single_line_codes()
    {
        await using var s = await StartAsync(fixture);
        var app = s.App;
        var first = await AddAsync(app, s.Courant, "a", -30m, "CARREFOUR CITY");
        var second = await AddAsync(app, s.Courant, "b", -12m, "CARREFOUR CITY");
        var split = await AddAsync(app, s.Courant, "c", -30m, "CB DIVERS");
        var ignored = await AddAsync(app, s.Courant, "d", -5m, "CB DIVERS");
        await OkAsync(await app.RawAsync(app.AdultA, HttpMethod.Put, $"{BankingApi.Root}/transactions/{split}/split",
            new { parts = new object[] { new { amount = "-10" }, new { amount = "-20" } } }));
        await OkAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, $"{BankingApi.Root}/transactions/{ignored}/ignore"));
        var unknown = Guid.NewGuid();

        var (changed, refused) = Outcome(await OkAsync(await BulkAsync(app, app.AdultA, Seed("food.groceries"), first, second, split, ignored, unknown, first)));

        Assert.Equal([first, second], changed);
        Assert.Equal(new Dictionary<Guid, string>
        {
            [split] = BankingCodes.LineSplit, [ignored] = BankingCodes.LineIgnored, [unknown] = "NOT_FOUND",
        }, refused);
        Assert.Equal(("Courses", "hand"), Of((await LineAsync(app, app.AdultA, first)).Line));
        Assert.Equal(("Courses", "hand"), Of((await LineAsync(app, app.AdultA, second)).Line));
        Assert.Equal((null, null), Of((await LineAsync(app, app.AdultA, ignored)).Line));
        // One line alone is refused the same.
        await BankingApi.AssertRefusedAsync(await PutAsync(app, app.AdultA, ignored, Seed("food.groceries")), HttpStatusCode.Conflict, BankingCodes.LineIgnored);
    }

    [Fact]
    public async Task Bulk_refuses_a_line_the_caller_cannot_write_and_one_they_cannot_read_as_unknown()
    {
        await using var s = await StartAsync(fixture);
        var app = s.App;
        var readable = await AddAsync(app, s.Courant, "a", -30m, "CARREFOUR CITY");
        var hidden = await AddAsync(app, s.Livret, "b", -30m, "CARREFOUR CITY");

        var response = await BulkAsync(app, app.AdultB, null, readable, hidden, Guid.NewGuid());
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var (changed, refused) = Outcome(await OkAsync(response));

        Assert.Empty(changed);
        Assert.Equal(refused[readable], (await BankingApi.JsonAsync(await PutAsync(app, app.AdultB, readable, null))).GetProperty("code").GetString());
        // A line B cannot read answers as a line that does not exist.
        Assert.Equal("NOT_FOUND", refused[hidden]);
        Assert.DoesNotContain("Livret A", body, StringComparison.Ordinal);
        Assert.Equal((null, null), Of((await LineAsync(app, app.AdultA, readable)).Line));
        Assert.Equal((null, null), Of((await LineAsync(app, app.AdultA, hidden)).Line));
    }

    [Fact]
    public async Task Bulk_names_1_to_200_lines_and_a_category_in_use()
    {
        await using var s = await StartAsync(fixture);
        var app = s.App;
        var line = await AddAsync(app, s.Courant, "a", -30m, "CARREFOUR CITY");

        await BankingApi.AssertRefusedAsync(await BulkAsync(app, app.AdultA, null), HttpStatusCode.UnprocessableEntity, BankingCodes.CategoriseLines);
        await BankingApi.AssertRefusedAsync(await BulkAsync(app, app.AdultA, null, [.. Enumerable.Range(0, 201).Select(_ => Guid.NewGuid())]),
            HttpStatusCode.UnprocessableEntity, BankingCodes.CategoriseLines);
        Assert.Equal(HttpStatusCode.NotFound, (await BulkAsync(app, app.AdultA, Guid.NewGuid(), line)).StatusCode);
        Assert.Equal((null, null), Of((await LineAsync(app, app.AdultA, line)).Line));
    }

    [Fact]
    public async Task Reset_on_a_linked_line_gives_back_the_link_category()
    {
        await using var s = await StartAsync(fixture);
        var app = s.App;
        var debit = await AddAsync(app, s.Courant, "a", -500m, "VIR VERS LIVRET");
        var unlinked = await AddAsync(app, s.Courant, "b", -30m, "CARREFOUR CITY");
        await InternalAsync(app, debit, s.Livret);
        await OkAsync(await PutAsync(app, app.AdultA, debit, Seed("food.groceries")));
        await OkAsync(await PutAsync(app, app.AdultA, unlinked, Seed("food.groceries")));

        Assert.Equal(("Virement entre comptes", "link"), Of(await OkAsync(await app.RawAsync(app.AdultA, HttpMethod.Delete, Category(debit)))));
        Assert.Equal((null, null), Of(await OkAsync(await app.RawAsync(app.AdultA, HttpMethod.Delete, Category(unlinked)))));
    }

    [Fact]
    public async Task Reset_gives_back_the_bank_code_hint_unless_its_category_is_archived()
    {
        await using var s = await StartAsync(fixture);
        var app = s.App;
        var cash = await AddAsync(app, s.Courant, "a", -20m, "RETRAIT DAB", bankCode: "CCRD/CWDL");
        var fee = await AddAsync(app, s.Courant, "b", -2m, "FRAIS TENUE DE COMPTE", bankCode: "MDOP/CHRG");
        await OkAsync(await PutAsync(app, app.AdultA, cash, Seed("food.groceries")));
        await OkAsync(await PutAsync(app, app.AdultA, fee, Seed("food.groceries")));

        Assert.Equal(("Retrait d'espèces", "hint"), Of(await OkAsync(await app.RawAsync(app.AdultA, HttpMethod.Delete, Category(cash)))));

        // A hint never writes an archived category, as a rule does not.
        await OkAsync(await app.RawAsync(app.AdultA, HttpMethod.Patch, $"{BankingApi.Root}/categories/{Seed("bank")}", new { archived = true }));
        Assert.Equal((null, null), Of(await OkAsync(await app.RawAsync(app.AdultA, HttpMethod.Delete, Category(fee)))));
    }

    [Fact]
    public async Task A_masked_internal_line_still_shows_its_category()
    {
        await using var s = await StartAsync(fixture);
        var app = s.App;
        var debit = await AddAsync(app, s.Courant, "a", -500m, "VIR VERS LIVRET");
        await InternalAsync(app, debit, s.Livret);

        var (body, ofB) = await LineAsync(app, app.AdultB, debit);

        Assert.Equal(("linked", JsonValueKind.Null), (ofB.GetProperty("linkState").GetString(), ofB.GetProperty("link").ValueKind));
        Assert.Equal(("Virement entre comptes", "link"), Of(ofB));
        Assert.Equal(("Mouvements internes", "transfer"), (ofB.GetProperty("category").GetProperty("parentName").GetString(),
            ofB.GetProperty("category").GetProperty("kind").GetString()));
        Assert.DoesNotContain("Livret A", body, StringComparison.Ordinal);
        Assert.DoesNotContain(s.Livret.ToString(), body, StringComparison.Ordinal);
    }
}
