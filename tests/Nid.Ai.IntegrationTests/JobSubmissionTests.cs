using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Nid.Ai.Application;
using Nid.Ai.Infrastructure.Persistence;
using Nid.Ai.Protocol.Contracts;
using Nid.Platform.Contracts;
using Nid.TestSupport;
using NodaTime;

namespace Nid.Ai.IntegrationTests;

public sealed class JobSubmissionTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static readonly Instant Now = NidApp.GoldenCaseToday.AtMidnight().InUtc().ToInstant();

    [Fact]
    public async Task The_same_input_twice_while_it_waits_is_one_job()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await using var scope = Scope(app);
        var jobs = scope.ServiceProvider.GetRequiredService<IModelJobs>();

        var first = await jobs.SubmitAsync(Echo("hello"), TestContext.Current.CancellationToken);
        var again = await jobs.SubmitAsync(Echo("hello"), TestContext.Current.CancellationToken);
        var other = await jobs.SubmitAsync(Echo("bye"), TestContext.Current.CancellationToken);

        Assert.Equal(first, again);
        Assert.NotEqual(first, other);
        var view = await jobs.GetAsync(first, TestContext.Current.CancellationToken);
        Assert.Equal("queued", view!.Status);
    }

    [Fact]
    public async Task An_unknown_type_or_a_bad_input_is_refused()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await using var scope = Scope(app);
        var jobs = scope.ServiceProvider.GetRequiredService<IModelJobs>();

        var unknown = await Assert.ThrowsAsync<ModelCallException>(() => jobs.SubmitAsync(
            new JobRequest("speak", 1, JsonSerializer.SerializeToElement(new { text = "hi" }), "none"), TestContext.Current.CancellationToken));
        var bad = await Assert.ThrowsAsync<ModelCallException>(() => jobs.SubmitAsync(
            new JobRequest("echo", 1, JsonSerializer.SerializeToElement(new { words = "hi" }), "none"), TestContext.Current.CancellationToken));

        Assert.Equal(JobCodes.UnknownType, unknown.Code);
        Assert.Equal(JobCodes.InvalidInput, bad.Code);
    }

    [Fact]
    public async Task Local_workers_come_first_then_the_least_loaded()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await using var scope = Scope(app);
        var db = scope.ServiceProvider.GetRequiredService<AiDbContext>();
        db.Workers.AddRange(
            Row("remote-idle", "remote", load: 0),
            Row("local-busy", "local", load: 3),
            Row("local-idle", "local", load: 1));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var route = await scope.ServiceProvider.GetRequiredService<IWorkerRouter>()
            .RouteAsync("echo", 1, ModelCallKind.Job, "none", null, TestContext.Current.CancellationToken);

        Assert.Equal(["local-idle", "local-busy", "remote-idle"], route.Workers.Select(w => w.Host));
    }

    [Fact]
    public async Task Disabled_offline_and_undeclaring_workers_are_never_chosen()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await using var scope = Scope(app);
        var db = scope.ServiceProvider.GetRequiredService<AiDbContext>();
        db.Workers.AddRange(
            Row("disabled", "local", status: "disabled"),
            Row("offline", "local", seen: Now - Duration.FromMinutes(4)),
            Row("no-echo", "local", declares: "other"),
            Row("pending", "local", status: "pending"));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var route = await scope.ServiceProvider.GetRequiredService<IWorkerRouter>()
            .RouteAsync("echo", 1, ModelCallKind.Job, "none", null, TestContext.Current.CancellationToken);

        Assert.Empty(route.Workers);
        Assert.Equal(JobCodes.NoWorker, route.Reason);
    }

    [Fact]
    public async Task A_target_worker_is_the_only_route()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await using var scope = Scope(app);
        var db = scope.ServiceProvider.GetRequiredService<AiDbContext>();
        var target = Row("target", "remote", load: 9);
        db.Workers.AddRange(Row("better", "local"), target);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var route = await scope.ServiceProvider.GetRequiredService<IWorkerRouter>()
            .RouteAsync("echo", 1, ModelCallKind.Request, "none", target.Id, TestContext.Current.CancellationToken);

        Assert.Equal(["target"], route.Workers.Select(w => w.Host));
    }

    [Fact]
    public async Task A_remote_worker_sees_household_data_only_after_the_opt_in()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await using var scope = Scope(app);
        var db = scope.ServiceProvider.GetRequiredService<AiDbContext>();
        db.Workers.Add(Row("remote", "remote"));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var router = scope.ServiceProvider.GetRequiredService<IWorkerRouter>();

        var closed = await router.RouteAsync("echo", 1, ModelCallKind.Job, "documents", null, TestContext.Current.CancellationToken);
        var test = await router.RouteAsync("echo", 1, ModelCallKind.Job, "none", null, TestContext.Current.CancellationToken);
        await scope.ServiceProvider.GetRequiredService<IAiSettings>()
            .SetRemoteWorkersAllowedAsync(true, app.AdultA.Value, Now, TestContext.Current.CancellationToken);
        var open = await router.RouteAsync("echo", 1, ModelCallKind.Job, "documents", null, TestContext.Current.CancellationToken);

        Assert.Empty(closed.Workers);
        Assert.Equal("ai.remote_not_allowed", closed.Reason);
        Assert.Single(test.Workers);
        Assert.Single(open.Workers);
    }

    [Fact]
    public async Task Local_only_data_goes_to_a_safe_worker_only()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await using var scope = Scope(app);
        var db = scope.ServiceProvider.GetRequiredService<AiDbContext>();
        var safe = Row("safe", "local");
        safe.Safe = true;
        db.Workers.AddRange(Row("plain", "local"), safe);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var router = scope.ServiceProvider.GetRequiredService<IWorkerRouter>();

        var route = await router.RouteAsync("echo", 1, ModelCallKind.Job, "local_only", null, TestContext.Current.CancellationToken);

        Assert.Equal(["safe"], route.Workers.Select(w => w.Host));
        db.Workers.Remove(safe);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        Assert.Equal("ai.no_safe_worker",
            (await router.RouteAsync("echo", 1, ModelCallKind.Job, "local_only", null, TestContext.Current.CancellationToken)).Reason);
    }

    [Fact]
    public async Task An_unknown_data_class_is_refused()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await using var scope = Scope(app);

        var refused = await Assert.ThrowsAsync<ModelCallException>(() => scope.ServiceProvider.GetRequiredService<IModelJobs>().SubmitAsync(
            Echo("hi") with { DataClass = "payslips" }, TestContext.Current.CancellationToken));

        Assert.Equal("ai.unknown_data_class", refused.Code);
    }

    private static AsyncServiceScope Scope(NidApp app)
    {
        var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
        return scope;
    }

    private static JobRequest Echo(string text) => new("echo", 1, JsonSerializer.SerializeToElement(new EchoJobInput(text), ContractJson.Strict), "none");

    private static ModelWorkerRow Row(string host, string placement, int load = 0, string status = "active", Instant? seen = null, string declares = "echo")
        => new()
        {
            Id = Guid.CreateVersion7(), Name = host, Host = host, Port = 8443, Placement = placement, Status = status,
            WorkerPublicKey = new byte[65], WorkerKeyFingerprint = new string('a', 64), CreatedAt = Now, LastSeenAt = seen ?? Now,
            LastDeclaration = JsonSerializer.Serialize(new WorkerDeclaration("1.0.0", [new ContractVersion(declares, 1)],
                [new ContractVersion(declares, 1)], 4, [], new string('a', 64), load), ContractJson.Strict),
        };
}
