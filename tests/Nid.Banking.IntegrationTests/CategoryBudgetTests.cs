using System.Net;
using System.Text.Json;
using Nid.Banking.Contracts;
using Nid.Banking.Domain;
using Nid.TestSupport;

namespace Nid.Banking.IntegrationTests;

/// <summary>#85 spec §Model: the budget routes, one budget per expense category and per branch, and what a merge or an archive does to one.</summary>
public class CategoryBudgetTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static string Root => $"{BankingApi.Root}/budgets";

    private static Guid Seed(string key) => CategorySeed.Find(key)!.Id;

    private static Task<HttpResponseMessage> SetAsync(NidApp app, string key, string? amount, string period = "month")
        => app.RawAsync(app.AdultA, HttpMethod.Put, $"{Root}/{Seed(key)}", new { amount, period });

    private static async Task OkAsync(Task<HttpResponseMessage> request)
    {
        var response = await request;
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    private static async Task<List<JsonElement>> ListAsync(NidApp app)
        => [.. (await BankingApi.JsonAsync(await app.RawAsync(app.AdultB, HttpMethod.Get, Root))).EnumerateArray()];

    private static Task<HttpResponseMessage> MergeAsync(NidApp app, string from, string into)
        => app.RawAsync(app.AdultA, HttpMethod.Post, $"{BankingApi.Root}/categories/{Seed(from)}/merge", new { into = Seed(into) });

    [Fact]
    public async Task Set_replace_list_and_delete_round_trip()
    {
        await using var app = await BankingApi.StartAsync(fixture);

        var created = await BankingApi.JsonAsync(await SetAsync(app, "food.groceries", "300"));
        Assert.Equal((Seed("food.groceries"), "Courses", "300.00", "month"), (created.GetProperty("categoryId").GetGuid(),
            created.GetProperty("categoryName").GetString(), created.GetProperty("amount").GetString(), created.GetProperty("period").GetString()));

        // The other adult replaces it: null follows what is committed, a yearly amount.
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultB, HttpMethod.Put, $"{Root}/{Seed("food.groceries")}", new { amount = (string?)null, period = "year" })).StatusCode);
        var budget = Assert.Single(await ListAsync(app));
        Assert.Equal(JsonValueKind.Null, budget.GetProperty("amount").ValueKind);
        Assert.Equal("year", budget.GetProperty("period").GetString());

        Assert.Equal(HttpStatusCode.NoContent, (await app.RawAsync(app.AdultA, HttpMethod.Delete, $"{Root}/{Seed("food.groceries")}")).StatusCode);
        Assert.Empty(await ListAsync(app));
        Assert.Equal(HttpStatusCode.NotFound, (await app.RawAsync(app.AdultA, HttpMethod.Delete, $"{Root}/{Seed("food.groceries")}")).StatusCode);
    }

    [Fact]
    public async Task Amount_period_and_category_are_checked()
    {
        await using var app = await BankingApi.StartAsync(fixture);

        foreach (var amount in new[] { "-5", "abc", "1e3", "1000000000000000" })
        {
            var response = await SetAsync(app, "food.groceries", amount);
            var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            Assert.Contains(BankingCodes.BudgetAmount, body);
            Assert.Contains("amount", body);
        }
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await SetAsync(app, "food.groceries", "10", "week")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await app.RawAsync(app.AdultA, HttpMethod.Put, $"{Root}/{Guid.NewGuid()}", new { amount = "10", period = "month" })).StatusCode);
        await BankingApi.AssertRefusedAsync(await SetAsync(app, "income.salary", "10"), HttpStatusCode.UnprocessableEntity, BankingCodes.CategoryKind);
        await BankingApi.AssertRefusedAsync(await SetAsync(app, "income", "10"), HttpStatusCode.UnprocessableEntity, BankingCodes.CategoryKind);

        await OkAsync(app.RawAsync(app.AdultA, HttpMethod.Patch, $"{BankingApi.Root}/categories/{Seed("food.delivery")}", new { archived = true }));
        await BankingApi.AssertRefusedAsync(await SetAsync(app, "food.delivery", "10"), HttpStatusCode.UnprocessableEntity, BankingCodes.CategoryArchived);
        Assert.Empty(await ListAsync(app));
    }

    [Fact]
    public async Task Writes_need_banking_on()
    {
        await using var app = await BankingApi.StartAsync(fixture, enable: false);
        await BankingApi.AssertRefusedAsync(await SetAsync(app, "food.groceries", "10"), HttpStatusCode.Conflict, BankingCodes.Disabled);
        await BankingApi.AssertRefusedAsync(
            await app.RawAsync(app.AdultA, HttpMethod.Delete, $"{Root}/{Seed("food.groceries")}"), HttpStatusCode.Conflict, BankingCodes.Disabled);
    }

    [Fact]
    public async Task A_parent_and_its_children_share_one_budget()
    {
        await using var app = await BankingApi.StartAsync(fixture);

        await OkAsync(SetAsync(app, "food", "600"));
        await BankingApi.AssertRefusedAsync(await SetAsync(app, "food.groceries", "300"), HttpStatusCode.UnprocessableEntity, BankingCodes.BudgetBranch);

        await app.RawAsync(app.AdultA, HttpMethod.Delete, $"{Root}/{Seed("food")}");
        await OkAsync(SetAsync(app, "food.groceries", "300"));
        await BankingApi.AssertRefusedAsync(await SetAsync(app, "food", "600"), HttpStatusCode.UnprocessableEntity, BankingCodes.BudgetBranch);

        // Nor does a move put a budgeted child under a budgeted parent.
        await OkAsync(SetAsync(app, "home", "900"));
        await BankingApi.AssertRefusedAsync(await app.RawAsync(app.AdultA, HttpMethod.Patch, $"{BankingApi.Root}/categories/{Seed("food.groceries")}",
            new { parentId = Seed("home"), moveParent = true }), HttpStatusCode.UnprocessableEntity, BankingCodes.BudgetBranch);
    }

    [Fact]
    public async Task A_merge_moves_a_budget_and_never_makes_two_meet()
    {
        await using var app = await BankingApi.StartAsync(fixture);

        await OkAsync(SetAsync(app, "food.delivery", "80"));
        await OkAsync(MergeAsync(app, "food.delivery", "food.restaurants"));
        var moved = Assert.Single(await ListAsync(app));
        Assert.Equal((Seed("food.restaurants"), "80.00"), (moved.GetProperty("categoryId").GetGuid(), moved.GetProperty("amount").GetString()));

        // Both budgeted: refused, nothing merged.
        await OkAsync(SetAsync(app, "food.groceries", "300"));
        await BankingApi.AssertRefusedAsync(await MergeAsync(app, "food.restaurants", "food.groceries"), HttpStatusCode.UnprocessableEntity, BankingCodes.BudgetConflict);
        // « Alimentation » into « Logement »: its budgeted children would land under a budgeted parent.
        await OkAsync(SetAsync(app, "home", "900"));
        await BankingApi.AssertRefusedAsync(await MergeAsync(app, "food", "home"), HttpStatusCode.UnprocessableEntity, BankingCodes.BudgetConflict);

        var categories = (await BankingApi.JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, $"{BankingApi.Root}/categories"))).EnumerateArray()
            .ToDictionary(c => c.GetProperty("id").GetGuid());
        Assert.False(categories[Seed("food.restaurants")].GetProperty("archived").GetBoolean());
        Assert.False(categories[Seed("food")].GetProperty("archived").GetBoolean());
        Assert.Equal(Seed("food"), categories[Seed("food.groceries")].GetProperty("parentId").GetGuid());
        Assert.Equal(3, (await ListAsync(app)).Count);
    }

    [Fact]
    public async Task An_archived_category_keeps_its_budget_hidden()
    {
        await using var app = await BankingApi.StartAsync(fixture);
        await OkAsync(SetAsync(app, "food.groceries", "300"));
        var category = $"{BankingApi.Root}/categories/{Seed("food.groceries")}";

        await OkAsync(app.RawAsync(app.AdultA, HttpMethod.Patch, category, new { archived = true }));
        Assert.Empty(await ListAsync(app));

        await OkAsync(app.RawAsync(app.AdultA, HttpMethod.Patch, category, new { archived = false }));
        Assert.Equal("300.00", Assert.Single(await ListAsync(app)).GetProperty("amount").GetString());
    }

    [Fact]
    public async Task An_archived_childs_budget_never_blocks_its_parent()
    {
        await using var app = await BankingApi.StartAsync(fixture);
        await OkAsync(SetAsync(app, "food.groceries", "300"));
        await OkAsync(app.RawAsync(app.AdultA, HttpMethod.Patch, $"{BankingApi.Root}/categories/{Seed("food.groceries")}", new { archived = true }));

        await OkAsync(SetAsync(app, "food", "600"));
        Assert.Equal(Seed("food"), Assert.Single(await ListAsync(app)).GetProperty("categoryId").GetGuid());
    }
}
