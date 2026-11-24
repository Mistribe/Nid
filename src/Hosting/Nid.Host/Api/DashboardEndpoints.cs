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
            => await FieldReader.Run(invoker, r => new GetDashboardQuery(r.Scope(scope)), view => view, ct))
            .Returns<DashboardView>();

        api.MapGet("/dashboard/net-worth", async (string? scope, string? period, IInvoker invoker, CancellationToken ct)
            => await FieldReader.Run(invoker, r => new GetNetWorthHistoryQuery(r.Scope(scope), ParsePeriod(period, r)), view => view, ct))
            .Returns<NetWorthHistoryView>();

        // Issue #415: « Depuis le mois dernier ».
        api.MapGet("/dashboard/changes", async (string? scope, IInvoker invoker, CancellationToken ct)
            => await FieldReader.Run(invoker, r => new GetMonthChangesQuery(r.Scope(scope)), view => view, ct))
            .Returns<MonthChangesView>();

        // Issue #104: any adult sets the household's target allocation, as the tax profile.
        api.MapPut("/dashboard/allocation-target", async (AllocationTargetBody body, IInvoker invoker, CancellationToken ct)
            => (await invoker.InvokeAsync(new SaveAllocationTargetCommand(body.Target ?? new Dictionary<string, decimal>()), ct)).ToHttp(target => new AllocationTargetBody(target)))
            .Returns<AllocationTargetBody>();

        return routes;
    }

    public sealed record AllocationTargetBody(IReadOnlyDictionary<string, decimal>? Target);

    /// <summary>2D2b spec §4: absent is the default, an unknown period refused at <c>period</c> (never a silent default).</summary>
    internal static NetWorthPeriod ParsePeriod(string? period, FieldReader reader)
    {
        switch (period)
        {
            case null or "12m": return NetWorthPeriod.TwelveMonths;
            case "3y": return NetWorthPeriod.ThreeYears;
            case "all": return NetWorthPeriod.All;
            default:
                reader.Add("period", "request.period", $"'{period}' is not a period: send '12m', '3y' or 'all'.");
                return NetWorthPeriod.TwelveMonths;
        }
    }
}
