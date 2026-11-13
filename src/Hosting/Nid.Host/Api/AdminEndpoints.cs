using Microsoft.Extensions.Options;
using Nid.Host.Auth;
using System.Text.Json.Serialization;
using Nid.Core.Application.Admin;
using Nid.Core.Contracts;
using Nid.Platform.Admin;
using Nid.Platform.Audit;
using Nid.Platform.Notifications;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Host.Api;

/// <summary>The admin area (identity-and-access/administration-and-audit.md §2.2); every request is IAdminOnly.</summary>
public static class AdminEndpoints
{
    public sealed record PreRegisterBody(string DisplayName, string Email);

    /// <summary>Issue 215: <c>RevokeTokens</c> also revokes the adult's access tokens, for a suspected takeover.</summary>
    public sealed record PasswordLinkBody(bool RevokeTokens);

    /// <summary>The link's address, shown once (the secret is in its fragment), what it does and when it stops working.</summary>
    public sealed record PasswordLinkResult(string Url, string Purpose, NodaTime.Instant ExpiresAt);

    /// <summary>A key nobody reads is refused rather than silently dropped (400), like the preferences body.</summary>
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    public sealed record HouseholdBody(
        int? MaxAdults, string? ReferenceCurrency, bool? PricesEnabled, bool? IrlEnabled, bool? BankingEnabled, string? EnableBankingAppId,
        int? BankRetentionMonths, bool? ClearBankRetention, bool? InflationEnabled = null);

    public sealed record PersonIdResponse(Guid Id);

