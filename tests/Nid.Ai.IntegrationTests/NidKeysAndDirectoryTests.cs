using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nid.Ai.Application;
using Nid.Ai.Infrastructure.Persistence;
using Nid.Ai.Infrastructure.Workers;
using Nid.Platform.Contracts;
using Nid.TestSupport;
using NodaTime;
using UnambitiousFx.Myelin;

namespace Nid.Ai.IntegrationTests;

public sealed class NidKeysAndDirectoryTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task One_active_key_is_created_and_never_stored_in_clear()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await using var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
        var keys = scope.ServiceProvider.GetRequiredService<INidKeys>();

        var first = await keys.ActiveAsync(TestContext.Current.CancellationToken);
        var again = await keys.ActiveAsync(TestContext.Current.CancellationToken);

        Assert.Equal(first.Id, again.Id);
        var row = await scope.ServiceProvider.GetRequiredService<AiDbContext>().Keys.AsNoTracking()
            .SingleAsync(TestContext.Current.CancellationToken);
        var clear = first.Key.ExportPkcs8PrivateKey();
        Assert.False(row.PrivateKey.AsSpan().IndexOf(clear.AsSpan(clear.Length - 32)) >= 0);
        Assert.Equal(first.Key.ExportSubjectPublicKeyInfo(), row.PublicKey);
    }

    [Fact]
    public async Task A_rotation_leaves_one_active_key_and_a_retiring_one()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await using var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
        var keys = scope.ServiceProvider.GetRequiredService<INidKeys>();
        var first = await keys.ActiveAsync(TestContext.Current.CancellationToken);

        var next = await keys.RotateAsync(TestContext.Current.CancellationToken);

        Assert.NotEqual(first.Id, next.Id);
        Assert.Equal(next.Id, (await keys.ActiveAsync(TestContext.Current.CancellationToken)).Id);
        Assert.False((await keys.FindAsync(first.Id, TestContext.Current.CancellationToken))!.Active);

        await keys.RetireUnusedAsync(TestContext.Current.CancellationToken);
        Assert.Null(await keys.FindAsync(first.Id, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Registered_endpoints_match_host_and_port_and_drop_a_revoked_worker_at_once()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var registered = app.Services.GetRequiredService<IRegisteredEndpoints>();
        Guid id;
        await using (var scope = app.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
            var db = scope.ServiceProvider.GetRequiredService<AiDbContext>();
            var row = Row("box", 8443, "active");
            id = row.Id;
            db.Workers.Add(row);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        app.Services.GetRequiredService<IWorkerDirectory>().Invalidate();

        Assert.True(await registered.IsRegisteredAsync("box", 8443, TestContext.Current.CancellationToken));
        Assert.False(await registered.IsRegisteredAsync("box", 9443, TestContext.Current.CancellationToken));

        await using (var scope = app.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
            var db = scope.ServiceProvider.GetRequiredService<AiDbContext>();
            var row = await db.Workers.SingleAsync(w => w.Id == id, TestContext.Current.CancellationToken);
            row.Status = "revoked";
            row.RevokedAt = Instant.FromUnixTimeSeconds(1);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        app.Services.GetRequiredService<IWorkerDirectory>().Invalidate();

        Assert.False(await registered.IsRegisteredAsync("box", 8443, TestContext.Current.CancellationToken));
    }

    private static ModelWorkerRow Row(string host, int port, string status) => new()
    {
        Id = Guid.CreateVersion7(), Name = "GPU box", Host = host, Port = port, Placement = "local", Status = status,
        WorkerPublicKey = new byte[65], WorkerKeyFingerprint = new string('a', 64), CreatedAt = Instant.FromUnixTimeSeconds(0),
    };
}
