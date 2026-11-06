using System.Globalization;
using Nid.Core.Application.Projection;
using Nid.Core.Contracts;
using UnambitiousFx.Synapse.Abstractions;
using static Nid.Host.Api.SubscriptionEndpoints;

namespace Nid.Host.Api;

public static class ProjectionEndpoints
{
    /// <summary>Net worth projection spec §2.1 and §7: the household's assumptions, any adult's to read and replace.</summary>
    public static IEndpointRouteBuilder MapProjectionEndpoints(this IEndpointRouteBuilder routes)
    {
        var api = routes.MapGroup("/api/v1/core/projection-assumptions").RequireAuthorization()
            .AddEndpointFilter<MalformedInputFilter>().AddEndpointFilter<ActorFilter>();

        api.MapGet("", async (IInvoker invoker, CancellationToken ct)
            => (await invoker.InvokeAsync(new GetProjectionAssumptionsQuery(), ct)).ToHttp(view => view))
            .Returns<ProjectionAssumptionsView>();

        api.MapPut("", async (AssumptionsBody body, IInvoker invoker, CancellationToken ct) =>
            {
                var reader = new FieldReader();
                var assumptions = body.ToAssumptions(reader);
                return assumptions is null
                    ? reader.Problem()
                    : (await invoker.InvokeAsync(new SaveProjectionAssumptionsCommand(assumptions), ct)).ToHttp(view => view);
            })
            .Returns<ProjectionAssumptionsView>();

        // Spec §7: the base projection, in the reader's scope.
        routes.MapGroup("/api/v1/projection").RequireAuthorization()
            .AddEndpointFilter<MalformedInputFilter>().AddEndpointFilter<ActorFilter>()
            .MapGet("", async (string? scope, string? horizon, IInvoker invoker, CancellationToken ct)
                => (await invoker.InvokeAsync(new GetProjectionQuery(ParseScope(scope), ParseHorizon(horizon)), ct)).ToHttp(view => view))
            .Returns<ProjectionView>();

        return routes;
    }

    /// <summary>
    /// Spec §2.3: absent is ten years; <c>{n}y</c> from 1 to 40, or <c>retire:{personId}</c>. Anything else is a bad request, never a
    /// silent default (as <see cref="DashboardEndpoints.ParsePeriod"/>).
    /// </summary>
    public static ProjectionHorizon ParseHorizon(string? horizon)
    {
        if (horizon is null) return ProjectionHorizon.Default;
        if (horizon.EndsWith('y') && int.TryParse(horizon.AsSpan(0, horizon.Length - 1), NumberStyles.None, CultureInfo.InvariantCulture, out var years)
            && years is >= 1 and <= ProjectionHorizon.MaxYears)
            return new ProjectionHorizon(years, null);
        if (horizon.StartsWith("retire:", StringComparison.Ordinal) && Guid.TryParse(horizon.AsSpan("retire:".Length), out var person))
            return new ProjectionHorizon(null, new PersonId(person));
        throw new BadHttpRequestException($"'{horizon}' is not a horizon: send '{{n}}y' (1 to {ProjectionHorizon.MaxYears}) or 'retire:{{personId}}'.");
    }

    /// <summary>The same shape as the view: rates in percent, the savings in the reference currency; a missing member is read as such.</summary>
    public sealed record AssumptionsBody(
        IReadOnlyDictionary<string, string>? ClassRates, string? Inflation, string? MonthlySavings, string? SavingsClass, bool? FreedPaymentsToSavings,
        IReadOnlyDictionary<string, string>? InflationByYear = null, IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>? ClassRatesByYear = null,
        string? IncomeGrowth = null)
    {
        internal ProjectionAssumptions? ToAssumptions(FieldReader reader)
        {
            var rates = new Dictionary<string, decimal>();
            foreach (var (@class, text) in ClassRates ?? new Dictionary<string, string>())
                if (reader.Decimal(text, $"classRates.{@class}") is { } percent)
                    rates[@class] = percent / 100m;
            if (ClassRates is null) reader.Add("classRates", "request.required", "The rates are required.");

            var inflation = reader.Decimal(Inflation, "inflation") / 100m;
            if (Inflation is null) reader.Add("inflation", "request.required", "The inflation is required.");
            var savings = reader.Decimal(MonthlySavings, "monthlySavings");
            var savingsClass = reader.Text(SavingsClass, "savingsClass");
            if (FreedPaymentsToSavings is null) reader.Add("freedPaymentsToSavings", "request.required", "This choice is required.");

            // Inflation spec C15: the yearly tables and the income growth are optional; a body without them is read as none.
            decimal? Percent(string? text, string path) => reader.Decimal(text, path);
            var byYear = ProjectionAssumptionsView.Years(InflationByYear, "inflationByYear", Percent, (path, message) => reader.Add(path, "request.invalid", message));
            var classYears = ProjectionAssumptionsView.ClassYears(ClassRatesByYear, "classRatesByYear", Percent, (path, message) => reader.Add(path, "request.invalid", message));
            var growth = reader.Decimal(IncomeGrowth, "incomeGrowth") / 100m;

            return reader.Ok
                ? new ProjectionAssumptions(rates, inflation!.Value, savings, savingsClass!, FreedPaymentsToSavings!.Value, byYear, classYears, growth ?? 0m)
                : null;
        }
    }
}
