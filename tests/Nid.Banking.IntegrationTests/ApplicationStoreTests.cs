using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nid.Banking.Application;
using Nid.Banking.Contracts;
using Nid.Banking.Infrastructure.Persistence;
using Nid.Platform.Contracts;
using Nid.TestSupport;
using NodaTime;
using Duration = NodaTime.Duration;

namespace Nid.Banking.IntegrationTests;

/// <summary>#382: each adult's Enable Banking application, its key encrypted at rest, and the connections made with it.</summary>
public class ApplicationStoreTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static readonly Instant Now = NidApp.GoldenCaseToday.AtMidnight().InUtc().ToInstant();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string NewPem() => RSA.Create(2048).ExportRSAPrivateKeyPem();

    private static BankingDbContext Db(NidApp app) => new(new DbContextOptionsBuilder<BankingDbContext>().Configure(app.ConnectionString).Options);

    private static AsyncServiceScope Scope(NidApp app)
    {
        var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
        return scope;
    }

    [Fact]
    public async Task A_keyless_save_of_a_missing_application_loses_the_race_instead_of_throwing()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await using var scope = Scope(app);
        var store = scope.ServiceProvider.GetRequiredService<IApplicationStore>();

        Assert.False(await store.SaveAsync(app.AdultA, "app-alice", null, Ct));
        Assert.True(await store.DeleteAsync(app.AdultA, Ct));
    }

    [Fact]
    public async Task An_application_is_saved_found_and_its_key_decrypted()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await using var scope = Scope(app);
        var store = scope.ServiceProvider.GetRequiredService<IApplicationStore>();
        var pem = NewPem();

        Assert.Null(await store.FindAsync(app.AdultA, Ct));
        Assert.Null(await store.CredentialAsync(app.AdultA, Ct));
        await store.SaveAsync(app.AdultA, "app-alice", pem, Ct);

        var found = await store.FindAsync(app.AdultA, Ct);
        Assert.Equal(("app-alice", true, Now), (found!.ApplicationId, found.KeyReadable, found.KeyLoadedAt));
        Assert.Equal(new EnableBankingApp("app-alice", pem), await store.CredentialAsync(app.AdultA, Ct));
        Assert.Equal([app.AdultA], await store.OwnersAsync(Ct));
        Assert.Null(await store.FindAsync(app.AdultB, Ct));

        await store.DeleteAsync(app.AdultA, Ct);
        Assert.Null(await store.FindAsync(app.AdultA, Ct));
    }

    [Fact]
    public async Task Saving_without_a_key_changes_the_id_and_keeps_the_key()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await using var scope = Scope(app);
        var store = scope.ServiceProvider.GetRequiredService<IApplicationStore>();
        var pem = NewPem();
        await store.SaveAsync(app.AdultA, "app-alice", pem, Ct);

        await store.SaveAsync(app.AdultA, "app-alice-2", null, Ct);

        Assert.Equal(new EnableBankingApp("app-alice-2", pem), await store.CredentialAsync(app.AdultA, Ct));
    }

    [Fact]
    public async Task The_key_is_encrypted_at_rest_and_a_tampered_one_reads_as_unreadable_and_is_never_audited()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await using var scope = Scope(app);
        var store = scope.ServiceProvider.GetRequiredService<IApplicationStore>();
        await store.SaveAsync(app.AdultA, "app-alice", NewPem(), Ct);

        await using var db = Db(app);
        var stored = await db.Applications.AsNoTracking().Select(a => a.PrivateKey).SingleAsync(Ct);
        Assert.DoesNotContain("PRIVATE KEY", Encoding.UTF8.GetString(stored));

        var audit = await db.Database.SqlQuery<string>($"select data::text as \"Value\" from platform.audit_log where entity = 'banking.application'").ToListAsync(Ct);
        Assert.NotEmpty(audit);
        Assert.DoesNotContain(audit, d => d.Contains("private_key"));

        var tampered = stored.ToArray();
        tampered[^1] ^= 0xFF;
        await db.Database.ExecuteSqlAsync($"update banking.application set private_key = {tampered}", Ct);

        var found = await store.FindAsync(app.AdultA, Ct);
        Assert.False(found!.KeyReadable);
        Assert.Null(await store.CredentialAsync(app.AdultA, Ct));
    }

    [Fact]
    public async Task An_application_is_in_use_by_a_connection_past_its_pending_state()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await using var scope = Scope(app);
        var connections = scope.ServiceProvider.GetRequiredService<IConnectionStore>();
        var pending = await connections.CreatePendingAsync(app.AdultA, "app-alice", "Boursorama", "FR", [1], Now + Duration.FromMinutes(15), Ct);

        Assert.False(await connections.InUseAsync("app-alice", null, Ct));

        await connections.ActivateAsync(pending, "session-1", Now + Duration.FromDays(180), Ct);

        Assert.True(await connections.InUseAsync("app-alice", null, Ct));
        Assert.True(await connections.InUseAsync("app-alice", app.AdultA, Ct));
        Assert.False(await connections.InUseAsync("app-alice", app.AdultB, Ct));
        Assert.False(await connections.InUseAsync("app-bob", null, Ct));
        Assert.Equal("app-alice", (await connections.MineAsync(app.AdultA, Ct)).Single().ApplicationId);
    }
}
