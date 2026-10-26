using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nid.Core.Contracts;
using Nid.Core.Infrastructure.Persistence;
using Nid.Host.Jobs;
using Nid.Platform.Contracts;
using Nid.Platform.Infrastructure.Persistence;
using Nid.RealEstate.Contracts;
using Nid.Subscriptions.IntegrationTests.Proposals;
using Nid.TestSupport;
using NodaTime;
using Quartz;

namespace Nid.Subscriptions.IntegrationTests.Notifications;

/// <summary>Notifications design §2.1: access violations, pending proposals and failed jobs publish through the notifier.</summary>
public class ProducerTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string Proposals = "/api/v1/core/proposals";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static async Task<List<NotificationRow>> RowsAsync(NidApp app, string kind)
    {
        await using var db = PostgresFixture.NewPlatformContext(app.ConnectionString);
        return await db.Notifications.AsNoTracking().Where(n => n.Kind == kind).ToListAsync();
    }

    private sealed class EmptyForMeVisibleItems(CoreDbContext db) : IVisibleItems
    {
        Task<IReadOnlySet<ItemId>> IVisibleItems.ForAsync(PersonId actor, VisibilityScope scope, CancellationToken ct)
            => ForAsync(actor, scope, includeArchived: false, ct);

        public async Task<IReadOnlySet<ItemId>> ForAsync(PersonId actor, VisibilityScope scope, bool includeArchived, CancellationToken ct)
            => scope == VisibilityScope.Me
                ? new HashSet<ItemId>()
                : await new VisibleItems(db).ForAsync(actor, scope, includeArchived, ct);
    }

    /// <summary>The echo kind without a target.</summary>
    private sealed class FreeHandler : IProposalHandler
    {
        private readonly EchoProposalHandler echo = new();
        public string Module => echo.Module;
        public string Kind => "test.free";
        public int PayloadVersion => 1;
        public bool NeedsTarget => false;
        public Task<UnambitiousFx.Functional.Result<ProposalCheck>> ValidateAsync(ItemId target, JsonElement payload, bool edited, CancellationToken ct) => echo.ValidateAsync(target, payload, edited, ct);
        public Task<JsonElement> PreviewAsync(ItemId target, JsonElement payload, CancellationToken ct) => echo.PreviewAsync(target, payload, ct);
        public Task<UnambitiousFx.Functional.Result<IReadOnlyList<Guid>>> ApplyAsync(ItemId target, JsonElement payload, CancellationToken ct) => echo.ApplyAsync(target, payload, ct);
    }

    [Fact]
    public async Task Access_violation_reaches_every_adult_even_when_the_request_fails()
    {
        await using var app = await NidApp.StartAsync(fixture, services =>
            services.AddScoped<IVisibleItems>(sp => new EmptyForMeVisibleItems(sp.GetRequiredService<CoreDbContext>())));
        await app.NewSubscriptionAsync("leaky", app.AdultA, Visibility.Shared);

        var response = await app.RawAsync(app.AdultA, HttpMethod.Get, "/api/v1/subscriptions?scope=household");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("core.access_violation", JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("code").GetString());
        var rows = await RowsAsync(app, "access_violation");
        Assert.Equal(new[] { app.AdultA.Value, app.AdultB.Value }.Order(), rows.Select(r => r.PersonId).Order());
        Assert.All(rows, r =>
        {
            Assert.Equal((NotificationStates.Open, "critical"), (r.State, r.Level));
            Assert.Empty(r.ItemIds);
            Assert.Equal("ListSubscriptionsQuery", JsonDocument.Parse(r.Params).RootElement.GetProperty("request").GetString());
        });
        await using var platform = PostgresFixture.NewPlatformContext(app.ConnectionString);
        Assert.Single(await platform.AuditLog.Where(a => a.Category == "security" && a.Action == "access_violation").ToListAsync());
        // One notice per request and UTC day (the audit keeps each occurrence).
        Assert.All(rows, r => Assert.Matches(@"^access_violation:ListSubscriptionsQuery:\d{4}-\d{2}-\d{2}$", r.Key));
    }

    private static async Task<Guid> SubmitAsync(NidApp app, PersonId who, Guid? target, string subject)
    {
        var response = await app.RawAsync(who, HttpMethod.Post, Proposals,
            new { kind = target is null ? "test.free" : EchoProposalHandler.KindName, targetItemId = target, payload = new { subject } });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
    }

    /// <summary>Throws on any write check for <see cref="Person"/> while it is set: the decider lookup fails, the actor's own check does not.</summary>
    private sealed class FailingFor
    {
        public PersonId? Person;
    }

    private sealed class FlakyPolicy(IAccessPolicy inner, FailingFor failing) : IAccessPolicy
    {
        public Task<UnambitiousFx.Functional.Result> CheckAsync(PersonId actor, ItemId item, AccessKind kind, CancellationToken ct)
            => actor == failing.Person && kind == AccessKind.Write ? throw new InvalidOperationException("The policy is down.") : inner.CheckAsync(actor, item, kind, ct);

        public Task<IReadOnlySet<ItemId>> CheckManyAsync(PersonId actor, IReadOnlyCollection<ItemId> ids, AccessKind kind, CancellationToken ct)
            => inner.CheckManyAsync(actor, ids, kind, ct);

        public Task<UnambitiousFx.Functional.Result> CheckCreationAsync(PersonId actor, IReadOnlyList<OwnerShare> owners, CancellationToken ct)
            => inner.CheckCreationAsync(actor, owners, ct);
    }

    [Fact]
    public async Task A_decider_lookup_that_fails_stores_nothing_so_the_retry_notifies()
    {
        var failing = new FailingFor();
        await using var app = await NidApp.StartAsync(fixture, services =>
        {
            services.AddScoped<IProposalHandler, EchoProposalHandler>();
            services.AddScoped<IAccessPolicy>(sp => new FlakyPolicy(ActivatorUtilities.CreateInstance<AccessPolicy>(sp), failing));
        });
        var joint = await app.NewOwnedSubscriptionAsync("joint", Visibility.Shared, (app.AdultA, 5_000), (app.AdultB, 5_000));
        failing.Person = app.AdultB;

        // The test server hands the unhandled exception back (in production: a 500), before anything was stored.
        await Assert.ThrowsAsync<InvalidOperationException>(() => app.RawAsync(app.AdultA, HttpMethod.Post, Proposals,
            new { kind = EchoProposalHandler.KindName, targetItemId = joint.Value, payload = new { subject = "s1" } }));
        await using (var core = PostgresFixture.NewCoreContext(app.ConnectionString))
            Assert.Empty(await core.Proposals.AsNoTracking().ToListAsync());
        Assert.Empty(await RowsAsync(app, "proposal_pending"));

        failing.Person = null;
        var id = await SubmitAsync(app, app.AdultA, joint.Value, "s1");
        Assert.Equal(new[] { app.AdultA.Value, app.AdultB.Value }.Order(), (await ForProposalAsync(app, id)).Select(r => r.PersonId).Order());
    }

    private static async Task<uint> VersionAsync(NidApp app, PersonId who, Guid id)
        => JsonDocument.Parse(await (await app.RawAsync(who, HttpMethod.Get, $"{Proposals}/{id}")).Content.ReadAsStringAsync())
            .RootElement.GetProperty("proposal").GetProperty("version").GetUInt32();

    private static async Task<List<NotificationRow>> ForProposalAsync(NidApp app, Guid id)
        => (await RowsAsync(app, "proposal_pending")).Where(r => r.Key == $"proposal_pending:{id}").ToList();

    [Fact]
    public async Task Proposal_pending_reaches_the_deciders_and_resolves_on_decision()
    {
        await using var app = await NidApp.StartAsync(fixture, services =>
        {
            services.AddScoped<IProposalHandler, EchoProposalHandler>();
            services.AddScoped<IProposalHandler, FreeHandler>();
        });
        var joint = await app.NewOwnedSubscriptionAsync("joint", Visibility.Shared, (app.AdultA, 5_000), (app.AdultB, 5_000));
        var aliceOnly = await app.NewSubscriptionAsync("alice", app.AdultA, Visibility.Shared);

        var shared = await SubmitAsync(app, app.AdultB, joint.Value, "s1");
        var readOnly = await SubmitAsync(app, app.AdultA, aliceOnly.Value, "s2");
        var untargeted = await SubmitAsync(app, app.AdultB, null, "s3");

        Assert.Equal(new[] { app.AdultA.Value, app.AdultB.Value }.Order(), (await ForProposalAsync(app, shared)).Select(r => r.PersonId).Order());
        Assert.Equal(app.AdultA.Value, Assert.Single(await ForProposalAsync(app, readOnly)).PersonId);
        Assert.Equal(app.AdultB.Value, Assert.Single(await ForProposalAsync(app, untargeted)).PersonId);

        var approved = await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Proposals}/{shared}/approve", new { version = await VersionAsync(app, app.AdultA, shared) });
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        Assert.All(await ForProposalAsync(app, shared), r => Assert.Equal(NotificationStates.Resolved, r.State));

        var rejected = await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Proposals}/{readOnly}/reject", new { version = await VersionAsync(app, app.AdultA, readOnly) });
        Assert.Equal(HttpStatusCode.OK, rejected.StatusCode);
        Assert.All(await ForProposalAsync(app, readOnly), r => Assert.Equal(NotificationStates.Resolved, r.State));
        Assert.Equal(NotificationStates.Open, Assert.Single(await ForProposalAsync(app, untargeted)).State);
    }

    [Fact]
    public async Task A_superseded_proposal_resolves()
    {
        await using var app = await NidApp.StartAsync(fixture, services => services.AddScoped<IProposalHandler, EchoProposalHandler>());
        var item = await app.NewSubscriptionAsync("alice", app.AdultA, Visibility.Shared);

        var first = await SubmitAsync(app, app.AdultA, item.Value, "s1");
        var second = await SupersedeAsync(app, item.Value);

        Assert.NotEqual(first, second);
        Assert.Equal(NotificationStates.Resolved, Assert.Single(await ForProposalAsync(app, first)).State);
        Assert.Equal(NotificationStates.Open, Assert.Single(await ForProposalAsync(app, second)).State);
    }

    private static async Task<Guid> SupersedeAsync(NidApp app, Guid item)
    {
        var response = await app.RawAsync(app.AdultA, HttpMethod.Post, Proposals,
            new { kind = EchoProposalHandler.KindName, targetItemId = item, payload = new { subject = "s1", note = "autre" } });
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
    }

    /// <summary>Makes <see cref="IIrlRunLog.TryStartAsync"/> throw while <see cref="Crash"/> is set: a crash of the irl-weekly job.</summary>
    private sealed class Gate
    {
        public volatile bool Crash;
        public int Calls;
    }

    private sealed class FlakyRunLog(IIrlRunLog inner, Gate gate) : IIrlRunLog
    {
        public Task<IrlRunView?> TryStartAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref gate.Calls);
            return gate.Crash ? throw new InvalidOperationException("boom") : inner.TryStartAsync(ct);
        }

        public Task FinishAsync(IrlRunView run, CancellationToken ct) => inner.FinishAsync(run, ct);

        public Task<bool> IsRunningAsync(CancellationToken ct) => inner.IsRunningAsync(ct);

        public Task<IReadOnlyList<IrlRunView>> RecentAsync(int count, CancellationToken ct) => inner.RecentAsync(count, ct);

        public Task<int> PurgeBeforeAsync(Instant before, CancellationToken ct) => inner.PurgeBeforeAsync(before, ct);
    }

    private static async Task RunIrlAsync(NidApp app, IScheduler scheduler, Gate gate, bool crash)
    {
        var calls = gate.Calls;
        gate.Crash = crash;
        await scheduler.TriggerJob(IrlJobs.WeeklyKey);
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline && (gate.Calls == calls || (await scheduler.QueryFireInstances(new FireInstanceQuery())).Items.Count > 0))
            await Task.Delay(100);
        Assert.True(gate.Calls > calls, "the job never ran");
    }

    [Fact]
    public async Task Job_failed_is_one_notification_per_incident()
    {
        var name = "nid-test-" + Guid.NewGuid().ToString("N");
        var gate = new Gate();
        await using var app = await NidApp.StartAsync(fixture,
            services =>
            {
                var real = services.Single(d => d.ServiceType == typeof(IIrlRunLog)).ImplementationType!;
                services.AddScoped<IIrlRunLog>(sp => new FlakyRunLog((IIrlRunLog)ActivatorUtilities.CreateInstance(sp, real), gate));
            },
            new Dictionary<string, string>
            {
                ["Jobs:Enabled"] = "true", ["Jobs:SchedulerName"] = name,
                ["Nid:Smtp:Host"] = "smtp.test.localhost", ["Nid:Smtp:From"] = "Nid <nid@test.localhost>",
            });
        (await app.RawAsync(app.AdultA, HttpMethod.Patch, "/api/v1/admin/household", new { irlEnabled = true })).EnsureSuccessStatusCode();
        await using (var core = PostgresFixture.NewCoreContext(app.ConnectionString))
        {
            (await core.Persons.SingleAsync(p => p.Id == app.AdultA.Value)).Email = "a@example.test";
            await core.SaveChangesAsync();
        }
        var saved = await app.RawAsync(app.AdultA, HttpMethod.Put, "/api/v1/platform/notifications/preferences",
            new { emailEnabled = true, action = "immediate", info = "digest", frequency = "weekly", weekday = 1, leadDays = new Dictionary<string, int>(), version = 0 });
        Assert.True(saved.IsSuccessStatusCode, await saved.Content.ReadAsStringAsync());
        var scheduler = await app.Services.GetRequiredKeyedService<ISchedulerFactory>(name).GetScheduler();

        await RunIrlAsync(app, scheduler, gate, crash: true);
        await RunIrlAsync(app, scheduler, gate, crash: true);

        var open = Assert.Single(await RowsAsync(app, "job_failed"));
        Assert.Equal((app.AdultA.Value, "job_failed:irl-weekly", NotificationStates.Open, 1), (open.PersonId, open.Key, open.State, open.Round));
        Assert.Equal(1, Assert.Single(await EmailsAsync(app, open.Id)).Round); // the second crash of the same incident sends nothing

        await RunIrlAsync(app, scheduler, gate, crash: false);
        Assert.Equal(NotificationStates.Resolved, Assert.Single(await RowsAsync(app, "job_failed")).State);

        await RunIrlAsync(app, scheduler, gate, crash: true);
        var again = Assert.Single(await RowsAsync(app, "job_failed"));
        Assert.Equal((NotificationStates.Open, 2), (again.State, again.Round));
        Assert.Equal([1, 2], (await EmailsAsync(app, again.Id)).Select(d => d.Round).Order());
    }

    private static async Task<List<NotificationDeliveryRow>> EmailsAsync(NidApp app, Guid notification)
    {
        await using var db = PostgresFixture.NewPlatformContext(app.ConnectionString);
        return await db.Deliveries.AsNoTracking().Where(d => d.NotificationId == notification && d.Channel == "email").ToListAsync();
    }
}
