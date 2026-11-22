using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nid.Ai.Contracts;
using Nid.Ai.Infrastructure.Persistence;
using Nid.Platform.Infrastructure.Persistence;
using Nid.TestSupport;
using NodaTime;

namespace Nid.Ai.IntegrationTests;

public sealed class PairingTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task An_admin_pairs_a_worker()
    {
        await using var world = await PairedWorld.StartAsync(fixture);

        var view = await world.PairAsync();

        Assert.Equal("active", view.Status);
        Assert.NotNull(view.NidKeyFingerprint);
        Assert.NotNull(view.SoftwareVersion);
        await using var scope = world.Nid.Services.CreateAsyncScope();
        var row = await scope.ServiceProvider.GetRequiredService<AiDbContext>().Workers.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(row.TlsCertificateSha256);
        Assert.NotNull(row.NidKeyId);
        Assert.NotNull(row.LastDeclaration);
        Assert.Equal(view.NidKeyFingerprint, world.Worker.State.Load() is { NidKey: { } spki }
            ? Protocol.Keys.KeyFingerprint.Compact(Convert.FromBase64String(spki)) : null);

        var events = await scope.ServiceProvider.GetRequiredService<PlatformDbContext>().AuditLog.AsNoTracking()
            .Where(e => e.Category == "security").Select(e => e.Action).ToListAsync(TestContext.Current.CancellationToken);
        Assert.Contains("model_worker_registered", events);
        Assert.Contains("model_worker_paired", events);
    }

    [Fact]
    public async Task A_wrong_fingerprint_or_code_leaves_it_pending()
    {
        await using var world = await PairedWorld.StartAsync(fixture);
        var registered = await (await world.RegisterAsync()).Content.ReadFromJsonAsync<RegisteredWorker>(PairedWorld.NidJson, TestContext.Current.CancellationToken);

        var wrongFingerprint = await world.Nid.RawAsync(world.Nid.AdultA, HttpMethod.Post, $"/api/v1/ai/workers/{registered!.Id}/pair",
            new { fingerprint = new string('0', 64), code = world.Worker.PairingCode() });
        var wrongCode = await world.Nid.RawAsync(world.Nid.AdultA, HttpMethod.Post, $"/api/v1/ai/workers/{registered.Id}/pair",
            new { fingerprint = registered.Fingerprint, code = "ZZZZZZZZ" });

        Assert.Contains(AiCodes.FingerprintMismatch, await wrongFingerprint.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Contains(AiCodes.PairingRefused, await wrongCode.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var list = await world.ListAsync();
        Assert.Equal("pending", Assert.Single(list.Workers).Status);
        Assert.False(world.Worker.State.Load().IsPaired);
    }

    [Fact]
    public async Task A_second_registration_of_a_paired_worker_is_refused()
    {
        await using var world = await PairedWorld.StartAsync(fixture);
        await world.PairAsync();

        var again = await world.RegisterAsync();

        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Contains(AiCodes.WorkerExists, await again.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_pending_registration_older_than_an_hour_is_dropped()
    {
        await using var world = await PairedWorld.StartAsync(fixture);
        await world.RegisterAsync();
        await using (var scope = world.Nid.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AiDbContext>();
            await db.Workers.ExecuteUpdateAsync(s => s.SetProperty(w => w.CreatedAt, Instant.FromUnixTimeSeconds(0)), TestContext.Current.CancellationToken);
        }

        await world.RegisterAsync(url: "https://other.test:8443"); // registering sweeps the stale ones first

        var list = await world.ListAsync();
        Assert.DoesNotContain(list.Workers, w => w.Url == PairedWorld.WorkerUrl);
    }

    [Fact]
    public async Task An_adult_who_is_not_admin_reads_the_list_but_cannot_register()
    {
        await using var world = await PairedWorld.StartAsync(fixture);

        var refused = await world.RegisterAsync(asPerson: world.Nid.AdultB.Value);

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Contains("platform.admin_required", await refused.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Empty((await world.ListAsync(world.Nid.AdultB)).Workers);
    }

    [Fact]
    public async Task An_unreachable_worker_is_reported_and_stays_pending()
    {
        await using var nid = await NidApp.StartAsync(fixture);

        var response = await nid.RawAsync(nid.AdultA, HttpMethod.Post, "/api/v1/ai/workers",
            new { name = "Nowhere", url = "https://nowhere.invalid:8443", placement = "local", proxyDeclared = false });

        Assert.Contains(AiCodes.WorkerUnreachable, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }
}
