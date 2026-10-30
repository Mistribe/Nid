using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using Nid.Banking.Application;
using Nid.Banking.Application.Sync;
using Nid.Banking.Contracts;
using Nid.Core.Contracts;
using Nid.Host.Auth;
using UnambitiousFx.Synapse.Abstractions;
using static Nid.Host.Api.SubscriptionEndpoints;

namespace Nid.Host.Api;

/// <summary>Bank sync spec §3.1 and §8: the banks, the person's connections, the bank's callback and the bank accounts.</summary>
public static class BankingEndpoints
{
    public const string CallbackPath = "/api/v1/banking/callback";

    /// <summary>Where the browser lands after the bank, success or refusal; the page reads <c>connected</c> or <c>error</c>.</summary>
    private const string BanksPage = "/settings/banks";

    public static IEndpointRouteBuilder MapBankingEndpoints(this IEndpointRouteBuilder routes)
    {
        var api = routes.MapGroup("/api/v1/banking").RequireAuthorization()
            .AddEndpointFilter<MalformedInputFilter>().AddEndpointFilter<ActorFilter>();

        api.MapGet("/status", async (IInvoker invoker, CancellationToken ct)
            => (await invoker.InvokeAsync(new GetBankingStatusQuery(), ct)).ToHttp(status => status))
            .Returns<BankingStatusView>();

        api.MapGet("/aspsps", async (IInvoker invoker, CancellationToken ct)
            => (await invoker.InvokeAsync(new ListAspspsQuery(), ct)).ToHttp(banks => banks))
            .Returns<IReadOnlyList<AspspView>>();

        api.MapPost("/connections", async (ConnectBody body, IOptions<NidAuthOptions> auth, IInvoker invoker, CancellationToken ct)
            => (await invoker.InvokeAsync(new StartConnectionCommand(body.AspspName ?? "", Callback(auth)), ct))
                .ToHttp(started => started, StatusCodes.Status201Created))
            .Returns<ConnectionStarted>(StatusCodes.Status201Created);

        api.MapPost("/connections/{id:guid}/renew", async (Guid id, IOptions<NidAuthOptions> auth, IInvoker invoker, CancellationToken ct)
            => (await invoker.InvokeAsync(new RenewConnectionCommand(id, Callback(auth)), ct)).ToHttp(started => started))
            .Returns<ConnectionStarted>();

        // The bank sends the browser back here: a navigation, answered by a redirect to the page, never by a body.
        api.MapGet("/callback", async (string? state, string? code, string? error, IInvoker invoker, CancellationToken ct) =>
            {
                var result = await invoker.InvokeAsync(new CompleteConnectionCommand(state ?? "", code, error), ct);
                return result.TryGetValue(out var id)
                    ? Results.Redirect($"{BanksPage}?connected={id}")
                    : Results.Redirect($"{BanksPage}?error={Uri.EscapeDataString(result.TryGetFailure(out var failure) ? failure!.Code : "ERROR")}");
            })
            .ExcludeFromDescription();

        api.MapGet("/connections", async (IInvoker invoker, CancellationToken ct)
            => (await invoker.InvokeAsync(new ListMyConnectionsQuery(), ct)).ToHttp(connections => connections))
            .Returns<IReadOnlyList<ConnectionView>>();

        api.MapDelete("/connections/{id:guid}", async (Guid id, IInvoker invoker, CancellationToken ct) =>
            {
                // A 204 carries no body: ToHttp would write one.
                var result = await invoker.InvokeAsync(new DeleteConnectionCommand(id), ct);
                return result.TryGetValue(out _) ? Results.NoContent() : result.ToHttp(v => v);
            })
            .ReturnsNoContent();

        api.MapGet("/accounts", async (Guid? envelopeId, IInvoker invoker, CancellationToken ct)
            => (await invoker.InvokeAsync(new ListBankAccountsQuery(envelopeId is { } e ? new ItemId(e) : null), ct)).ToHttp(accounts => accounts))
            .Returns<IReadOnlyList<BankAccountView>>();

        api.MapPatch("/accounts/{id:guid}", async (Guid id, BankAccountBody body, IInvoker invoker, CancellationToken ct)
            => (await invoker.InvokeAsync(new UpdateBankAccountCommand(
                    id, body.EnvelopeId, body.ClearEnvelope ?? false, body.CardOfAccountId, body.ClearCard ?? false, body.AutoApplyBalance, body.Ignored), ct))
                .ToHttp(x => new IdResponse(x)))
            .Returns<IdResponse>();

        // Read now, in the request: the bank sees the person's own browser (Psu-Ip-Address, Psu-User-Agent), as UseForwardedHeaders resolved it.
        api.MapPost("/sync", async (SyncBody body, HttpContext http, IInvoker invoker, CancellationToken ct)
            => (await invoker.InvokeAsync(new SyncMineCommand(body.ConnectionId, new PsuHeaders(
                    http.Connection.RemoteIpAddress?.ToString(), http.Request.Headers.UserAgent is { Count: > 0 } agent ? agent.ToString() : null)), ct))
                .ToHttp(requested => requested, StatusCodes.Status202Accepted))
            .Returns<BankSyncRequested>(StatusCodes.Status202Accepted);

        var admin = routes.MapGroup("/api/v1/admin/banking").RequireAuthorization()
            .AddEndpointFilter<MalformedInputFilter>().AddEndpointFilter<ActorFilter>();

        admin.MapGet("/runs", async (IInvoker invoker, CancellationToken ct)
            => (await invoker.InvokeAsync(new GetBankSyncRunsQuery(), ct)).ToHttp(runs => runs))
            .Returns<IReadOnlyList<BankSyncRunView>>();

        return routes;
    }

    private static string Callback(IOptions<NidAuthOptions> auth) => auth.Value.PublicUrl.TrimEnd('/') + CallbackPath;

    public sealed record ConnectBody(string? AspspName);

    /// <summary>A field left out is left as it is; a key nobody reads is refused rather than silently dropped (400).</summary>
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    public sealed record BankAccountBody(Guid? EnvelopeId, bool? ClearEnvelope, Guid? CardOfAccountId, bool? ClearCard, bool? AutoApplyBalance, bool? Ignored);

    /// <summary>Without <c>connectionId</c>, every connection of the person.</summary>
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    public sealed record SyncBody(Guid? ConnectionId);
}
