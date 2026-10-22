using Nid.Core.Application.Dashboard;
using UnambitiousFx.Synapse.Abstractions;
using static Nid.Host.Api.SubscriptionEndpoints;

namespace Nid.Host.Api;

public static class DashboardEndpoints
{
    /// <summary>2D1 spec §4: one aggregated read for the home page.</summary>
    public static IEndpointRouteBuilder MapDashboardEndpoints(this IEndpointRouteBuilder routes)
    {
        var api = routes.MapGroup("/api/v1").RequireAuthorization()
            .AddEndpointFilter<MalformedInputFilter>().AddEndpointFilter<ActorFilter>();

        api.MapGet("/dashboard", async (string? scope, IInvoker invoker, CancellationToken ct)
            => (await invoker.InvokeAsync(new GetDashboardQuery(ParseScope(scope)), ct)).ToHttp(view => view))
            .Returns<DashboardView>();

        api.MapGet("/dashboard/net-worth", async (string? scope, string? period, IInvoker invoker, CancellationToken ct)
            => (await invoker.InvokeAsync(new GetNetWorthHistoryQuery(ParseScope(scope), ParsePeriod(period)), ct)).ToHttp(view => view))
            .Returns<NetWorthHistoryView>();

        // Issue #104: any adult sets the household's target allocation, as the tax profile.
        api.MapPut("/dashboard/allocation-target", async (AllocationTargetBody body, IInvoker invoker, CancellationToken ct)
            => (await invoker.InvokeAsync(new SaveAllocationTargetCommand(body.Target ?? new Dictionary<string, decimal>()), ct)).ToHttp(target => new AllocationTargetBody(target)))
            .Returns<AllocationTargetBody>();

        return routes;
    }

    public sealed record AllocationTargetBody(IReadOnlyDictionary<string, decimal>? Target);

    /// <summary>2D2b spec §4: absent is the default, an unknown period a bad request (never a silent default).</summary>
    internal static NetWorthPeriod ParsePeriod(string? period) => period switch
    {
        null or "12m" => NetWorthPeriod.TwelveMonths,
        "3y" => NetWorthPeriod.ThreeYears,
        "all" => NetWorthPeriod.All,
        _ => throw new BadHttpRequestException($"'{period}' is not a period: send '12m', '3y' or 'all'."),
    };
}
