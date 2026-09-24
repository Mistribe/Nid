using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Nid.Core.Infrastructure.Persistence;
using Nid.TestSupport;
using Npgsql;

namespace Nid.Core.IntegrationTests;

[Collection("postgres")]
public class AdminSchemaTests(PostgresFixture postgres)
{
    /// <summary>The migration just before this sub-plan's: a household that already has its adults.</summary>
    private const string BeforeAdminArea = "20260924074725_FxRateQuoteIndex";

    [Fact]
    public async Task The_migration_makes_the_oldest_adult_admin_and_no_one_else()
    {
        var connectionString = await postgres.NewEmptyConnectionStringAsync();
        await using var db = PostgresFixture.NewCoreContext(connectionString);
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync(BeforeAdminArea);

        // ExecuteSqlRawAsync runs the text through string.Format even with no parameters, so a literal '{}' must
        // be escaped as '{{}}'.
        await db.Database.ExecuteSqlRawAsync("""
            insert into core.person (id, kind, oidc_subject, display_name, email, session_epoch, preferences, created_at) values
              ('01920000-0000-7000-8000-000000000002', 'adult', 'sub-b', 'Bob', null, 0, '{{}}', '2026-01-02T00:00:00Z'),
              ('01920000-0000-7000-8000-000000000001', 'adult', 'sub-a', 'Alice', null, 0, '{{}}', '2026-01-01T00:00:00Z'),
              ('01920000-0000-7000-8000-000000000003', 'dependent', null, 'Kid', null, 0, '{{}}', '2025-12-01T00:00:00Z');
            """);

        await migrator.MigrateAsync();

        Assert.Equal(["Alice"], await db.Persons.Where(p => p.IsAdmin).Select(p => p.DisplayName).ToListAsync());
    }

    [Fact]
    public async Task A_pending_adult_needs_an_email_and_a_dependent_is_never_admin()
    {
        await using var db = await postgres.NewDatabaseAsync();

        // session_epoch and preferences have no database default, so every raw insert lists them.
        var noSubjectNoEmail = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync("""
            insert into core.person (id, kind, display_name, session_epoch, preferences, created_at) values
              ('01920000-0000-7000-8000-000000000010', 'adult', 'Nobody', 0, '{{}}', now());
            """));
        Assert.Equal("ck_person_subject", noSubjectNoEmail.ConstraintName);

        var adminKid = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync("""
            insert into core.person (id, kind, display_name, is_admin, session_epoch, preferences, created_at) values
              ('01920000-0000-7000-8000-000000000011', 'dependent', 'Kid', true, 0, '{{}}', now());
            """));
        Assert.Equal("ck_person_admin", adminKid.ConstraintName);
    }

    [Fact]
    public async Task Two_adults_cannot_share_an_email_whatever_its_case()
    {
        await using var db = await postgres.NewDatabaseAsync();
        // session_epoch and preferences have no database default, so every raw insert lists them.
        await db.Database.ExecuteSqlRawAsync("""
            insert into core.person (id, kind, display_name, email, session_epoch, preferences, created_at) values
              ('01920000-0000-7000-8000-000000000020', 'adult', 'Carol', 'carol@example.test', 0, '{{}}', now());
            """);

        var duplicate = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync("""
            insert into core.person (id, kind, display_name, email, session_epoch, preferences, created_at) values
              ('01920000-0000-7000-8000-000000000021', 'adult', 'Carol again', 'CAROL@Example.test', 0, '{{}}', now());
            """));
        Assert.Equal("ix_person_adult_email", duplicate.ConstraintName);
    }

    [Fact]
    public async Task The_adult_cap_defaults_to_two_and_is_at_least_one()
    {
        await using var db = await postgres.NewDatabaseAsync();
        db.HouseholdSettings.Add(new HouseholdSettingsRow());
        await db.SaveChangesAsync();

        Assert.Equal(2, await db.HouseholdSettings.Select(s => s.MaxAdults).SingleAsync());
        var zero = await Assert.ThrowsAsync<PostgresException>(
            () => db.Database.ExecuteSqlRawAsync("update core.household_settings set max_adults = 0"));
        Assert.Equal("ck_household_settings_max_adults", zero.ConstraintName);
    }
}
