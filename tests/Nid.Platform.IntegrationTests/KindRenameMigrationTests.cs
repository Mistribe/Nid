using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Nid.TestSupport;
using Npgsql;

namespace Nid.Platform.IntegrationTests;

/// <summary>
/// #645: a household's notifications, household deliveries and preferences, stored under the legacy kind names by the previous image,
/// come out of the migration under the prefixed names with everything else as it was; <c>Down</c> puts the old names back.
/// </summary>
public class KindRenameMigrationTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    /// <summary>Platform's last migration before the rename: the previous image's schema.</summary>
    private const string Before = "20261117094018_NotificationSignals81";

    private const string Person = "01900000-0000-7000-8000-00000000000a";
    private const string Item = "01900000-0000-7000-8000-0000000000b1";

    private static readonly string Seed = $$"""
        insert into platform.notification (id, person_id, kind, key, level, item_ids, notify_on, params, state, created_at, read_at, round)
        values
          ('01900000-0000-7000-8000-000000000001', '{{Person}}', 'loan_end', 'loan_end:{{Item}}:2027-03-31', 'info', '{{{Item}}}', '2027-03-01', '{}', 'open', '2027-03-01T08:00:00Z', null, 1),
          ('01900000-0000-7000-8000-000000000002', '{{Person}}', 'rent_arrears', 'rent_arrears:{{Item}}:2027-02', 'action', '{{{Item}}}', '2027-02-10', '{}', 'snoozed', '2027-02-10T08:00:00Z', '2027-02-11T08:00:00Z', 2),
          ('01900000-0000-7000-8000-000000000003', '{{Person}}', 'job_failed', 'job_failed:fx-daily', 'action', '{}', '2027-02-10', '{"job": "fx-daily"}', 'resolved', '2027-02-10T08:00:00Z', null, 1),
          ('01900000-0000-7000-8000-000000000004', '{{Person}}', 'subscriptions.ends_without_renewal', 'subscriptions.ends_without_renewal:{{Item}}', 'info', '{{{Item}}}', '2027-02-10', '{}', 'open', '2027-02-10T08:00:00Z', null, 1);
        insert into platform.notification_delivery (id, notification_id, person_id, channel, round, state, attempts, created_at, household_key)
        values
          ('01900000-0000-7000-8000-000000000011', '01900000-0000-7000-8000-000000000001', '{{Person}}', 'discord_household', 1, 'sent', 1, '2027-03-01T08:00:00Z', 'loan_end:{{Item}}:2027-03-31'),
          ('01900000-0000-7000-8000-000000000012', '01900000-0000-7000-8000-000000000003', '{{Person}}', 'discord_household', 1, 'sent', 1, '2027-02-10T08:00:00Z', 'job_failed:fx-daily@123'),
          ('01900000-0000-7000-8000-000000000013', '01900000-0000-7000-8000-000000000001', '{{Person}}', 'email', 1, 'pending', 0, '2027-03-01T08:00:00Z', null);
        insert into platform.notification_preferences (person_id, email_enabled, action_mode, info_mode, frequency, weekday, lead_days, signals, version)
        values ('{{Person}}', true, 'immediate', 'digest', 'weekly', 1, '{"tenancy_end": 60, "loan_end": 10}', '{"net_worth_milestone": 100000, "card_unused": 3}', 4);
        """;

    private const string Rows = """
        select kind || ' ' || key || ' ' || state || ' ' || round from platform.notification order by id
        """;

    private const string HouseholdKeys = "select coalesce(household_key, '-') from platform.notification_delivery order by id";

    private const string Preferences = "select lead_days::text || ' ' || signals::text || ' ' || version from platform.notification_preferences";

    [Fact]
    public async Task A_households_notifications_and_preferences_take_the_prefixed_names()
    {
        var ct = TestContext.Current.CancellationToken;
        var connectionString = await fixture.NewEmptyConnectionStringAsync();
        await using var platform = PostgresFixture.NewPlatformContext(connectionString);
        await platform.GetService<IMigrator>().MigrateAsync(Before, ct);
        await ExecuteAsync(connectionString, Seed, ct);

        await platform.Database.MigrateAsync(ct);

        Assert.Equal([
            $"realestate.loan_end realestate.loan_end:{Item}:2027-03-31 open 1",
            $"realestate.rent_arrears realestate.rent_arrears:{Item}:2027-02 snoozed 2",
            "platform.job_failed platform.job_failed:fx-daily resolved 1",
            $"subscriptions.ends_without_renewal subscriptions.ends_without_renewal:{Item} open 1",
        ], await QueryAsync(connectionString, Rows, ct));
        Assert.Equal([$"realestate.loan_end:{Item}:2027-03-31", "platform.job_failed:fx-daily@123", "-"], await QueryAsync(connectionString, HouseholdKeys, ct));
        Assert.Equal(["""{"realestate.loan_end": 10, "realestate.tenancy_end": 60} {"banking.card_unused": 3, "core.net_worth_milestone": 100000} 4"""],
            await QueryAsync(connectionString, Preferences, ct));
        Assert.False(platform.Database.HasPendingModelChanges());
    }

    [Fact]
    public async Task Down_puts_the_legacy_names_back()
    {
        var ct = TestContext.Current.CancellationToken;
        var connectionString = await fixture.NewEmptyConnectionStringAsync();
        await using var platform = PostgresFixture.NewPlatformContext(connectionString);
        await platform.GetService<IMigrator>().MigrateAsync(Before, ct);
        await ExecuteAsync(connectionString, Seed, ct);
        string[] queries = [Rows, HouseholdKeys, Preferences];
        var seeded = new List<List<string>>();
        foreach (var query in queries)
            seeded.Add(await QueryAsync(connectionString, query, ct));

        await platform.Database.MigrateAsync(ct);
        await platform.GetService<IMigrator>().MigrateAsync(Before, ct);

        for (var i = 0; i < queries.Length; i++)
            Assert.Equal(seeded[i], await QueryAsync(connectionString, queries[i], ct));
    }

    private static async Task ExecuteAsync(string connectionString, string sql, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<List<string>> QueryAsync(string connectionString, string sql, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<string>();
        while (await reader.ReadAsync(ct))
            rows.Add(reader.GetString(0));
        return rows;
    }
}
