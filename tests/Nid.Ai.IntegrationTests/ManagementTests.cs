using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nid.Ai.Application;
using Nid.Ai.Contracts;
using Nid.Ai.Infrastructure.Persistence;
using Nid.Ai.Protocol.Keys;
using Nid.Platform.Contracts;
using Nid.TestSupport;
using NodaTime;
using UnambitiousFx.Myelin;

namespace Nid.Ai.IntegrationTests;

public sealed class ManagementTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task A_worker_is_renamed_disabled_enabled_and_revoked()
    {
        await using var world = await PairedWorld.StartAsync(fixture);
        var worker = await world.PairAsync();

        Assert.Equal("Box", (await Send<WorkerView>(world, HttpMethod.Patch, $"/api/v1/ai/workers/{worker.Id}", new { name = "Box" })).Name);
        Assert.Equal("disabled", (await Send<WorkerView>(world, HttpMethod.Post, $"/api/v1/ai/workers/{worker.Id}/disable")).Status);
        Assert.False(await world.Nid.Services.GetRequiredService<IRegisteredEndpoints>().IsRegisteredAsync("worker.test", 8443, TestContext.Current.CancellationToken));
        Assert.Equal("active", (await Send<WorkerView>(world, HttpMethod.Post, $"/api/v1/ai/workers/{worker.Id}/enable")).Status);

        var revoked = await world.Nid.RawAsync(world.Nid.AdultA, HttpMethod.Post, $"/api/v1/ai/workers/{worker.Id}/revoke");
        Assert.Equal(HttpStatusCode.OK, revoked.StatusCode);
        Assert.Empty((await world.ListAsync()).Workers);
        Assert.False(await world.Nid.Services.GetRequiredService<IRegisteredEndpoints>().IsRegisteredAsync("worker.test", 8443, TestContext.Current.CancellationToken));

        var again = await world.Nid.RawAsync(world.Nid.AdultA, HttpMethod.Post, $"/api/v1/ai/workers/{worker.Id}/enable");
        Assert.Contains(AiCodes.WorkerRevoked, await again.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_revoked_worker_is_refused_without_a_call()
    {
        await using var world = await PairedWorld.StartAsync(fixture);
        var worker = await world.PairAsync();
        await world.Nid.RawAsync(world.Nid.AdultA, HttpMethod.Post, $"/api/v1/ai/workers/{worker.Id}/revoke");
        await world.Worker.DisposeAsync(); // nothing is listening: a call would fail as unreachable, not as revoked

        var refresh = await world.Nid.RawAsync(world.Nid.AdultA, HttpMethod.Post, $"/api/v1/ai/workers/{worker.Id}/refresh");

        Assert.Contains(AiCodes.WorkerRevoked, await refresh.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Rotating_a_worker_key_keeps_the_old_one_for_a_day()
    {
        await using var world = await PairedWorld.StartAsync(fixture);
        var worker = await world.PairAsync();
        var before = worker.KeyFingerprint;

        var rotated = await Send<WorkerView>(world, HttpMethod.Post, $"/api/v1/ai/workers/{worker.Id}/rotate-key");

        Assert.NotEqual(before, rotated.KeyFingerprint);
        Assert.Equal(rotated.KeyFingerprint, KeyFingerprint.Of(WorkerPublicKey(world)));
        await using var scope = world.Nid.Services.CreateAsyncScope();
        var row = await scope.ServiceProvider.GetRequiredService<AiDbContext>().Workers.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(row.PreviousWorkerPublicKey);
        Assert.Equal(KeyFingerprint.Compact(row.PreviousWorkerPublicKey!), before.Replace(" ", ""));
        Assert.Equal(Duration.FromHours(24), row.PreviousWorkerKeyUntil - row.LastSeenAt);
        Assert.Equal("active", (await Send<WorkerView>(world, HttpMethod.Post, $"/api/v1/ai/workers/{worker.Id}/refresh")).Status);
    }

    [Fact]
    public async Task A_nid_rotation_is_adopted_after_confirmation_and_refresh()
    {
        await using var world = await PairedWorld.StartAsync(fixture);
        var worker = await world.PairAsync();

        var rotation = await Send<NidKeyRotation>(world, HttpMethod.Post, "/api/v1/ai/key/rotate");
        Assert.Empty(rotation.Unreached);
        Assert.NotEqual(worker.NidKeyFingerprint, rotation.Fingerprint.Replace(" ", ""));

        // Before the operator confirms, the worker still answers to the old key and Nid keeps using it.
        Assert.Equal(worker.NidKeyFingerprint, (await Send<WorkerView>(world, HttpMethod.Post, $"/api/v1/ai/workers/{worker.Id}/refresh")).NidKeyFingerprint);

        Assert.Equal(0, world.Worker.Run("confirm-rotation", rotation.Fingerprint));
        var refreshed = await Send<WorkerView>(world, HttpMethod.Post, $"/api/v1/ai/workers/{worker.Id}/refresh");

        Assert.Equal(rotation.Fingerprint.Replace(" ", ""), refreshed.NidKeyFingerprint);
        Assert.False(refreshed.NidKeyRotationOverdue);
        await using var scope = world.Nid.Services.CreateAsyncScope();
        Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<AiDbContext>().Keys.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_nid_rotation_names_the_disabled_workers_it_could_not_tell()
    {
        await using var world = await PairedWorld.StartAsync(fixture);
        var worker = await world.PairAsync();
        await Send<WorkerView>(world, HttpMethod.Post, $"/api/v1/ai/workers/{worker.Id}/disable");

        var rotation = await Send<NidKeyRotation>(world, HttpMethod.Post, "/api/v1/ai/key/rotate");

        Assert.Equal([worker.Id], rotation.Unreached);
    }

    [Fact]
    public async Task An_unconfirmed_nid_rotation_is_flagged_after_seven_days()
    {
        await using var world = await PairedWorld.StartAsync(fixture);
        var worker = await world.PairAsync();
        await Send<NidKeyRotation>(world, HttpMethod.Post, "/api/v1/ai/key/rotate");
        await using (var scope = world.Nid.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AiDbContext>();
            await db.Keys.Where(k => k.Status == "active")
                .ExecuteUpdateAsync(s => s.SetProperty(k => k.CreatedAt, k => k.CreatedAt - Duration.FromDays(8)), TestContext.Current.CancellationToken);
        }

        Assert.True(Assert.Single((await world.ListAsync()).Workers).NidKeyRotationOverdue);
        _ = worker;
    }

    [Fact]
    public async Task Managing_a_worker_is_for_an_admin()
    {
        await using var world = await PairedWorld.StartAsync(fixture);
        var worker = await world.PairAsync();

        var refused = await world.Nid.RawAsync(world.Nid.AdultB, HttpMethod.Post, $"/api/v1/ai/workers/{worker.Id}/revoke");

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
    }

    private static byte[] WorkerPublicKey(PairedWorld world)
    {
        using var key = Protocol.Sealing.WorkerKeyPair.Import(Convert.FromBase64String(world.Worker.State.Load().WorkerKey));
        return key.PublicKey;
    }

    private static async Task<T> Send<T>(PairedWorld world, HttpMethod method, string url, object? body = null)
    {
        var response = await world.Nid.RawAsync(world.Nid.AdultA, method, url, body);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<T>(PairedWorld.NidJson))!;
    }
}
