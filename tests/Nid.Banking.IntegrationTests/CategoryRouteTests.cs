using System.Net;
using System.Text.Json;
using Nid.Banking.Contracts;
using Nid.Banking.Domain;
using Nid.TestSupport;

namespace Nid.Banking.IntegrationTests;

/// <summary>Transaction categories: the routes that list, create, rename, archive and merge them.</summary>
public class CategoryRouteTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static string Root => $"{BankingApi.Root}/categories";

    private static Guid Seed(string key) => CategorySeed.Find(key)!.Id;

    [Fact]
    public async Task Any_adult_lists_the_seed()
    {
        await using var app = await BankingApi.StartAsync(fixture);
        foreach (var person in new[] { app.AdultA, app.AdultB })
        {
            var response = await app.RawAsync(person, HttpMethod.Get, Root);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var all = (await BankingApi.JsonAsync(response)).EnumerateArray().ToList();
            Assert.Equal(CategorySeed.All.Count, all.Count);
            var groceries = Assert.Single(all, c => c.GetProperty("key").GetString() == "food.groceries");
            Assert.Equal("Courses", groceries.GetProperty("name").GetString());
            Assert.Equal("Courses", groceries.GetProperty("defaultName").GetString());
            Assert.Equal("expense", groceries.GetProperty("kind").GetString());
            Assert.Equal(Seed("food"), groceries.GetProperty("parentId").GetGuid());
            Assert.False(groceries.GetProperty("archived").GetBoolean());
        }
    }

    [Fact]
    public async Task Create_rename_archive_and_reset_name_round_trip()
    {
        await using var app = await BankingApi.StartAsync(fixture);

        var created = await app.RawAsync(app.AdultA, HttpMethod.Post, Root, new { name = "Animaux", kind = "expense" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var own = await BankingApi.JsonAsync(created);
        var id = own.GetProperty("id").GetGuid();
        Assert.Equal(JsonValueKind.Null, own.GetProperty("defaultName").ValueKind);

        var child = await BankingApi.JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, Root, new { parentId = id, name = "Veto" }));
        Assert.Equal("expense", child.GetProperty("kind").GetString());

        var renamed = await BankingApi.JsonAsync(await app.RawAsync(app.AdultB, HttpMethod.Patch, $"{Root}/{id}", new { name = "Compagnons" }));
        Assert.Equal("Compagnons", renamed.GetProperty("name").GetString());

        var archived = await BankingApi.JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Patch, $"{Root}/{id}", new { archived = true }));
        Assert.True(archived.GetProperty("archived").GetBoolean());

        var groceries = Seed("food.groceries");
        await app.RawAsync(app.AdultA, HttpMethod.Patch, $"{Root}/{groceries}", new { name = "Supermarché" });
        var reset = await BankingApi.JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Patch, $"{Root}/{groceries}", new { resetName = true }));
        Assert.Equal("Courses", reset.GetProperty("name").GetString());

        await BankingApi.AssertRefusedAsync(
            await app.RawAsync(app.AdultA, HttpMethod.Patch, $"{Root}/{id}", new { resetName = true }), HttpStatusCode.UnprocessableEntity, BankingCodes.CategoryName);
        await BankingApi.AssertRefusedAsync(
            await app.RawAsync(app.AdultA, HttpMethod.Post, Root, new { name = "Sans genre" }), HttpStatusCode.UnprocessableEntity, BankingCodes.CategoryKind);
    }

    [Fact]
    public async Task Merge_returns_the_target_and_the_source_is_archived()
    {
        await using var app = await BankingApi.StartAsync(fixture);
        var from = Seed("food.delivery");
        var into = Seed("food.restaurants");

        var response = await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Root}/{from}/merge", new { into });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(into, (await BankingApi.JsonAsync(response)).GetProperty("id").GetGuid());

        var source = (await BankingApi.JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, Root))).EnumerateArray()
            .Single(c => c.GetProperty("id").GetGuid() == from);
        Assert.True(source.GetProperty("archived").GetBoolean());
        Assert.Equal(into, source.GetProperty("mergedIntoId").GetGuid());
    }

    [Fact]
    public async Task Duplicate_name_is_a_field_error_with_code_category_name()
    {
        await using var app = await BankingApi.StartAsync(fixture);
        var response = await app.RawAsync(app.AdultA, HttpMethod.Post, Root, new { parentId = Seed("food"), name = "COURSES" });

        await BankingApi.AssertRefusedAsync(response, HttpStatusCode.UnprocessableEntity, BankingCodes.CategoryName);
    }
}
