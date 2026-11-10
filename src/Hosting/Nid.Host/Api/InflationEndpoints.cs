using System.Globalization;
using Nid.Core.Application.Inflation;
using Nid.Core.Contracts;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Host.Api;

/// <summary><c>/api/v1/core/inflation</c>: the observed inflation (any adult) and the CPI collection's admin routes.</summary>
public static class InflationEndpoints
{
    /// <summary><c>Value</c>: a decimal string, like every amount on the wire.</summary>
    public sealed record CpiValueBody(string? Value);

    public static IEndpointRouteBuilder MapInflationEndpoints(this IEndpointRouteBuilder routes)
    {
        var api = routes.MapGroup("/api/v1/core/inflation").RequireAuthorization()
            .AddEndpointFilter<MalformedInputFilter>().AddEndpointFilter<ActorFilter>();

        api.MapGet("", async (IInvoker invoker, CancellationToken ct)
            => (await invoker.InvokeAsync(new GetInflationQuery(), ct)).ToHttp(r => r))
            .Returns<InflationView>();

        api.MapGet("/admin", async (int? page, IInvoker invoker, CancellationToken ct)
            => (await invoker.InvokeAsync(new GetCpiAdminQuery(page), ct)).ToHttp(r => r))
            .Returns<CpiValuePage>();

        api.MapGet("/runs", async (int? page, IInvoker invoker, CancellationToken ct)
            => (await invoker.InvokeAsync(new GetCpiRunsQuery(page), ct)).ToHttp(r => r))
            .Returns<CpiRunPage>();

        api.MapPost("/sync", async (IInvoker invoker, CancellationToken ct)
            => (await invoker.InvokeAsync(new RequestCpiSyncCommand(), ct)).ToHttp(r => r, StatusCodes.Status202Accepted))
            .Returns<CpiSyncRequested>(StatusCodes.Status202Accepted);

        api.MapPut("/{year:int}/{month:int}", async (int year, int month, CpiValueBody body, IInvoker invoker, CancellationToken ct) =>
            {
                var reader = new FieldReader();
                if (!decimal.TryParse(body.Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value))
                    reader.Add("value", "request.amount", "A CPI value is a decimal string such as \"103.40\".");
                return !reader.Ok
                    ? reader.Problem()
                    : (await invoker.InvokeAsync(new SetCpiValueCommand(year, month, value), ct)).ToHttp(r => r);
            })
            .Returns<CpiValueView>();

        return routes;
    }
}
