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
    public async Task The_first_login_creates_an_adult_and_the_household_settings_and_is_not_an_admin_by_default()
    {
        await using var db = await postgres.NewDatabaseAsync();
        var registry = new PersonRegistry(db, PostgresFixture.Clock);

        var result = await registry.ProvisionAdultAsync("sub-a", "Alice", "alice@example.test", default);

        Assert.True(result.TryGetValue(out var person));
        Assert.Equal((PersonKind.Adult, false, false), (person.Kind, person.IsAdmin, person.IsPending));
        Assert.Equal(1, db.HouseholdSettings.Count());
    }

    [Fact]
    public async Task The_admin_role_is_the_identity_providers_and_follows_it_at_every_login()
    {
        await using var db = await postgres.NewDatabaseAsync();
        var registry = new PersonRegistry(db, PostgresFixture.Clock);

        Assert.True((await registry.ProvisionAdultAsync("sub-a", "Alice", null, isAdmin: true, default)).Ok().IsAdmin);
        Assert.True((await registry.ProvisionAdultAsync("sub-a", "Alice", null, isAdmin: true, default)).Ok().IsAdmin);
        // The only admin loses the group: the provider decides, nothing here keeps them an admin.
        Assert.False((await registry.ProvisionAdultAsync("sub-a", "Alice", null, isAdmin: false, default)).Ok().IsAdmin);
        Assert.True((await registry.ProvisionAdultAsync("sub-a", "Alice", null, isAdmin: true, default)).Ok().IsAdmin);
        Assert.Single(db.Persons);
    }

    [Fact]
    public async Task FindBySubject_returns_the_bound_adult_and_nothing_for_an_unknown_subject()
    {
        await using var db = await postgres.NewDatabaseAsync();
        var registry = new PersonRegistry(db, PostgresFixture.Clock);
        var alice = (await registry.ProvisionAdultAsync("sub-a", "Alice", null, isAdmin: true, default)).Ok();

        Assert.Equal(alice.Id, (await registry.FindBySubjectAsync("sub-a", default))!.Id);
        Assert.Null(await registry.FindBySubjectAsync("sub-x", default));
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
    public async Task A_known_subjects_new_email_claim_matching_another_adult_keeps_its_old_email()
    {
        await using var db = await postgres.NewDatabaseAsync();
        var registry = new PersonRegistry(db, PostgresFixture.Clock);
        await registry.ProvisionAdultAsync("sub-a", "Alice", "alice@example.test", default);
        await registry.PreRegisterAdultAsync("Bob", "bob@example.test", default);
        Assert.True((await registry.ProvisionAdultAsync("sub-b", "Bob", "bob@example.test", default)).TryGetValue(out var bob));

        Assert.True((await registry.ProvisionAdultAsync("sub-b", "Bob", "ALICE@Example.test", default)).TryGetValue(out var updated));

        Assert.Equal(bob.Email, updated.Email);
    }

    [Fact]
    public async Task An_unknown_identity_the_provider_let_through_becomes_an_adult_under_the_cap()
    {
        await using var db = await postgres.NewDatabaseAsync();
        var registry = new PersonRegistry(db, PostgresFixture.Clock);
        await registry.ProvisionAdultAsync("sub-a", "Alice", "alice@example.test", default);

        var newcomer = await registry.ProvisionAdultAsync("sub-b", "Bob", "bob@example.test", isAdmin: true, default);

        Assert.True(newcomer.TryGetValue(out var bob));
        Assert.Equal(("Bob", "bob@example.test", true, false), (bob.DisplayName, bob.Email, bob.IsAdmin, bob.IsPending));
        Assert.Equal(2, db.Persons.Count());
    }

    [Fact]
    public async Task A_new_adult_beyond_the_cap_is_refused_and_nothing_is_written()
    {
        await using var db = await postgres.NewDatabaseAsync();
        var registry = new PersonRegistry(db, PostgresFixture.Clock);
        await registry.ProvisionAdultAsync("sub-a", "Alice", null, default);
        await registry.ProvisionAdultAsync("sub-b", "Bob", null, default);

        Assert.Equal(PersonCodes.AdultCap, Code(await registry.ProvisionAdultAsync("sub-c", "Carol", null, default)));
        Assert.Equal(2, db.Persons.Count());

        await db.HouseholdSettings.ExecuteUpdateAsync(s => s.SetProperty(x => x.MaxAdults, 3));
        Assert.True((await registry.ProvisionAdultAsync("sub-c", "Carol", null, default)).IsSuccess);
    }

    [Fact]
    public async Task An_email_another_adult_holds_under_another_subject_is_refused_not_taken_over()
    {
        await using var db = await postgres.NewDatabaseAsync();
        var registry = new PersonRegistry(db, PostgresFixture.Clock);
        await registry.ProvisionAdultAsync("sub-a", "Alice", "alice@example.test", default);

        // The same person with a new subject (the provider was reinstalled), or someone else: an admin rebinds.
        var result = await registry.ProvisionAdultAsync("sub-new", "Alice", "ALICE@example.test", isAdmin: true, default);

        Assert.Equal(PersonCodes.EmailTaken, Code(result));
        Assert.Single(db.Persons);
        Assert.Equal("sub-a", db.Persons.Single().OidcSubject);
    }

    [Fact]
    public async Task A_preregistered_adult_is_bound_by_email_whatever_the_case()
    {
        await using var db = await postgres.NewDatabaseAsync();
        var registry = new PersonRegistry(db, PostgresFixture.Clock);
        await registry.ProvisionAdultAsync("sub-a", "Alice", null, default);

        Assert.True((await registry.PreRegisterAdultAsync(" Bob ", " bob@example.test ", default)).TryGetValue(out var pending));
        Assert.Equal(("Bob", "bob@example.test", true, false), (pending.DisplayName, pending.Email, pending.IsPending, pending.IsAdmin));

        Assert.True((await registry.ProvisionAdultAsync("sub-b", "Bob B.", "BOB@Example.test", isAdmin: true, default)).TryGetValue(out var bound));
        Assert.Equal((pending.Id, "Bob B.", false, true), (bound.Id, bound.DisplayName, bound.IsPending, bound.IsAdmin));
        Assert.Equal(2, db.Persons.Count());
    }

    [Fact]
    public async Task Without_an_email_claim_no_pending_adult_is_bound_and_a_new_adult_is_created()
    {
        await using var db = await postgres.NewDatabaseAsync();
        var registry = new PersonRegistry(db, PostgresFixture.Clock);
        await registry.ProvisionAdultAsync("sub-a", "Alice", null, default);
        await registry.PreRegisterAdultAsync("Bob", "bob@example.test", default);
        await db.HouseholdSettings.ExecuteUpdateAsync(s => s.SetProperty(x => x.MaxAdults, 3));

        var anonymous = (await registry.ProvisionAdultAsync("sub-b", "Bob", null, default)).Ok();

        Assert.False(anonymous.IsPending);
        Assert.Null(anonymous.Email);
        Assert.Equal(3, db.Persons.Count());
        Assert.Single(db.Persons, p => p.OidcSubject == null);
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
    public async Task Simultaneous_first_logins_never_exceed_the_cap()
    {
        var connectionString = await postgres.NewConnectionStringAsync();

        var attempts = Enumerable.Range(0, 6).Select(async i =>
        {
            await using var db = PostgresFixture.NewCoreContext(connectionString);
            return await new PersonRegistry(db, PostgresFixture.Clock)
                .ProvisionAdultAsync($"sub-{i}", $"Person {i}", $"person{i}@example.test", isAdmin: i == 0, default);
        });
        var results = await Task.WhenAll(attempts);

        Assert.Equal(2, results.Count(r => r.IsSuccess));
        Assert.Equal(4, results.Count(r => Code(r) == PersonCodes.AdultCap));
        await using var probe = PostgresFixture.NewCoreContext(connectionString);
        Assert.Equal(2, await probe.Persons.CountAsync(p => p.Kind == "adult"));
        Assert.Equal(1, await probe.HouseholdSettings.CountAsync());
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
        var admin = (await registry.ProvisionAdultAsync("sub-a", "Alice", "alice@example.test", isAdmin: true, default)).Ok();
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
