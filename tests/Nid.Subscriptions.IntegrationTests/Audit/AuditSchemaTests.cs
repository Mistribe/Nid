using Microsoft.EntityFrameworkCore;
using Nid.Platform.Infrastructure.Audit;
using Nid.TestSupport;
using Npgsql;
using NodaTime;

namespace Nid.Subscriptions.IntegrationTests.Audit;

/// <summary>Spec §3: one append-only table, owned by Platform, that every module's context can write.</summary>
[Collection("postgres")]
public class AuditSchemaTests(PostgresFixture fixture)
{
    private static AuditEntryRow Entry(Guid item) => new()
    {
        Id = Guid.CreateVersion7(), At = SystemClock.Instance.GetCurrentInstant(), Category = "write", Action = "insert",
        ActorKind = "system", Via = "job", Scopes = ["read"], Entity = "core.item", EntityKey = item.ToString(),
        ItemIds = [item], Data = """{"before":null,"after":{}}""",
    };

    [Fact]
    public async Task Each_module_context_writes_the_table_platform_owns()
    {
        var connection = await fixture.NewConnectionStringAsync();
        var fromCore = Guid.CreateVersion7();
        var fromSubscriptions = Guid.CreateVersion7();

        await using (var core = PostgresFixture.NewCoreContext(connection))
        {
            core.Set<AuditEntryRow>().Add(Entry(fromCore));
            await core.SaveChangesAsync();
        }
        await using (var subs = PostgresFixture.NewSubscriptionsContext(connection))
        {
            subs.Set<AuditEntryRow>().Add(Entry(fromSubscriptions));
            await subs.SaveChangesAsync();
        }

        await using var platform = PostgresFixture.NewPlatformContext(connection);
        var rows = await platform.AuditLog.OrderBy(r => r.Seq).ToListAsync();
        Assert.Equal([fromCore, fromSubscriptions], rows.Select(r => r.ItemIds.Single()));
        Assert.True(rows[0].Seq < rows[1].Seq);
    }

    [Theory]
    [InlineData("update platform.audit_log set action = 'forged'")]
    [InlineData("delete from platform.audit_log")]
    [InlineData("truncate platform.audit_log")]
    public async Task The_log_refuses_to_be_rewritten(string statement)
    {
        var connection = await fixture.NewConnectionStringAsync();
        await using var core = PostgresFixture.NewCoreContext(connection);
        core.Set<AuditEntryRow>().Add(Entry(Guid.CreateVersion7()));
        await core.SaveChangesAsync();

        var refused = await Assert.ThrowsAsync<PostgresException>(() => core.Database.ExecuteSqlRawAsync(statement));
        Assert.Contains("append-only", refused.MessageText);
    }

    [Fact]
    public async Task No_context_has_model_changes_left_for_a_migration()
    {
        var connection = await fixture.NewConnectionStringAsync();
        await using var core = PostgresFixture.NewCoreContext(connection);
        await using var subs = PostgresFixture.NewSubscriptionsContext(connection);
        await using var platform = PostgresFixture.NewPlatformContext(connection);

        Assert.False(core.Database.HasPendingModelChanges());
        Assert.False(subs.Database.HasPendingModelChanges());
        Assert.False(platform.Database.HasPendingModelChanges());
    }
}
