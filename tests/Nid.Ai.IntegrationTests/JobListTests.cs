using System.Net;
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

/// <summary>The jobs page (#476): any adult sees what waits and why, never a job's input or result; filters and pages are the
/// server's.</summary>
public sealed class JobListTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static readonly Instant Now = NidApp.GoldenCaseToday.AtMidnight().InUtc().ToInstant();

    [Fact]
    public async Task Another_adult_may_not_list_the_jobs()
    {
        await using var app = await NidApp.StartAsync(fixture);

        // #585: the jobs are the administration's.
        Assert.Equal(HttpStatusCode.Forbidden, (await app.RawAsync(app.AdultB, HttpMethod.Get, "/api/v1/ai/jobs")).StatusCode);
    }

    [Fact]
    public async Task An_admin_sees_the_jobs_newest_first_without_their_content()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await SubmitAsync(app, "first");
        await SubmitAsync(app, "second");

        var page = await ListAsync(app, "");

        Assert.Equal(2, page.Total);
        Assert.Equal(1, page.Page);
        Assert.Equal(["echo"], page.Types);
        Assert.All(page.Items, j => Assert.Equal("queued", j.Status));
        Assert.True(page.Items[0].CreatedAt >= page.Items[1].CreatedAt);
        var raw = await (await app.RawAsync(app.AdultA, HttpMethod.Get, "/api/v1/ai/jobs")).Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain("first", raw);
        Assert.DoesNotContain("input", raw);
    }

    [Fact]
    public async Task Filters_and_pages_are_applied_by_the_server()
    {
        await using var app = await NidApp.StartAsync(fixture);
        for (var i = 0; i < 55; i++)
            await SubmitAsync(app, $"job {i}");

        var second = await ListAsync(app, "?page=2");
        var running = await ListAsync(app, "?status=running");
        var other = await ListAsync(app, "?type=speak");

        Assert.Equal(55, second.Total);
        Assert.Equal(5, second.Items.Count);
        Assert.Equal(0, running.Total);
        Assert.Equal(0, other.Total);
    }

    [Fact]
    public async Task An_unknown_status_is_refused()
    {
        await using var app = await NidApp.StartAsync(fixture);

        var response = await app.RawAsync(app.AdultA, HttpMethod.Get, "/api/v1/ai/jobs?status=done");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task A_worker_shows_the_load_it_declared()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await using (var scope = Scope(app))
        {
            var db = scope.ServiceProvider.GetRequiredService<AiDbContext>();
            db.Workers.Add(new ModelWorkerRow
            {
                Id = Guid.CreateVersion7(), Name = "box", Host = "box", Port = 8443, Placement = "local", Status = "active",
                WorkerPublicKey = new byte[65], WorkerKeyFingerprint = new string('a', 64), CreatedAt = Now, LastSeenAt = Now,
                LastDeclaration = JsonSerializer.Serialize(new WorkerDeclaration("1.0.0", [], [], 4, [], new string('a', 64), 3), ContractJson.Strict),
            });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var list = await (await app.RawAsync(app.AdultB, HttpMethod.Get, "/api/v1/ai/workers")).Content
            .ReadFromJsonAsync<WorkerList>(PairedWorld.NidJson, TestContext.Current.CancellationToken);

        Assert.Equal(3, Assert.Single(list!.Workers).Load);
    }

    private static AsyncServiceScope Scope(NidApp app)
    {
        var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
        return scope;
    }

    private static async Task SubmitAsync(NidApp app, string text)
    {
        await using var scope = Scope(app);
        await scope.ServiceProvider.GetRequiredService<IModelJobs>().SubmitAsync(
            new JobRequest("echo", 1, JsonSerializer.SerializeToElement(new EchoJobInput(text), ContractJson.Strict), "none"),
            TestContext.Current.CancellationToken).OrThrow();
    }

    private static async Task<ModelJobPage> ListAsync(NidApp app, string query)
    {
        var response = await app.RawAsync(app.AdultA, HttpMethod.Get, $"/api/v1/ai/jobs{query}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ModelJobPage>(PairedWorld.NidJson, TestContext.Current.CancellationToken))!;
    }
}
