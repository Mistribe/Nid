using Nid.Platform;
using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.Platform.Infrastructure.Persistence;
using Nid.Platform.Notifications;
using UnambitiousFx.Myelin;
using Nid.TestSupport;
using NodaTime;

namespace Nid.Platform.IntegrationTests;

/// <summary>Notifications design §2.3: the health source says what the admin pages already say about the backup and the syncs (today: 2025-10-15, 00:00 UTC).</summary>
public class HealthSourceTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string Hook = "https://discord.com/api/webhooks/777/householdTokenabcd";
    private static readonly Instant Now = NidApp.GoldenCaseToday.AtMidnight().InUtc().ToInstant();

    private sealed class Ok : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
    }

    private sealed class Throwing : ISyncHealth
    {
        public Task<SyncState?> ReadAsync(CancellationToken ct) => throw new InvalidOperationException("boom");
    }

    private sealed record Harness(NidApp App, string Path) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await App.DisposeAsync();
            File.Delete(Path);
        }

        public Task WriteAsync(string content) => File.WriteAllTextAsync(Path, content);
    }

    private async Task<Harness> StartAsync(bool configured = true, Action<IServiceCollection>? overrides = null)
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "nid-status-" + Guid.NewGuid().ToString("N") + ".json");
        var settings = configured ? new Dictionary<string, string> { ["Nid:Backup:StatusPath"] = path } : null;
        return new Harness(await NidApp.StartAsync(fixture, overrides, settings), path);
    }

    private static string Failed(string step = "dump", long? success = null)
        => $$"""{"last_result":"{{step}}","remote_result":"none","last_snapshot":"","last_success_at":{{(success?.ToString() ?? "null")}}}""";

    private static readonly long Yesterday = (Now - NodaTime.Duration.FromHours(21)).ToUnixTimeSeconds(); // 2025-10-14T03:00Z

    private static async Task EvaluateAsync(NidApp app)
    {
        var (_, failure) = await app.InvokeAsync(app.AdultA, new EvaluateNotificationsCommand(null, true));
        Assert.Null(failure);
    }

    private static async Task<List<NotificationRow>> RowsAsync(NidApp app, string kind)
    {
        await using var db = PostgresFixture.NewPlatformContext(app.ConnectionString);
        return await db.Notifications.AsNoTracking().Where(n => n.Kind == kind).ToListAsync();
    }

    private static async Task FxRunAsync(NidApp app, string status, Instant at)
    {
        await using var db = PostgresFixture.NewCoreContext(app.ConnectionString);
        db.FxSyncRuns.Add(new Nid.Core.Infrastructure.Persistence.FxSyncRunRow { Id = Guid.CreateVersion7(), Kind = "daily", StartedAt = at, FinishedAt = at, Status = status });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task A_failed_backup_is_one_critical_row_for_the_admin_and_resolves_when_fixed()
    {
        await using var h = await StartAsync();
        await h.WriteAsync(Failed(success: Yesterday));

        await EvaluateAsync(h.App);

        var row = Assert.Single(await RowsAsync(h.App, "backup_failed"));
        Assert.Equal((h.App.AdultA.Value, "critical", "backup_failed:2025-10-14T05:00", NotificationStates.Open), (row.PersonId, row.Level, row.Key, row.State));
        Assert.Empty(row.ItemIds);

        await h.WriteAsync($$"""{"last_result":"ok","remote_result":"none","last_snapshot":"x","last_success_at":{{Now.ToUnixTimeSeconds()}}}""");
        await EvaluateAsync(h.App);

        Assert.Equal(NotificationStates.Resolved, Assert.Single(await RowsAsync(h.App, "backup_failed")).State);
    }

    [Fact]
    public async Task A_failed_sync_after_a_success_is_an_info_row_for_the_admin()
    {
        await using var h = await StartAsync(configured: false);
        await FxRunAsync(h.App, "succeeded", Instant.FromUtc(2025, 10, 10, 23, 30)); // 01:30 on the 11th in Paris, the household's zone
        await FxRunAsync(h.App, "failed", Instant.FromUtc(2025, 10, 14, 6, 0));

        await EvaluateAsync(h.App);

        var row = Assert.Single(await RowsAsync(h.App, "sync_failed"));
        Assert.Equal((h.App.AdultA.Value, "info"), (row.PersonId, row.Level));
        Assert.Equal("sync_failed:fx:2025-10-11", row.Key);
    }

    [Fact]
    public async Task Unreadable_backup_status_is_one_signal()
    {
        await using var h = await StartAsync();
        await h.WriteAsync("{ garbage");

        await EvaluateAsync(h.App);
        await EvaluateAsync(h.App);

        var row = Assert.Single(await RowsAsync(h.App, "backup_failed"));
        Assert.Equal("backup_failed:never", row.Key);
    }

    [Fact]
    public async Task No_path_configured_is_no_row_and_no_error()
    {
        await using var h = await StartAsync(configured: false);
        await EvaluateAsync(h.App);
        Assert.Empty(await RowsAsync(h.App, "backup_failed"));
    }

    [Fact]
    public async Task One_failing_run_log_does_not_hide_the_backup()
    {
        await using var h = await StartAsync(overrides: s => s.AddScoped<ISyncHealth, Throwing>());
        await h.WriteAsync(Failed(success: Yesterday));

        await EvaluateAsync(h.App);

        Assert.Single(await RowsAsync(h.App, "backup_failed"));
    }

    [Fact]
    public async Task Only_the_critical_backup_notice_reaches_the_household_discord()
    {
        await using var h = await StartAsync(overrides: s =>
        {
            s.RemoveAll<SafeHttpClient>();
            s.AddSingleton(new SafeHttpClient(new HttpMessageInvoker(new Ok()), NidOutbound.Options(), NullLogger<SafeHttpClient>.Instance));
        });
        Assert.True((await h.App.RawAsync(h.App.AdultA, HttpMethod.Put, "/api/v1/admin/notifications/discord", new { url = Hook })).IsSuccessStatusCode);
        await h.WriteAsync(Failed(success: Yesterday));
        await FxRunAsync(h.App, "failed", Now);

        await EvaluateAsync(h.App);

        await using var db = PostgresFixture.NewPlatformContext(h.App.ConnectionString);
        var household = await db.Deliveries.AsNoTracking().Where(d => d.Channel == "discord_household").ToListAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Single(await RowsAsync(h.App, "sync_failed"));
        Assert.Single(household);
        var kind = await db.Notifications.AsNoTracking().Where(n => n.Id == household[0].NotificationId).Select(n => n.Kind).SingleAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("backup_failed", kind);
    }
}
