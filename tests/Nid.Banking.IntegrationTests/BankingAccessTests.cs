using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nid.Banking.Application;
using Nid.Banking.Contracts;
using Nid.Banking.Infrastructure.Persistence;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.TestSupport;
using NodaTime;
using UnambitiousFx.Functional;

namespace Nid.Banking.IntegrationTests;

/// <summary>
/// #414 step 2: an adult reaches Enable Banking with their own application only; a connection is read with its creator's application only,
/// when its id is theirs. No installation application to fall back on.
/// </summary>
public class BankingAccessTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static BankConnection Connection(PersonId person, string? applicationId)
        => new(Guid.NewGuid(), person, "enable_banking", "Banque Alpha", "FR", ConnectionStatus.Active, null, null, null, 0, Instant.MinValue, applicationId);

    private static async Task<T> AsAsync<T>(NidApp app, PersonId person, Func<BankingAccess, Task<T>> act)
    {
        await using var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.Browser(person);
        return await act(scope.ServiceProvider.GetRequiredService<BankingAccess>());
    }

    private static string? Code<T>(Result<T> result) where T : notnull => result.TryGetFailure(out var failure) ? failure!.Code : null;

    [Fact]
    public async Task Her_own_application_is_the_one_she_and_her_connections_use()
    {
        await using var app = await BankingApi.StartAsync(fixture);
        await BankingApi.ApplicationAsync(app, app.AdultA, "app-alice");

        var mine = await AsAsync(app, app.AdultA, a => a.MyAppAsync(Ct));
        var read = await AsAsync(app, app.AdultB, a => a.AppForAsync(Connection(app.AdultA, "app-alice"), Ct));

        Assert.Equal("app-alice", mine.TryGetValue(out var own) ? own!.Id : null);
        Assert.Equal("app-alice", read.TryGetValue(out var used) ? used!.Id : null);
        Assert.True(await AsAsync(app, app.AdultA, a => a.ConfiguredAsync(app.AdultA, Ct)));
    }

    [Fact]
    public async Task Without_an_application_nothing_is_configured()
    {
        await using var app = await BankingApi.StartAsync(fixture, enable: false);
        await BankingApi.SettingsAsync(app, new { bankingEnabled = true });

        Assert.Equal(BankingCodes.NotConfigured, Code(await AsAsync(app, app.AdultA, a => a.MyAppAsync(Ct))));
        Assert.Equal(BankingCodes.NotConfigured, Code(await AsAsync(app, app.AdultA, a => a.AppForAsync(Connection(app.AdultA, null), Ct))));
        Assert.False(await AsAsync(app, app.AdultA, a => a.ConfiguredAsync(app.AdultA, Ct)));
        Assert.False(await AsAsync(app, app.AdultA, a => a.AnyApplicationAsync(Ct)));
    }

    [Fact]
    public async Task An_unreadable_key_is_not_configured()
    {
        await using var app = await BankingApi.StartAsync(fixture);
        await using (var db = new BankingDbContext(new DbContextOptionsBuilder<BankingDbContext>().Configure(app.ConnectionString).Options))
        {
            // As after the Data Protection key ring was lost: the stored bytes no longer decrypt.
            var row = await db.Applications.SingleAsync(a => a.PersonId == app.AdultA.Value, Ct);
            row.PrivateKey = [1, 2, 3];
            await db.SaveChangesAsync(Ct);
        }

        Assert.False(await AsAsync(app, app.AdultA, a => a.ConfiguredAsync(app.AdultA, Ct)));
        Assert.Equal(BankingCodes.NotConfigured, Code(await AsAsync(app, app.AdultA, a => a.MyAppAsync(Ct))));
        Assert.Equal(BankingCodes.NotConfigured, Code(await AsAsync(app, app.AdultA, a => a.AppForAsync(Connection(app.AdultA, "app-1"), Ct))));
        Assert.True(await AsAsync(app, app.AdultA, a => a.ConfiguredAsync(app.AdultB, Ct)));
    }

    [Fact]
    public async Task A_connection_made_with_another_application_is_not_read()
    {
        await using var app = await BankingApi.StartAsync(fixture);
        await BankingApi.ApplicationAsync(app, app.AdultB, "app-bob");

        // Made with the former installation application (or none), or with Bob's: never Alice's « app-1 ».
        Assert.Equal(BankingCodes.NotConfigured, Code(await AsAsync(app, app.AdultA, a => a.AppForAsync(Connection(app.AdultA, "app-home"), Ct))));
        Assert.Equal(BankingCodes.NotConfigured, Code(await AsAsync(app, app.AdultA, a => a.AppForAsync(Connection(app.AdultA, null), Ct))));
        Assert.Equal(BankingCodes.NotConfigured, Code(await AsAsync(app, app.AdultA, a => a.AppForAsync(Connection(app.AdultA, "app-bob"), Ct))));
    }
}