    public static IEndpointRouteBuilder MapAdminEndpoints(this IEndpointRouteBuilder routes)
    {
        var api = routes.MapGroup("/api/v1/admin").RequireAuthorization()
            .AddEndpointFilter<MalformedInputFilter>().AddEndpointFilter<ActorFilter>();

        api.MapGet("/users", async (IInvoker invoker, CancellationToken ct)
                => (await invoker.InvokeAsync(new ListAdultsQuery(), ct)).ToHttp(list => list))
            .Returns<IReadOnlyList<AdultView>>();

        api.MapPost("/users", async (PreRegisterBody body, IInvoker invoker, CancellationToken ct)
                => (await invoker.InvokeAsync(new PreRegisterAdultCommand(body.DisplayName, body.Email), ct))
                    .ToHttp(adult => adult, StatusCodes.Status201Created))
            .Returns<AdultView>(StatusCodes.Status201Created);

        api.MapDelete("/users/{id:guid}", async (Guid id, IInvoker invoker, CancellationToken ct)
                => (await invoker.InvokeAsync(new DeletePendingAdultCommand(new PersonId(id)), ct))
                    .ToHttp(person => new PersonIdResponse(person.Value)))
            .Returns<PersonIdResponse>();

        api.MapPost("/users/{id:guid}/revoke-sessions", async (Guid id, IInvoker invoker, CancellationToken ct)
                => (await invoker.InvokeAsync(new RevokeSessionsCommand(new PersonId(id)), ct)).ToHttp(adult => adult))
            .Returns<AdultView>();

        // Issue 215 (local-accounts.md §7, §8, §9): a one-time password link, shown once; unlocking a local account.
        if (routes.ServiceProvider.GetRequiredService<AuthSettings>().Local)
        {
            api.MapPost("/users/{id:guid}/password-link", async (Guid id, PasswordLinkBody body, IInvoker invoker,
                        IOptions<NidAuthOptions> options, CancellationToken ct)
                    => (await invoker.InvokeAsync(new CreatePasswordLinkCommand(new PersonId(id),
                            NodaTime.Duration.FromHours(options.Value.Local.Bounded().LinkLifetimeHours), body.RevokeTokens), ct))
                        .ToHttp(link => new PasswordLinkResult(LocalAuthEndpoints.LinkUrl(options.Value.PublicUrl, link.Token), link.Purpose, link.ExpiresAt),
                            StatusCodes.Status201Created))
                .Returns<PasswordLinkResult>(StatusCodes.Status201Created);

            api.MapDelete("/users/{id:guid}/password-link", async (Guid id, IInvoker invoker, CancellationToken ct)
                    => (await invoker.InvokeAsync(new RevokePasswordLinkCommand(new PersonId(id)), ct))
                        .ToHttp(person => new PersonIdResponse(person.Value)))
                .Returns<PersonIdResponse>();

            api.MapPost("/users/{id:guid}/unlock", async (Guid id, IInvoker invoker, CancellationToken ct)
                    => (await invoker.InvokeAsync(new UnlockAccountCommand(new PersonId(id)), ct))
                        .ToHttp(person => new PersonIdResponse(person.Value)))
                .Returns<PersonIdResponse>();
        }

        api.MapGet("/household", async (IInvoker invoker, CancellationToken ct)
                => (await invoker.InvokeAsync(new GetHouseholdSettingsQuery(), ct)).ToHttp(settings => settings))
            .Returns<HouseholdSettingsView>();

        api.MapPatch("/household", async (HouseholdBody body, IInvoker invoker, CancellationToken ct)
                => (await invoker.InvokeAsync(new UpdateHouseholdSettingsCommand(
                        body.MaxAdults, body.ReferenceCurrency, body.PricesEnabled, body.IrlEnabled, body.BankingEnabled, body.EnableBankingAppId,
                        body.BankRetentionMonths, body.ClearBankRetention ?? false, body.InflationEnabled), ct))
                    .ToHttp(settings => settings))
            .Returns<HouseholdSettingsView>();

        api.MapGet("/jobs", async (IInvoker invoker, CancellationToken ct)
                => (await invoker.InvokeAsync(new ListJobsQuery(), ct)).ToHttp(jobs => jobs))
            .Returns<IReadOnlyList<JobView>>();

        api.MapGet("/jobs/{key}", async (string key, int? page, IInvoker invoker, CancellationToken ct)
                => (await invoker.InvokeAsync(new GetJobQuery(key, page), ct)).ToHttp(job => job))
            .Returns<JobDetailView>();

        api.MapGet("/jobs/{key}/runs/{id:guid}", async (string key, Guid id, IInvoker invoker, CancellationToken ct)
                => (await invoker.InvokeAsync(new GetJobRunQuery(key, id), ct)).ToHttp(run => run))
            .Returns<JobRunDetailView>();

        api.MapGet("/system", async (IInvoker invoker, CancellationToken ct)
                => (await invoker.InvokeAsync(new GetSystemStatusQuery(), ct)).ToHttp(status => status))
            .Returns<SystemStatusView>();

        api.MapGet("/security-journal", async (string? @event, int? page, IInvoker invoker, CancellationToken ct)
                => (await invoker.InvokeAsync(new SecurityJournalQuery(@event, page), ct)).ToHttp(p => p))
            .Returns<AdminSecurityEventPage>();

        api.MapGet("/notifications", async (int? page, IInvoker invoker, CancellationToken ct)
                => (await invoker.InvokeAsync(new GetAdminNotificationsQuery(page), ct)).ToHttp(view => view))
            .Returns<AdminNotificationsView>();

        api.MapPost("/notifications/test", async (NotificationEndpoints.TestChannelBody body, IInvoker invoker, CancellationToken ct)
                => (await invoker.InvokeAsync(new AdminTestChannelCommand(body.Channel ?? ""), ct)).ToHttp(result => result))
            .Returns<TestChannelResult>()
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        api.MapPut("/notifications/discord", async (NotificationEndpoints.DiscordWebhookBody body, IInvoker invoker, CancellationToken ct)
                => (await invoker.InvokeAsync(new SetHouseholdWebhookCommand(body.Url ?? ""), ct)).ToHttp(v => v))
            .Returns<WebhookView>();

        api.MapDelete("/notifications/discord", async (IInvoker invoker, CancellationToken ct)
                => NotificationEndpoints.NoContent(await invoker.InvokeAsync(new RemoveHouseholdWebhookCommand(), ct)))
            .ReturnsNoContent();

        return routes;
    }
}
