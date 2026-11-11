using Nid.Platform;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using MimeKit;
using Nid.Core.Contracts;
using Nid.Host.Notifications;
using Nid.Platform.Contracts;
using Nid.Platform.Infrastructure.Persistence;
using Nid.Platform.Notifications;
using UnambitiousFx.Myelin;
using Nid.TestSupport;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Platform.IntegrationTests;

/// <summary>Notifications design §5.3: the Discord channel, over a recording handler instead of the network.</summary>
public class DiscordChannelTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string Hook = "https://discord.com/api/webhooks/123456/secretTokenwxyz";
    private const string PersonalRoute = "/api/v1/platform/notifications/discord";
    private const string PreferencesRoute = "/api/v1/platform/notifications/preferences";
    private const string TestRoute = "/api/v1/platform/notifications/test";

    private static readonly NotificationKind Action = new("fake_action", "finding", NotificationLevel.Action, "fake", null);
    private static readonly NotificationKind Info = new("fake_info", "finding", NotificationLevel.Info, "fake", null);

    private static readonly Instant Start = Instant.FromUtc(2025, 10, 15, 8, 0);

    private sealed class Recorder : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Answer { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.NoContent);

        public List<(Uri Url, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.RequestUri!, await request.Content!.ReadAsStringAsync(cancellationToken)));
            return Answer(request);
        }
    }

    private sealed class SettableClock(Instant now) : IClock
    {
        public Instant Now { get; set; } = now;

        public Instant GetCurrentInstant() => Now;
    }

    private readonly SettableClock clock = new(Start);

    private sealed class FakeSource : INotificationSource
    {
        public string Name => "fake";

        public IReadOnlyList<NotificationKind> Kinds { get; } = [Action, Info];

        public List<Signal> Signals { get; } = [];

        public Task<Result<IReadOnlyList<Signal>>> SignalsAsync(PersonId reader, LocalDate today, LocalDate horizon, CancellationToken ct)
            => Task.FromResult(Result.Success<IReadOnlyList<Signal>>(Signals.ToList()));
    }

    private sealed class NoopTransport : ISmtpTransport
    {
        public Task SendAsync(MimeMessage message, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed record Harness(NidApp App, FakeSource Source, Recorder Discord) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => App.DisposeAsync();
    }

    private static SafeHttpClient Http(Recorder handler)
        => new(new HttpMessageInvoker(handler), NidOutbound.Options(), NullLogger<SafeHttpClient>.Instance);

    private async Task<Harness> StartAsync(bool email = false, bool webhook = true)
    {
        var source = new FakeSource();
        var discord = new Recorder();
        var app = await NidApp.StartAsync(
            fixture,
            services =>
            {
                services.AddSingleton<IClock>(clock);
                services.RemoveAll<INotificationSource>();
                services.AddSingleton<INotificationSource>(source);
                services.RemoveAll<SafeHttpClient>();
                services.AddSingleton(Http(discord));
                services.RemoveAll<ISmtpTransport>();
                services.AddSingleton<ISmtpTransport, NoopTransport>();
            },
            email ? new Dictionary<string, string> { ["Nid:Smtp:Host"] = "smtp.test.localhost", ["Nid:Smtp:From"] = "Nid <nid@test.localhost>" } : null);
        await using (var core = PostgresFixture.NewCoreContext(app.ConnectionString))
        {
            var row = await core.Persons.SingleAsync(p => p.Id == app.AdultA.Value);
            row.Email = "a@example.test";
            await core.SaveChangesAsync();
        }

        var current = await app.GetAsync<JsonElement>(app.AdultA, PreferencesRoute);
        var saved = await app.RawAsync(app.AdultA, HttpMethod.Put, PreferencesRoute, new
        {
            emailEnabled = email, action = "immediate", info = "digest", frequency = "daily", weekday = 1,
            leadDays = new Dictionary<string, int>(), version = current.GetProperty("version").GetInt32(),
        });
        Assert.True(saved.IsSuccessStatusCode);
        if (webhook)
            Assert.True((await app.RawAsync(app.AdultA, HttpMethod.Put, PersonalRoute, new { url = Hook })).IsSuccessStatusCode);
        return new Harness(app, source, discord);
    }

    private static Signal SignalOf(NotificationKind kind, string key, ItemId item)
        => new(key, kind.Kind, [item], null, new Dictionary<string, string>());

    private static async Task EvaluateAsync(Harness h)
    {
        var (_, failure) = await h.App.InvokeAsync(h.App.AdultA, new EvaluateNotificationsCommand(h.App.AdultA, true));
        Assert.Null(failure);
    }

    private static async Task DispatchAsync(Harness h)
    {
        var (_, failure) = await h.App.InvokeAsync(h.App.AdultA, new DispatchNotificationsCommand());
        Assert.Null(failure);
    }

    /// <summary>One dispatch as the Quartz job runs it: the system actor, not a person.</summary>
    private static async Task DispatchAsSystemAsync(Harness h)
    {
        await using var scope = h.App.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
        var result = await scope.ServiceProvider.GetRequiredService<IInvoker>().InvokeAsync(new DispatchNotificationsCommand());
        Assert.True(result.IsSuccess);
    }

    private static async Task<List<NotificationDeliveryRow>> DeliveriesAsync(Harness h)
    {
        await using var db = PostgresFixture.NewPlatformContext(h.App.ConnectionString);
        return await db.Deliveries.AsNoTracking().OrderBy(d => d.CreatedAt).ThenBy(d => d.Round).ThenBy(d => d.Id).ToListAsync();
    }

    private static async Task<string> WebhookStatusAsync(Harness h)
    {
        await using var db = PostgresFixture.NewPlatformContext(h.App.ConnectionString);
        return (await db.Webhooks.AsNoTracking().SingleAsync(w => w.PersonId == h.App.AdultA.Value)).Status;
    }

    private static async Task<ItemId> ItemAsync(Harness h, string name = "Netflix")
        => await h.App.NewSubscriptionAsync(name, h.App.AdultA, Visibility.Shared);

    private static async Task<(string Code, object? Status)> FailureOfAsync(Recorder handler, HttpResponseMessage answer)
    {
        handler.Answer = _ => answer;
        var result = await new DiscordChannel("discord_person", Http(handler)).SendAsync(new RenderedMessage("t", "d", "<p>d</p>"), new ChannelTarget(new PersonId(Guid.NewGuid()), Hook), default);
        Assert.True(result.TryGetFailure(out var failure));
        return (failure!.Code, NotificationChannelCodes.StatusOf(failure));
    }

    [Fact]
    public async Task Payload_never_pings()
    {
        var handler = new Recorder();

        var result = await new DiscordChannel("discord_person", Http(handler)).SendAsync(
            new RenderedMessage("Nid : @everyone", "ping <@123>", "<p>x</p>"), new ChannelTarget(new PersonId(Guid.NewGuid()), Hook), TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        var (url, body) = Assert.Single(handler.Requests);
        Assert.Equal(Hook, url.ToString());
        var json = JsonDocument.Parse(body).RootElement;
        Assert.Empty(json.GetProperty("allowed_mentions").GetProperty("parse").EnumerateArray());
        var embed = Assert.Single(json.GetProperty("embeds").EnumerateArray());
        Assert.Equal(("Nid : @everyone", "ping <@123>"), (embed.GetProperty("title").GetString(), embed.GetProperty("description").GetString()));
        Assert.False(embed.TryGetProperty("url", out _));
    }

    [Fact]
    public async Task Long_text_is_cut()
    {
        var handler = new Recorder();

        await new DiscordChannel("discord_person", Http(handler)).SendAsync(
            new RenderedMessage(new string('t', 300), new string('x', 5000), ""), new ChannelTarget(new PersonId(Guid.NewGuid()), Hook), TestContext.Current.CancellationToken);

        var embed = JsonDocument.Parse(handler.Requests.Single().Body).RootElement.GetProperty("embeds")[0];
        var description = embed.GetProperty("description").GetString()!;
        Assert.Equal(4096, description.Length);
        Assert.EndsWith("…", description, StringComparison.Ordinal);
        Assert.Equal(256, embed.GetProperty("title").GetString()!.Length);
    }

    [Fact]
    public async Task Status_mapping()
    {
        var handler = new Recorder();

        Assert.True((await new DiscordChannel("discord_person", Http(handler)).SendAsync(
            new RenderedMessage("t", "d", ""), new ChannelTarget(new PersonId(Guid.NewGuid()), Hook), TestContext.Current.CancellationToken)).IsSuccess);
        foreach (var (status, code) in new[]
                 {
                     (404, NotificationChannelCodes.Broken), (401, NotificationChannelCodes.Broken), (403, NotificationChannelCodes.Broken),
                     (429, NotificationChannelCodes.Unreachable), (503, NotificationChannelCodes.Unreachable), (400, NotificationChannelCodes.Rejected),
                 })
        {
            var failed = await FailureOfAsync(handler, new HttpResponseMessage((HttpStatusCode)status));
            Assert.Equal(code, failed.Code);
            Assert.Equal<object?>(status, failed.Status);
        }

        handler.Answer = _ => throw new HttpRequestException("down");
        var down = await new DiscordChannel("discord_person", Http(handler)).SendAsync(
            new RenderedMessage("t", "d", ""), new ChannelTarget(new PersonId(Guid.NewGuid()), Hook), TestContext.Current.CancellationToken);
        Assert.True(down.TryGetFailure(out var failure));
        Assert.Equal(NotificationChannelCodes.Unreachable, failure!.Code);
    }

    [Fact]
    public async Task A_host_the_safe_client_refuses_is_rejected()
    {
        var channel = new DiscordChannel(
            "discord_person",
            new SafeHttpClient(new HttpMessageInvoker(new Recorder()), new MyelinOptions { AllowedHosts = ["www.ecb.europa.eu"] }, NullLogger<SafeHttpClient>.Instance));

        var result = await channel.SendAsync(new RenderedMessage("t", "d", ""), new ChannelTarget(new PersonId(Guid.NewGuid()), Hook), TestContext.Current.CancellationToken);

        Assert.True(result.TryGetFailure(out var failure));
        Assert.Equal(NotificationChannelCodes.Rejected, failure!.Code);
    }

    [Fact]
    public async Task A_404_breaks_the_webhook_and_skips_the_rest()
    {
        await using var h = await StartAsync();
        h.Discord.Answer = _ => new HttpResponseMessage(HttpStatusCode.NotFound);
        var item = await ItemAsync(h);
        h.Source.Signals.Add(SignalOf(Action, "a:1", item));
        h.Source.Signals.Add(SignalOf(Action, "a:2", item));
        await EvaluateAsync(h);

        await DispatchAsSystemAsync(h);

        var deliveries = await DeliveriesAsync(h);
        Assert.Equal(["failed", "skipped"], deliveries.Select(d => d.State).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal("notifications.channel_broken 404", deliveries.Single(d => d.State == "failed").LastError);
        Assert.Equal("broken", await WebhookStatusAsync(h));
        Assert.Single(h.Discord.Requests);

        h.Source.Signals.Add(SignalOf(Action, "a:3", await ItemAsync(h, "Disney")));
        await EvaluateAsync(h);
        Assert.Equal(2, (await DeliveriesAsync(h)).Count(d => d.Channel == "discord_person"));
    }

    [Fact]
    public async Task Failure_keeps_no_url()
    {
        await using var h = await StartAsync();
        h.Discord.Answer = _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        h.Source.Signals.Add(SignalOf(Action, "a:1", await ItemAsync(h)));
        await EvaluateAsync(h);

        await DispatchAsync(h);

        var delivery = Assert.Single(await DeliveriesAsync(h));
        Assert.Equal("notifications.channel_unreachable 503", delivery.LastError);
        Assert.DoesNotContain("secretToken", delivery.LastError, StringComparison.Ordinal);
        Assert.DoesNotContain("discord", delivery.LastError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Personal_discord_follows_the_rhythm()
    {
        await using var h = await StartAsync(email: true);
        var item = await ItemAsync(h);
        h.Source.Signals.Add(SignalOf(Action, "a:1", item));
        h.Source.Signals.Add(SignalOf(Info, "i:1", item));

        await EvaluateAsync(h);

        var now = await DeliveriesAsync(h);
        Assert.Equal(["discord_person", "email"], now.Select(d => d.Channel).Order(StringComparer.Ordinal).ToArray());
        Assert.All(now, d => Assert.NotNull(d.NotificationId));

        var (_, failure) = await h.App.InvokeAsync(h.App.AdultA, new SendDigestsCommand(true), TestContext.Current.CancellationToken);
        Assert.Null(failure);

        var digests = (await DeliveriesAsync(h)).Where(d => d.DigestId is not null).ToList();
        Assert.Equal(["discord_person", "email"], digests.Select(d => d.Channel).Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task Test_sends_to_the_webhook()
    {
        await using var h = await StartAsync();

        var sent = await h.App.RawAsync(h.App.AdultA, HttpMethod.Post, TestRoute, new { channel = "discord_person" });

        Assert.Equal(HttpStatusCode.OK, sent.StatusCode);
        Assert.True((await sent.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: TestContext.Current.CancellationToken)).GetProperty("sent").GetBoolean());
        Assert.Equal(Hook, Assert.Single(h.Discord.Requests).Url.ToString());

        var none = await h.App.RawAsync(h.App.AdultB, HttpMethod.Post, TestRoute, new { channel = "discord_person" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, none.StatusCode);
        var error = Assert.Single((await none.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: TestContext.Current.CancellationToken)).GetProperty("errors").EnumerateArray());
        Assert.Equal(("channel", "notifications.discord_unavailable"), (error.GetProperty("path").GetString(), error.GetProperty("code").GetString()));

        h.Discord.Answer = _ => new HttpResponseMessage(HttpStatusCode.NotFound);
        clock.Now += NodaTime.Duration.FromSeconds(30); // one test per person and channel every 30 s
        var gone = await (await h.App.RawAsync(h.App.AdultA, HttpMethod.Post, TestRoute, new { channel = "discord_person" })).Content.ReadFromJsonAsync<JsonElement>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(gone.GetProperty("sent").GetBoolean());
        Assert.Equal(NotificationChannelCodes.Broken, gone.GetProperty("error").GetString());
        Assert.Equal("broken", await WebhookStatusAsync(h));
    }
}
