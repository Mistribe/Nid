using Nid.Core.Application.Fx;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Host.Api;

public static class FxEndpoints
{
    /// <summary>A decimal string, like every rate and amount on the wire (1C2a design §6.3).</summary>
    public sealed record ManualRateBody(string Rate);

    public static IEndpointRouteBuilder MapFxEndpoints(this IEndpointRouteBuilder routes)
    {
        var api = routes.MapGroup("/api/v1/core/fx-rates").RequireAuthorization()
            .AddEndpointFilter<MalformedInputFilter>().AddEndpointFilter<ActorFilter>();

        // LocalDate has no TryParse(string, out LocalDate) the minimal API's route/query binder can find (only
        // a custom JsonConverter for the body), so dates come in as strings and are parsed the way
        // SubscriptionEndpoints reads its own from/to/horizon query parameters.
        api.MapGet("", async (string quote, string from, string to, IInvoker invoker, CancellationToken ct)
                => (await invoker.InvokeAsync(
                        new GetFxRatesQuery(quote, SubscriptionEndpoints.RequireDate(from), SubscriptionEndpoints.RequireDate(to)), ct))
                    .ToHttp(r => r))
            .Returns<IReadOnlyList<FxRateView>>();

        api.MapPut("/{date}/{quote}", async (string date, string quote, ManualRateBody body, IInvoker invoker, CancellationToken ct)
                => (await invoker.InvokeAsync(
                        new SetManualFxRateCommand(SubscriptionEndpoints.RequireDate(date), quote, body.Rate), ct))
                    .ToHttp(r => r))
            .Returns<FxRateView>();

        api.MapPost("/sync", async (IInvoker invoker, CancellationToken ct)
                => (await invoker.InvokeAsync(new RequestFxSyncCommand(), ct)).ToHttp(r => r, StatusCodes.Status202Accepted))
            .Returns<FxSyncRequested>(StatusCodes.Status202Accepted);

        api.MapGet("/runs", async (IInvoker invoker, CancellationToken ct)
                => (await invoker.InvokeAsync(new GetFxSyncRunsQuery(), ct)).ToHttp(r => r))
            .Returns<IReadOnlyList<FxSyncRunView>>();

        return routes;
    }
}
