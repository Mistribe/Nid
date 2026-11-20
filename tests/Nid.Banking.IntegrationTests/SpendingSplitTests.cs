using System.Globalization;
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

/// <summary>#84: a split line counts by its parts everywhere spending is counted, never also as the whole line.
/// Today is <see cref="NidApp.GoldenCaseToday"/> (2025-10-15).</summary>
public class SpendingSplitTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static Guid Seed(string key) => CategorySeed.Find(key)!.Id;
    private static string Money(decimal amount) => amount.ToString(CultureInfo.InvariantCulture);

    /// <summary>One joint account; on it a −100 line split −70 « Courses » / −30 « Restauration », and a −10 « Courses » line.</summary>
    private static async Task<(NidApp App, Guid Account, Guid Split)> StartAsync(PostgresFixture fixture)
    {
        var app = await BankingApi.StartAsync(fixture);
        await BankingApi.ConnectAsync(app, app.AdultA, "code-a", BankingApi.Account("uid-1", "hash-1", null, "Commun"));
        var account = (await SpendingTests.AccountsAsync(app, app.AdultA))["Commun"];
        await SpendingTests.PatchAsync(app, app.AdultA, account,
            new { envelopeId = (await app.NewEnvelopeAsync("Commun", Visibility.Shared, (app.AdultA, 5_000), (app.AdultB, 5_000))).Value });
        await SpendingTests.LineAsync(app, account, new(2025, 10, 2), -10m, "food.groceries");
        var split = await SpendingTests.LineAsync(app, account, new(2025, 10, 3), -100m);
        var response = await app.RawAsync(app.AdultA, HttpMethod.Put, $"{BankingApi.Root}/transactions/{split}/split", new
        {
            parts = new object[]
            {
                new { amount = Money(-70m), categoryId = Seed("food.groceries"), note = "Courses" },
                new { amount = Money(-30m), categoryId = Seed("food.delivery"), note = "Pizzas" },
            },
        });
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return (app, account, split);
    }

    private static async Task<JsonElement> GetAsync(NidApp app, string url)
    {
        var response = await app.RawAsync(app.AdultA, HttpMethod.Get, url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await BankingApi.JsonAsync(response);
    }

    private static JsonElement? Find(JsonElement view, string key)
        => view.GetProperty("categories").EnumerateArray().SelectMany(c => c.GetProperty("children").EnumerateArray().Prepend(c))
            .Where(c => c.GetProperty("id").GetGuid() == Seed(key)).Select(c => (JsonElement?)c).SingleOrDefault();

    [Fact]
    public async Task Spending_counts_the_parts_and_never_the_whole_line()
    {
        var (app, _, _) = await StartAsync(fixture);
        await using var _ = app;
        var view = await GetAsync(app, $"{BankingApi.Root}/spending");
        Assert.Equal("110.00", view.GetProperty("total").GetString());
        Assert.Equal("80.00", Find(view, "food.groceries")!.Value.GetProperty("amount").GetString());
        Assert.Equal("30.00", Find(view, "food.delivery")!.Value.GetProperty("amount").GetString());
        Assert.True(view.GetProperty("uncategorised").ValueKind == JsonValueKind.Null || view.GetProperty("uncategorised").GetProperty("amount").GetString() == "0.00");
    }

    [Fact]
    public async Task A_category_s_lines_list_the_part_once_with_its_amount_category_and_note()
    {
        var (app, _, split) = await StartAsync(fixture);
        await using var _ = app;
        var lines = await GetAsync(app, $"{BankingApi.Root}/spending/lines?categoryId={Seed("food.groceries")}");
        Assert.Equal(2, lines.GetProperty("count").GetInt32());
        var items = lines.GetProperty("items").EnumerateArray().ToList();
        var part = Assert.Single(items, i => i.GetProperty("id").GetGuid() == split);
        Assert.Equal("-70.00", part.GetProperty("amount").GetString());
        Assert.Equal("Courses", part.GetProperty("note").GetString());
        Assert.Equal(Seed("food.groceries"), part.GetProperty("category").GetProperty("id").GetGuid());
        var other = await GetAsync(app, $"{BankingApi.Root}/spending/lines?categoryId={Seed("food.delivery")}");
        Assert.Equal(["-30.00"], other.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("amount").GetString()!));
        var none = await GetAsync(app, $"{BankingApi.Root}/spending/lines?uncategorised=true");
        Assert.Equal(0, none.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task A_budget_is_consumed_by_the_part_and_the_dashboard_counts_the_line_once()
    {
        var (app, _, _) = await StartAsync(fixture);
        await using var _ = app;
        var put = await app.RawAsync(app.AdultA, HttpMethod.Put, $"{BankingApi.Root}/budgets/{Seed("food.delivery")}", new { amount = "100", period = "month" });
        Assert.True(put.StatusCode == HttpStatusCode.OK, await put.Content.ReadAsStringAsync(Ct));
        var budget = Find(await GetAsync(app, $"{BankingApi.Root}/spending"), "food.delivery")!.Value.GetProperty("budget");
        Assert.Equal("70.00", budget.GetProperty("left").GetString());

        await using var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.Browser(app.AdultA);
        var read = await scope.ServiceProvider.GetRequiredService<ISpendingContribution>()
            .EverydayAsync(app.AdultA, VisibilityScope.Household, new LocalDate(2025, 10, 15), Ct);
        Assert.True(read.TryGetValue(out var part));
        Assert.Equal(110m, part!.ByCategory.Sum(s => Assert.Single(s.Amount).Value));
    }

    [Fact]
    public async Task Operations_lists_a_split_line_once_for_a_category_of_a_part_and_not_as_uncategorised()
    {
        var (app, _, split) = await StartAsync(fixture);
        await using var _ = app;
        foreach (var key in new[] { "food.groceries", "food.delivery", "food" })
        {
            var page = await GetAsync(app, $"{BankingApi.Root}/operations?category={Seed(key)}");
            Assert.Single(page.GetProperty("items").EnumerateArray(), i => i.GetProperty("id").GetGuid() == split);
        }
        Assert.Empty((await GetAsync(app, $"{BankingApi.Root}/operations?category={Seed("transport.fuel")}")).GetProperty("items").EnumerateArray());
        var uncategorised = await GetAsync(app, $"{BankingApi.Root}/operations?uncategorised=true");
        Assert.DoesNotContain(uncategorised.GetProperty("items").EnumerateArray(), i => i.GetProperty("id").GetGuid() == split);
    }

    [Fact]
    public async Task Merging_a_category_moves_the_parts_filed_in_it()
    {
        var (app, _, split) = await StartAsync(fixture);
        await using var _ = app;
        var merge = await app.RawAsync(app.AdultA, HttpMethod.Post, $"{BankingApi.Root}/categories/{Seed("food.delivery")}/merge", new { into = Seed("food.restaurants") });
        Assert.Equal(HttpStatusCode.OK, merge.StatusCode);
        var lines = await GetAsync(app, $"{BankingApi.Root}/spending/lines?categoryId={Seed("food.restaurants")}");
        Assert.Equal(["-30.00"], lines.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("amount").GetString()!));
        Assert.Equal(split, lines.GetProperty("items")[0].GetProperty("id").GetGuid());
    }
}
