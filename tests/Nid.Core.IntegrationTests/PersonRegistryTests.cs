using Microsoft.EntityFrameworkCore;
using Nid.Core.Contracts;
using Nid.Core.Infrastructure.Persistence;
using Nid.TestSupport;

namespace Nid.Core.IntegrationTests;

[Collection("postgres")]
public class PersonRegistryTests(PostgresFixture postgres)
{
    private static string? Code(UnambitiousFx.Functional.Result<PersonInfo> result)
        => result.TryGetFailure(out var failure) ? failure!.Code : null;

    [Fact]
    public async Task First_login_creates_an_adult_and_the_household_settings()
    {
        await using var db = await postgres.NewDatabaseAsync();
        var registry = new PersonRegistry(db, PostgresFixture.Clock);

        var result = await registry.ProvisionAdultAsync("sub-a", "Alice", "alice@example.test", default);

        Assert.True(result.TryGetValue(out var person));
        Assert.Equal(PersonKind.Adult, person.Kind);
        Assert.Equal(1, db.HouseholdSettings.Count());
    }

    [Fact]
    public async Task Second_login_of_the_same_subject_reuses_the_person_and_refreshes_the_name()
    {
        await using var db = await postgres.NewDatabaseAsync();
        var registry = new PersonRegistry(db, PostgresFixture.Clock);

        Assert.True((await registry.ProvisionAdultAsync("sub-a", "Alice", null, default)).TryGetValue(out var first));
        Assert.True((await registry.ProvisionAdultAsync("sub-a", "Alice Renamed", null, default)).TryGetValue(out var second));

        Assert.Equal(first.Id, second.Id);
        Assert.Equal("Alice Renamed", second.DisplayName);
        Assert.Single(db.Persons);
    }

    [Fact]
    public async Task A_third_adult_is_refused()
    {
        await using var db = await postgres.NewDatabaseAsync();
        var registry = new PersonRegistry(db, PostgresFixture.Clock);

        await registry.ProvisionAdultAsync("sub-a", "Alice", null, default);
        await registry.ProvisionAdultAsync("sub-b", "Bob", null, default);
        var third = await registry.ProvisionAdultAsync("sub-c", "Carol", null, default);

        Assert.Equal("core.adult_cap", Code(third));
    }

    [Fact]
    public async Task Two_simultaneous_first_logins_never_exceed_two_adults()
    {
        var connectionString = await postgres.NewConnectionStringAsync();

        var attempts = Enumerable.Range(0, 6).Select(async i =>
        {
            await using var db = PostgresFixture.NewCoreContext(connectionString);
            return await new PersonRegistry(db, PostgresFixture.Clock).ProvisionAdultAsync($"sub-{i}", $"Person {i}", null, default);
        });
        var results = await Task.WhenAll(attempts);

        Assert.Equal(2, results.Count(r => r.IsSuccess));
        await using var probe = PostgresFixture.NewCoreContext(connectionString);
        Assert.Equal(2, probe.Persons.Count(p => p.Kind == "adult"));
    }

    [Fact]
    public async Task Dependent_has_no_subject_and_cannot_be_blank()
    {
        await using var db = await postgres.NewDatabaseAsync();
        var registry = new PersonRegistry(db, PostgresFixture.Clock);

        Assert.True((await registry.CreateDependentAsync("  Kid ", default)).TryGetValue(out var kid));
        Assert.Equal(PersonKind.Dependent, kid.Kind);
        Assert.Equal("Kid", kid.DisplayName);
        Assert.Equal("core.name_required", Code(await registry.CreateDependentAsync("   ", default)));
    }
}
