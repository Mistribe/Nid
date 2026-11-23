using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Nid.Ai.Application;
using Nid.Ai.Contracts;
using Nid.Ai.Infrastructure.Persistence;
using Nid.Ai.Protocol.Contracts;
using Nid.Platform.Contracts;
using Nid.TestSupport;
using NodaTime;

namespace Nid.Ai.IntegrationTests;

/// <summary>Decision D (docs/ai/README.md §11): one answer drives every « this needs a model » state.</summary>
public sealed class CapabilitiesTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static readonly Instant Now = NidApp.GoldenCaseToday.AtMidnight().InUtc().ToInstant();

    [Fact]
    public async Task Without_a_worker_echo_and_the_chat_say_so()
    {
        await using var app = await NidApp.StartAsync(fixture);

        var capabilities = await ReadAsync(app);

        Assert.False(capabilities.RemoteWorkersAllowed);
        Assert.Equal(
            [("echo", 0, 0, 0, "ai.no_worker"), ("chat", 0, 0, 0, "ai.no_worker")],
            capabilities.Capabilities.Select(c => (c.Capability, c.Local, c.Remote, c.Safe, c.Reason)));
    }

    [Fact]
    public async Task An_online_local_worker_and_its_models_are_counted()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await AddAsync(app, Row("box", "local", safe: true, models: [Model(DeclaredCapability.Embed)]));

        var capabilities = await ReadAsync(app);

        Assert.Equal(("echo", 1, 0, 1, (string?)null), Find(capabilities, "echo"));
        Assert.Equal(("embed", 1, 0, 1, (string?)null), Find(capabilities, "embed"));
    }

    [Fact]
    public async Task A_remote_worker_counts_only_after_the_opt_in()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await AddAsync(app, Row("far", "remote"));

        Assert.Equal(("echo", 0, 0, 0, "ai.remote_not_allowed"), Find(await ReadAsync(app), "echo"));

        await app.RawAsync(app.AdultA, HttpMethod.Put, "/api/v1/ai/settings/remote-workers", new { allowed = true });
        Assert.Equal(("echo", 0, 1, 0, (string?)null), Find(await ReadAsync(app), "echo"));
    }

    [Fact]
    public async Task Offline_remote_workers_without_the_opt_in_say_the_opt_in_is_missing()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await AddAsync(app, Row("far", "remote", seen: Now - Duration.FromMinutes(5)));

        Assert.Equal(("echo", 0, 0, 0, "ai.remote_not_allowed"), Find(await ReadAsync(app), "echo"));
    }

    [Fact]
    public async Task Workers_not_seen_for_three_minutes_are_offline()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await AddAsync(app, Row("stale", "local", seen: Now - Duration.FromMinutes(5), models: [Model(DeclaredCapability.Chat)]));

        var capabilities = await ReadAsync(app);

        Assert.Equal(("echo", 0, 0, 0, "ai.all_offline"), Find(capabilities, "echo"));
        Assert.Equal(("chat", 0, 0, 0, "ai.all_offline"), Find(capabilities, "chat"));
    }

    private static (string, int, int, int, string?) Find(ModelCapabilities capabilities, string name)
        => capabilities.Capabilities.Single(c => c.Capability == name) is var c ? (c.Capability, c.Local, c.Remote, c.Safe, c.Reason) : default;

    private static async Task<ModelCapabilities> ReadAsync(NidApp app)
    {
        var response = await app.RawAsync(app.AdultB, HttpMethod.Get, "/api/v1/ai/capabilities");
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ModelCapabilities>(PairedWorld.NidJson, TestContext.Current.CancellationToken))!;
    }

    private static async Task AddAsync(NidApp app, ModelWorkerRow row)
    {
        await using var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
        var db = scope.ServiceProvider.GetRequiredService<AiDbContext>();
        db.Workers.Add(row);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private static DeclaredModel Model(DeclaredCapability capability)
        => new(capability, "model", "sha256:ab", 8192, 1024, capability == DeclaredCapability.Embed ? 768 : null);

    private static ModelWorkerRow Row(string host, string placement, bool safe = false, Instant? seen = null, DeclaredModel[]? models = null)
        => new()
        {
            Id = Guid.CreateVersion7(), Name = host, Host = host, Port = 8443, Placement = placement, Status = "active", Safe = safe,
            WorkerPublicKey = new byte[65], WorkerKeyFingerprint = new string('a', 64), CreatedAt = Now, LastSeenAt = seen ?? Now,
            LastDeclaration = JsonSerializer.Serialize(new WorkerDeclaration("1.0.0", [new ContractVersion("echo", 1)],
                [new ContractVersion("echo", 1)], 4, models ?? [], new string('a', 64)), ContractJson.Strict),
        };
}
