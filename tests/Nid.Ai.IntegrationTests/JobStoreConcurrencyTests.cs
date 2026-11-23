using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Nid.Ai.Application;
using Nid.Ai.Contracts;
using Nid.Ai.Protocol.Contracts;
using Nid.Platform.Contracts;
using Nid.TestSupport;

namespace Nid.Ai.IntegrationTests;

public sealed class JobStoreConcurrencyTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task A_stale_job_is_not_saved_over_a_newer_one()
    {
        await using var app = await NidApp.StartAsync(fixture);
        Guid id;
        await using (var scope = Scope(app))
            id = await scope.ServiceProvider.GetRequiredService<IModelJobs>().SubmitAsync(
                new JobRequest("echo", 1, JsonSerializer.SerializeToElement(new EchoJobInput("hi"), ContractJson.Strict), "none"),
                TestContext.Current.CancellationToken).OrThrow();

        JobRecord stale;
        await using (var scope = Scope(app))
            stale = (await scope.ServiceProvider.GetRequiredService<IJobStore>().FindAsync(id, TestContext.Current.CancellationToken))!;

        await using (var first = Scope(app))
            Assert.True(await first.ServiceProvider.GetRequiredService<IJobStore>()
                .TryUpdateAsync(stale with { Status = JobState.Running }, TestContext.Current.CancellationToken));
        await using (var second = Scope(app))
            Assert.False(await second.ServiceProvider.GetRequiredService<IJobStore>()
                .TryUpdateAsync(stale with { Status = JobState.Running }, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Concurrent_claims_of_one_job_let_one_through()
    {
        await using var app = await NidApp.StartAsync(fixture);
        Guid id;
        await using (var scope = Scope(app))
            id = await scope.ServiceProvider.GetRequiredService<IModelJobs>().SubmitAsync(
                new JobRequest("echo", 1, JsonSerializer.SerializeToElement(new EchoJobInput("hi"), ContractJson.Strict), "none"),
                TestContext.Current.CancellationToken).OrThrow();
        JobRecord stale;
        await using (var scope = Scope(app))
            stale = (await scope.ServiceProvider.GetRequiredService<IJobStore>().FindAsync(id, TestContext.Current.CancellationToken))!;

        var claims = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
        {
            await using var scope = Scope(app);
            return await scope.ServiceProvider.GetRequiredService<IJobStore>()
                .TryUpdateAsync(stale with { Status = JobState.Running }, TestContext.Current.CancellationToken);
        }));

        Assert.Equal(1, claims.Count(c => c));
    }

    private static AsyncServiceScope Scope(NidApp app)
    {
        var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
        return scope;
    }
}
