using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nid.Ai.Application;
using Nid.Ai.Infrastructure.Persistence;
using Nid.Platform.Contracts;
using Nid.TestSupport;
using NodaTime;

namespace Nid.Ai.IntegrationTests;

/// <summary>#483: finished model jobs, their input and their result, do not stay forever; work still to do is never touched.</summary>
public sealed class JobPurgeTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static readonly Instant Now = NidApp.GoldenCaseToday.AtMidnight().InUtc().ToInstant();

    [Fact]
    public async Task Finished_jobs_go_after_their_delay_and_work_to_do_stays()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var ct = TestContext.Current.CancellationToken;
        var recentSuccess = await SeedAsync(app, "succeeded", finishedDaysAgo: 6);
        var oldSuccess = await SeedAsync(app, "succeeded", finishedDaysAgo: 8);
        var recentDead = await SeedAsync(app, "dead", finishedDaysAgo: 29);
        var oldDead = await SeedAsync(app, "dead", finishedDaysAgo: 31);
        var oldFailed = await SeedAsync(app, "failed", finishedDaysAgo: 31);
        var oldQueued = await SeedAsync(app, "queued", finishedDaysAgo: null);
        var oldRunning = await SeedAsync(app, "running", finishedDaysAgo: null);

        int purged;
        await using (var scope = Scope(app))
            purged = await scope.ServiceProvider.GetRequiredService<IJobPurge>().RunOnceAsync(ct);

        Assert.Equal(3, purged);
        await using var check = Scope(app);
        var left = await check.ServiceProvider.GetRequiredService<AiDbContext>().Jobs.AsNoTracking().Select(j => j.Id).ToListAsync(ct);
        Assert.Equal(
            new[] { recentSuccess, recentDead, oldQueued, oldRunning }.Order(),
            left.Order());
        Assert.DoesNotContain(oldSuccess, left);
        Assert.DoesNotContain(oldDead, left);
        Assert.DoesNotContain(oldFailed, left);
    }

    private static AsyncServiceScope Scope(NidApp app)
    {
        var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
        return scope;
    }

    /// <summary>A job created 100 days ago; a finished one finished <paramref name="finishedDaysAgo"/> days ago.</summary>
    private static async Task<Guid> SeedAsync(NidApp app, string status, int? finishedDaysAgo)
    {
        var id = Guid.CreateVersion7();
        await using var scope = Scope(app);
        var db = scope.ServiceProvider.GetRequiredService<AiDbContext>();
        db.Jobs.Add(new JobRow
        {
            Id = id, Type = "echo", Version = 1, Input = "{\"text\":\"x\"}", InputHash = id.ToString("N"), DataClass = "none", Status = status,
            CreatedAt = Now - Duration.FromDays(100), RunAfter = Now - Duration.FromDays(100), Result = status == "succeeded" ? "{}" : null,
            FinishedAt = finishedDaysAgo is { } days ? Now - Duration.FromDays(days) : null,
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return id;
    }
}
