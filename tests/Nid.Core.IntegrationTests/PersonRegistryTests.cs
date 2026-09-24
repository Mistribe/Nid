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
    public async Task The_first_login_creates_an_admin_adult_and_the_household_settings()
    {
        await using var db = await postgres.NewDatabaseAsync();
        var registry = new PersonRegistry(db, PostgresFixture.Clock);

        var result = await registry.ProvisionAdultAsync("sub-a", "Alice", "alice@example.test", default);

        Assert.True(result.TryGetValue(out var person));
        Assert.Equal((PersonKind.Adult, true, false), (person.Kind, person.IsAdmin, person.IsPending));
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
    public async Task An_unknown_identity_is_refused_once_an_adult_exists()
    {
        await using var db = await postgres.NewDatabaseAsync();
        var registry = new PersonRegistry(db, PostgresFixture.Clock);
        await registry.ProvisionAdultAsync("sub-a", "Alice", null, default);

        var stranger = await registry.ProvisionAdultAsync("sub-x", "Mallory", "mallory@example.test", default);

        Assert.Equal(PersonCodes.NotRegistered, Code(stranger));
        Assert.Single(db.Persons);
    }

    [Fact]
    public async Task A_preregistered_adult_is_bound_by_email_whatever_the_case()
    {
        await using var db = await postgres.NewDatabaseAsync();
        var registry = new PersonRegistry(db, PostgresFixture.Clock);
        await registry.ProvisionAdultAsync("sub-a", "Alice", null, default);

        Assert.True((await registry.PreRegisterAdultAsync(" Bob ", " bob@example.test ", default)).TryGetValue(out var pending));
        Assert.Equal(("Bob", "bob@example.test", true, false), (pending.DisplayName, pending.Email, pending.IsPending, pending.IsAdmin));

        Assert.True((await registry.ProvisionAdultAsync("sub-b", "Bob B.", "BOB@Example.test", default)).TryGetValue(out var bound));
        Assert.Equal((pending.Id, "Bob B.", false), (bound.Id, bound.DisplayName, bound.IsPending));
        Assert.Equal(2, db.Persons.Count());
    }

    [Fact]
    public async Task Without_an_email_claim_no_pending_adult_is_bound()
    {
        await using var db = await postgres.NewDatabaseAsync();
        var registry = new PersonRegistry(db, PostgresFixture.Clock);
        await registry.ProvisionAdultAsync("sub-a", "Alice", null, default);
        await registry.PreRegisterAdultAsync("Bob", "bob@example.test", default);

        Assert.Equal(PersonCodes.NotRegistered, Code(await registry.ProvisionAdultAsync("sub-b", "Bob", null, default)));
    }

    [Fact]
    public async Task Preregistration_needs_a_name_and_an_email_unique_among_adults()
    {
        await using var db = await postgres.NewDatabaseAsync();
        var registry = new PersonRegistry(db, PostgresFixture.Clock);
        await registry.ProvisionAdultAsync("sub-a", "Alice", "alice@example.test", default);

        Assert.Equal(PersonCodes.NameRequired, Code(await registry.PreRegisterAdultAsync("  ", "bob@example.test", default)));
        Assert.Equal(PersonCodes.EmailRequired, Code(await registry.PreRegisterAdultAsync("Bob", "not-an-email", default)));
        Assert.Equal(PersonCodes.EmailTaken, Code(await registry.PreRegisterAdultAsync("Bob", "ALICE@example.test", default)));
    }

    [Fact]
    public async Task Pending_adults_count_against_the_cap()
    {
        await using var db = await postgres.NewDatabaseAsync();
        var registry = new PersonRegistry(db, PostgresFixture.Clock);
        await registry.ProvisionAdultAsync("sub-a", "Alice", null, default);
        Assert.True((await registry.PreRegisterAdultAsync("Bob", "bob@example.test", default)).IsSuccess);

        Assert.Equal(PersonCodes.AdultCap, Code(await registry.PreRegisterAdultAsync("Carol", "carol@example.test", default)));

        await db.HouseholdSettings.ExecuteUpdateAsync(s => s.SetProperty(x => x.MaxAdults, 3));
        Assert.True((await registry.PreRegisterAdultAsync("Carol", "carol@example.test", default)).IsSuccess);
    }

    [Fact]
    public async Task Simultaneous_first_logins_make_exactly_one_admin()
    {
        var connectionString = await postgres.NewConnectionStringAsync();

        var attempts = Enumerable.Range(0, 6).Select(async i =>
        {
            await using var db = PostgresFixture.NewCoreContext(connectionString);
            return await new PersonRegistry(db, PostgresFixture.Clock)
                .ProvisionAdultAsync($"sub-{i}", $"Person {i}", $"person{i}@example.test", default);
        });
        var results = await Task.WhenAll(attempts);

        Assert.Single(results, r => r.IsSuccess);
        Assert.Equal(5, results.Count(r => Code(r) == PersonCodes.NotRegistered));
        await using var probe = PostgresFixture.NewCoreContext(connectionString);
        Assert.Equal(1, await probe.Persons.CountAsync(p => p.Kind == "adult" && p.IsAdmin));
        Assert.Equal(1, await probe.Persons.CountAsync(p => p.Kind == "adult"));
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

    private static string? Code(UnambitiousFx.Functional.Result result)
        => result.TryGetFailure(out var failure) ? failure!.Code : null;

    private static async Task<(PersonRegistry Registry, PersonInfo Admin, PersonInfo Pending)> HouseholdAsync(CoreDbContext db)
    {
        var registry = new PersonRegistry(db, PostgresFixture.Clock);
        var admin = (await registry.ProvisionAdultAsync("sub-a", "Alice", "alice@example.test", default)).Ok();
        var pending = (await registry.PreRegisterAdultAsync("Bob", "bob@example.test", default)).Ok();
        return (registry, admin, pending);
    }

    [Fact]
    public async Task Only_a_pending_adult_who_owns_nothing_can_be_removed()
    {
        await using var db = await postgres.NewDatabaseAsync();
        var (registry, admin, pending) = await HouseholdAsync(db);

        Assert.Equal(PersonCodes.PersonActive, Code(await registry.DeletePendingAdultAsync(admin.Id, default)));
        Assert.IsType<UnambitiousFx.Functional.Failures.NotFoundFailure>(
            (await registry.DeletePendingAdultAsync(PersonId.New(), default)).TryGetFailure(out var missing) ? missing : null);

        Assert.True((await registry.DeletePendingAdultAsync(pending.Id, default)).IsSuccess);
        Assert.Single(db.Persons);
    }

    [Fact]
    public async Task A_pending_adult_who_owns_an_item_cannot_be_removed()
    {
        await using var db = await postgres.NewDatabaseAsync();
        var (registry, _, pending) = await HouseholdAsync(db);
        var items = new ItemRegistry(db, PostgresFixture.Clock);
        (await items.CreateAsync(new NewItem("subscriptions", "subscription", "imported", Visibility.Shared, null,
            [new OwnerShare(pending.Id, 10_000)]), default)).Ok();

        Assert.Equal(PersonCodes.PersonActive, Code(await registry.DeletePendingAdultAsync(pending.Id, default)));
    }

    [Fact]
    public async Task The_last_active_admin_cannot_be_demoted_and_a_dependent_cannot_be_promoted()
    {
        await using var db = await postgres.NewDatabaseAsync();
        var (registry, admin, pending) = await HouseholdAsync(db);
        var kid = (await registry.CreateDependentAsync("Kid", default)).Ok();

        Assert.Equal(PersonCodes.LastAdmin, Code(await registry.SetAdminAsync(admin.Id, false, default)));
        // A pending admin cannot act yet: it does not keep the household administered.
        Assert.True((await registry.SetAdminAsync(pending.Id, true, default)).Ok().IsAdmin);
        Assert.Equal(PersonCodes.LastAdmin, Code(await registry.SetAdminAsync(admin.Id, false, default)));
        Assert.Equal(PersonCodes.NotAnAdult, Code(await registry.SetAdminAsync(kid.Id, true, default)));
    }

    [Fact]
    public async Task An_admin_can_step_down_once_another_active_admin_exists()
    {
        await using var db = await postgres.NewDatabaseAsync();
        var (registry, admin, pending) = await HouseholdAsync(db);
        await registry.ProvisionAdultAsync("sub-b", "Bob", "bob@example.test", default);
        await registry.SetAdminAsync(pending.Id, true, default);

        var demoted = (await registry.SetAdminAsync(admin.Id, false, default)).Ok();

        Assert.False(demoted.IsAdmin);
    }

    [Fact]
    public async Task Two_simultaneous_demotions_leave_one_active_admin()
    {
        var connectionString = await postgres.NewConnectionStringAsync();
        PersonId alice, bob;
        await using (var db = PostgresFixture.NewCoreContext(connectionString))
        {
            var registry = new PersonRegistry(db, PostgresFixture.Clock);
            alice = (await registry.ProvisionAdultAsync("sub-a", "Alice", null, default)).Ok().Id;
            bob = (await registry.PreRegisterAdultAsync("Bob", "bob@example.test", default)).Ok().Id;
            await registry.ProvisionAdultAsync("sub-b", "Bob", "bob@example.test", default);
            await registry.SetAdminAsync(bob, true, default);
        }

        var results = await Task.WhenAll(new[] { alice, bob }.Select(async id =>
        {
            await using var db = PostgresFixture.NewCoreContext(connectionString);
            return await new PersonRegistry(db, PostgresFixture.Clock).SetAdminAsync(id, false, default);
        }));

        Assert.Single(results, r => r.IsSuccess);
        await using var probe = PostgresFixture.NewCoreContext(connectionString);
        Assert.Equal(1, await probe.Persons.CountAsync(p => p.IsAdmin));
    }

    [Fact]
    public async Task Signing_out_everywhere_increments_the_session_epoch()
    {
        await using var db = await postgres.NewDatabaseAsync();
        var (registry, admin, _) = await HouseholdAsync(db);

        var revoked = (await registry.RevokeSessionsAsync(admin.Id, default)).Ok();

        Assert.Equal(admin.SessionEpoch + 1, revoked.SessionEpoch);
        Assert.IsType<UnambitiousFx.Functional.Failures.NotFoundFailure>(
            (await registry.RevokeSessionsAsync(PersonId.New(), default)).TryGetFailure(out var missing) ? missing : null);
    }
}
