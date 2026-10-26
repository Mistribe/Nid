using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using MailKit.Net.Smtp;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MimeKit;
using Nid.Core.Contracts;
using Nid.Host.Notifications;
using Nid.Platform.Contracts;
using Nid.Platform.Infrastructure.Persistence;
using Nid.TestSupport;
using NodaTime;
using UnambitiousFx.Functional;

namespace Nid.Subscriptions.IntegrationTests.Notifications;

/// <summary>Notifications design §5.3 and §6.4: the email channel, its test message and the admin status.</summary>
public class EmailChannelTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string TestRoute = "/api/v1/platform/notifications/test";
    private const string AdminRoute = "/api/v1/admin/notifications";

    private sealed class RecordingTransport : ISmtpTransport
    {
        public List<MimeMessage> Sent { get; } = [];

        public Exception? Throws { get; set; }

        public Task SendAsync(MimeMessage message, CancellationToken ct)
        {
            if (Throws is not null)
                throw Throws;
            Sent.Add(message);
            return Task.CompletedTask;
        }
    }

    private static readonly Dictionary<string, string> Smtp = new()
    {
        ["Nid:Smtp:Host"] = "smtp.test.localhost",
        ["Nid:Smtp:From"] = "Nid <nid@test.localhost>",
    };

    private sealed class SettableClock(Instant now) : IClock
    {
        public Instant Now { get; set; } = now;

        public Instant GetCurrentInstant() => Now;
    }

    private readonly SettableClock clock = new(Instant.FromUtc(2025, 10, 15, 8, 0));

    private static readonly RenderedMessage Message = new("Nid : Un bail arrive à échéance", "texte", "<p>html</p>");

    private static ChannelTarget Target() => new(new PersonId(Guid.NewGuid()), "a@example.test");

    private static EmailChannel ChannelOf(RecordingTransport transport, SmtpOptions? options = null)
        => new(options ?? new SmtpOptions { Host = "smtp.test.localhost", From = "Nid <nid@test.localhost>" }, transport);

    private static (string Code, object? Status) FailureOf(Result result)
    {
        Assert.True(result.TryGetFailure(out var failure));
        return (failure!.Code, NotificationChannelCodes.StatusOf(failure));
    }

    private async Task<(NidApp App, RecordingTransport Transport)> StartAsync(bool configured = true)
    {
        var transport = new RecordingTransport();
        var app = await NidApp.StartAsync(
            fixture,
            services =>
            {
                services.RemoveAll<ISmtpTransport>();
                services.AddSingleton<ISmtpTransport>(transport);
                services.AddSingleton<IClock>(clock);
            },
            configured ? Smtp : null);
        await using (var core = PostgresFixture.NewCoreContext(app.ConnectionString))
        {
            var row = await core.Persons.SingleAsync(p => p.Id == app.AdultA.Value);
            row.Email = "a@example.test";
            await core.SaveChangesAsync();
        }

        return (app, transport);
    }

    private static async Task<JsonElement> JsonOf(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>();

    private static async Task AssertFieldAsync(HttpResponseMessage response, string path, string code)
    {
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var error = Assert.Single((await JsonOf(response)).GetProperty("errors").EnumerateArray());
        Assert.Equal((path, code), (error.GetProperty("path").GetString(), error.GetProperty("code").GetString()));
    }

    [Fact]
    public async Task Sends_through_the_transport()
    {
        var transport = new RecordingTransport();

        var result = await ChannelOf(transport).SendAsync(Message, Target(), CancellationToken.None);

        Assert.True(result.IsSuccess);
        var mail = Assert.Single(transport.Sent);
        Assert.Equal("a@example.test", Assert.Single(mail.To.Mailboxes).Address);
        var from = Assert.Single(mail.From.Mailboxes);
        Assert.Equal(("nid@test.localhost", "Nid"), (from.Address, from.Name));
        Assert.Equal("Nid : Un bail arrive à échéance", mail.Subject);
        Assert.Equal("texte", mail.TextBody);
        Assert.Equal("<p>html</p>", mail.HtmlBody);
    }

    [Fact]
    public async Task Maps_failures()
    {
        var transport = new RecordingTransport();
        var channel = ChannelOf(transport);

        async Task<(string Code, object? Status)> FailWithAsync(Exception e)
        {
            transport.Throws = e;
            return FailureOf(await channel.SendAsync(Message, Target(), CancellationToken.None));
        }

        Assert.Equal((NotificationChannelCodes.Rejected, 550),
            await FailWithAsync(new SmtpCommandException(SmtpErrorCode.RecipientNotAccepted, SmtpStatusCode.MailboxUnavailable, "no such user")));
        Assert.Equal((NotificationChannelCodes.Unreachable, 421),
            await FailWithAsync(new SmtpCommandException(SmtpErrorCode.UnexpectedStatusCode, SmtpStatusCode.ServiceNotAvailable, "busy")));
        Assert.Equal((NotificationChannelCodes.Unreachable, 450),
            await FailWithAsync(new SmtpCommandException(SmtpErrorCode.RecipientNotAccepted, SmtpStatusCode.MailboxBusy, "later")));
        Assert.Equal(NotificationChannelCodes.Unreachable, (await FailWithAsync(new SocketException())).Code);
        Assert.Equal(NotificationChannelCodes.Unreachable, (await FailWithAsync(new TimeoutException())).Code);
        Assert.Equal(NotificationChannelCodes.Unreachable, (await FailWithAsync(new IOException("reset"))).Code);
        Assert.Equal(NotificationChannelCodes.Unreachable, (await FailWithAsync(new OperationCanceledException())).Code);
        Assert.Equal(NotificationChannelCodes.Rejected, (await FailWithAsync(new SmtpProtocolException("bad reply"))).Code);
        Assert.Equal(NotificationChannelCodes.Rejected, (await FailWithAsync(new MailKit.Security.AuthenticationException("bad login"))).Code);
        Assert.Equal(NotificationChannelCodes.Rejected, (await FailWithAsync(new MailKit.Security.SslHandshakeException("bad certificate", new IOException("tls")))).Code);
    }

    [Fact]
    public async Task Test_endpoints_report_rejected_for_a_wrong_password()
    {
        var (app, transport) = await StartAsync();
        await using var _ = app;
        transport.Throws = new MailKit.Security.AuthenticationException("535 bad credentials");

        var personal = await JsonOf(await app.RawAsync(app.AdultA, HttpMethod.Post, TestRoute, new { channel = "email" }));
        clock.Now += NodaTime.Duration.FromSeconds(30); // one test per person and channel every 30 s
        var admin = await JsonOf(await app.RawAsync(app.AdultA, HttpMethod.Post, AdminRoute + "/test", new { channel = "email" }));

        foreach (var body in new[] { personal, admin })
        {
            Assert.False(body.GetProperty("sent").GetBoolean());
            Assert.Equal(NotificationChannelCodes.Rejected, body.GetProperty("error").GetString());
        }
    }

    [Fact]
    public async Task A_cancelled_caller_is_not_a_failure()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var transport = new RecordingTransport { Throws = new OperationCanceledException(cts.Token) };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ChannelOf(transport).SendAsync(Message, Target(), cts.Token));
    }

    [Fact]
    public async Task An_address_that_is_not_one_is_rejected()
    {
        var transport = new RecordingTransport();

        var result = await ChannelOf(transport).SendAsync(Message, new ChannelTarget(new PersonId(Guid.NewGuid()), "not an address"), CancellationToken.None);

        Assert.Equal(NotificationChannelCodes.Rejected, FailureOf(result).Code);
        Assert.Empty(transport.Sent);
    }

    [Fact]
    public void The_channel_is_available_with_a_host_and_a_sender()
    {
        var transport = new RecordingTransport();

        Assert.True(ChannelOf(transport).IsAvailable);
        Assert.False(ChannelOf(transport, new SmtpOptions()).IsAvailable);
        Assert.False(ChannelOf(transport, new SmtpOptions { Host = "smtp.test.localhost" }).IsAvailable);
    }

    [Fact]
    public async Task A_host_without_a_sender_stops_the_startup()
    {
        var e = await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            // The host is built on first use.
            await using var app = await NidApp.StartAsync(fixture, settings: new Dictionary<string, string> { ["Nid:Smtp:Host"] = "smtp.test.localhost" });
            _ = app.Services;
        });

        var messages = new List<string>();
        for (Exception? inner = e; inner is not null; inner = inner.InnerException)
            messages.Add(inner.Message);
        Assert.Contains(messages, m => m.Contains("Nid:Smtp:From", StringComparison.Ordinal));
    }

    [Fact]
    public void An_email_host_needs_the_public_url()
    {
        var options = new SmtpOptions { Host = "smtp.test.localhost", From = "nid@test.localhost" };

        var e = Assert.Throws<InvalidOperationException>(() => options.Validate(""));
        Assert.Contains("Auth:PublicUrl", e.Message, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => options.Validate(null));
        options.Validate("https://nid.example.org");
        new SmtpOptions().Validate(""); // no SMTP, no need for a link base
    }

    [Fact]
    public async Task Test_endpoint()
    {
        var (app, transport) = await StartAsync();
        await using var _ = app;

        var sent = await app.RawAsync(app.AdultA, HttpMethod.Post, TestRoute, new { channel = "email" });

        Assert.Equal(HttpStatusCode.OK, sent.StatusCode);
        Assert.True((await JsonOf(sent)).GetProperty("sent").GetBoolean());
        var mail = Assert.Single(transport.Sent);
        Assert.Equal("a@example.test", Assert.Single(mail.To.Mailboxes).Address);
        Assert.Equal("nid@test.localhost", Assert.Single(mail.From.Mailboxes).Address);
        Assert.Equal("Nid : message de test", mail.Subject);

        await AssertFieldAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, TestRoute, new { channel = "sms" }),
            "channel", "notifications.unknown_channel");
        await AssertFieldAsync(await app.RawAsync(app.AdultB, HttpMethod.Post, TestRoute, new { channel = "email" }),
            "channel", "notifications.email_unavailable");
        Assert.Single(transport.Sent);
    }

    [Fact]
    public async Task A_second_test_within_30_seconds_is_throttled()
    {
        var (app, transport) = await StartAsync();
        await using var _ = app;
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Post, TestRoute, new { channel = "email" })).StatusCode);

        clock.Now += NodaTime.Duration.FromSeconds(29);
        foreach (var route in new[] { TestRoute, AdminRoute + "/test" })
        {
            var refused = await app.RawAsync(app.AdultA, HttpMethod.Post, route, new { channel = "email" });
            Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
            Assert.Equal("notifications.test_throttled", (await JsonOf(refused)).GetProperty("code").GetString());
        }
        Assert.Single(transport.Sent); // a refused test never reaches the channel

        clock.Now += NodaTime.Duration.FromSeconds(1);
        var again = await app.RawAsync(app.AdultA, HttpMethod.Post, AdminRoute + "/test", new { channel = "email" });
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(2, transport.Sent.Count);
    }

    [Fact]
    public async Task Test_endpoint_reports_a_failed_send_without_throwing()
    {
        var (app, transport) = await StartAsync();
        await using var _ = app;
        transport.Throws = new SmtpCommandException(SmtpErrorCode.RecipientNotAccepted, SmtpStatusCode.MailboxUnavailable, "no such user");

        var response = await app.RawAsync(app.AdultA, HttpMethod.Post, TestRoute, new { channel = "email" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await JsonOf(response);
        Assert.False(body.GetProperty("sent").GetBoolean());
        Assert.Equal(NotificationChannelCodes.Rejected, body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Test_endpoint_refuses_when_smtp_is_off()
    {
        var (app, transport) = await StartAsync(configured: false);
        await using var _ = app;

        await AssertFieldAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, TestRoute, new { channel = "email" }),
            "channel", "notifications.email_unavailable");
        Assert.Empty(transport.Sent);
    }

    [Fact]
    public async Task Admin_status_and_failures()
    {
        var (app, transport) = await StartAsync();
        await using var _ = app;
        var now = Instant.FromUtc(2025, 10, 15, 0, 0);
        await using (var db = PostgresFixture.NewPlatformContext(app.ConnectionString))
        {
            NotificationDeliveryRow Delivery(Guid notification, string state, Instant at, string? error, int round)
                => new()
                {
                    Id = Guid.NewGuid(), NotificationId = notification, PersonId = app.AdultB.Value, Channel = "email", State = state,
                    Attempts = 6, LastError = error, CreatedAt = at, Round = round,
                };
            var notification = new NotificationRow
            {
                Id = Guid.NewGuid(), PersonId = app.AdultB.Value, Kind = "tenancy_end", Key = "tenancy_end:x", Level = "action",
                NotifyOn = new LocalDate(2025, 10, 1), State = "open", CreatedAt = now - NodaTime.Duration.FromDays(10),
            };
            db.Notifications.Add(notification);
            db.Deliveries.AddRange(
                Delivery(notification.Id, "failed", now - NodaTime.Duration.FromDays(5), "notifications.channel_unreachable 421", 1),
                Delivery(notification.Id, "failed", now - NodaTime.Duration.FromDays(40), "notifications.channel_rejected 550", 2),
                Delivery(notification.Id, "sent", now - NodaTime.Duration.FromDays(1), null, 3),
                Delivery(notification.Id, "pending", now - NodaTime.Duration.FromDays(1), "notifications.channel_unreachable 421", 4));
            await db.SaveChangesAsync();
        }

        Assert.Equal(HttpStatusCode.Forbidden, (await app.RawAsync(app.AdultB, HttpMethod.Get, AdminRoute)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await app.RawAsync(app.AdultB, HttpMethod.Post, AdminRoute + "/test", new { channel = "email" })).StatusCode);

        var raw = await app.GetRawAsync(app.AdultA, AdminRoute);
        var view = JsonDocument.Parse(raw).RootElement;
        Assert.True(view.GetProperty("smtpConfigured").GetBoolean());
        Assert.Equal("smtp.test.localhost", view.GetProperty("smtpHost").GetString());
        var failure = Assert.Single(view.GetProperty("failures").EnumerateArray());
        Assert.Equal(
            ["at", "channel", "error", "id", "kind"],
            failure.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(("email", "tenancy_end", "notifications.channel_unreachable 421"),
            (failure.GetProperty("channel").GetString(), failure.GetProperty("kind").GetString(), failure.GetProperty("error").GetString()));
        Assert.Equal(new DateTimeOffset(2025, 10, 10, 0, 0, 0, TimeSpan.Zero), failure.GetProperty("at").GetDateTimeOffset());

        var sent = await app.RawAsync(app.AdultA, HttpMethod.Post, AdminRoute + "/test", new { channel = "email" });
        Assert.True((await JsonOf(sent)).GetProperty("sent").GetBoolean());
        Assert.Equal("a@example.test", Assert.Single(Assert.Single(transport.Sent).To.Mailboxes).Address);
    }

    [Fact]
    public async Task Admin_status_without_smtp()
    {
        var (app, _) = await StartAsync(configured: false);
        await using var _ = app;

        var view = await app.GetAsync<JsonElement>(app.AdultA, AdminRoute);

        Assert.False(view.GetProperty("smtpConfigured").GetBoolean());
        Assert.Equal(JsonValueKind.Null, view.GetProperty("smtpHost").ValueKind);
        Assert.Empty(view.GetProperty("failures").EnumerateArray());
    }
}
