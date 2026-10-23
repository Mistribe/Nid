using Nid.Core.Application.Tax;
using Nid.Core.Contracts;
using Nid.Core.Domain.Tax;
using UnambitiousFx.Synapse.Abstractions;
using static Nid.Host.Api.SubscriptionEndpoints;

namespace Nid.Host.Api;

public static class TaxEndpoints
{
    /// <summary>Tax estimate spec §8.</summary>
    public static IEndpointRouteBuilder MapTaxEndpoints(this IEndpointRouteBuilder routes)
    {
        var api = routes.MapGroup("/api/v1/tax").RequireAuthorization()
            .AddEndpointFilter<MalformedInputFilter>().AddEndpointFilter<ActorFilter>();

        api.MapGet("", async (int? year, string? scope, IInvoker invoker, CancellationToken ct)
            => (await invoker.InvokeAsync(new GetTaxEstimateQuery(year, ParseScope(scope)), ct)).ToHttp(view => view))
            .Returns<TaxEstimateView>();

        // Rental 2044 spec §5.
        api.MapGet("/2044", async (int? year, string? scope, IInvoker invoker, CancellationToken ct)
            => (await invoker.InvokeAsync(new GetRental2044Query(year, ParseScope(scope)), ct)).ToHttp(view => view))
            .Returns<Rental2044View>();

        // Sale tax spec §6.
        api.MapGet("/sale", async (string? scope, Guid? item, IInvoker invoker, CancellationToken ct)
            => (await invoker.InvokeAsync(new GetSaleTaxQuery(ParseScope(scope), item is { } g ? new ItemId(g) : null), ct)).ToHttp(view => view))
            .Returns<SaleTaxView>();

        api.MapPut("/profile/{year:int}", async (int year, string? scope, TaxProfileBody body, IInvoker invoker, CancellationToken ct) =>
            {
                var reader = new FieldReader();
                var profile = body.Read(reader);
                return profile is null
                    ? reader.Problem()
                    : (await invoker.InvokeAsync(new SaveTaxProfileCommand(year, profile, ParseScope(scope)), ct)).ToHttp(view => view);
            })
            .Returns<TaxEstimateView>();

        api.MapPost("/simulate", async (TaxInputsView body, IInvoker invoker, CancellationToken ct) =>
            {
                var reader = new FieldReader();
                var inputs = Read(body, reader);
                return inputs is null
                    ? reader.Problem()
                    : (await invoker.InvokeAsync(new SimulateTaxQuery(inputs), ct)).ToHttp(view => view);
            })
            .Returns<TaxResultView>();

        return routes;
    }

    public sealed record TaxProfileBody(
        IReadOnlyDictionary<Guid, string>? Statuses, int? Dependents, int ChildrenUnder6, string? RentalRegime, IReadOnlyList<PinelCommitment>? Pinel,
        string? ChildcareOutsideHome, string? HomeEmployment, string? Donations, string? DonationsAid,
        IReadOnlyDictionary<Guid, string>? ContributionRates = null, IReadOnlyDictionary<Guid, string>? ActualExpenses = null)
    {
        internal TaxProfile? Read(FieldReader reader)
        {
            var rates = (ContributionRates ?? new Dictionary<Guid, string>()).ToDictionary(x => x.Key, x => Rate(reader, x.Value, $"contributionRates.{x.Key}"));
            var expenses = (ActualExpenses ?? new Dictionary<Guid, string>()).ToDictionary(x => x.Key, x => Amount(reader, x.Value, $"actualExpenses.{x.Key}"));
            var childcare = Amount(reader, ChildcareOutsideHome, "childcareOutsideHome");
            var home = Amount(reader, HomeEmployment, "homeEmployment");
            var donations = Amount(reader, Donations, "donations");
            var aid = Amount(reader, DonationsAid, "donationsAid");
            return reader.Ok
                ? new TaxProfile(Statuses ?? new Dictionary<Guid, string>(), Dependents, ChildrenUnder6, RentalRegime, Pinel ?? [], childcare, home, donations, aid,
                    rates, expenses)
                : null;
        }
    }

    private static TaxInputs? Read(TaxInputsView body, FieldReader reader)
    {
        var declarants = new List<TaxDeclarant>();
        for (var i = 0; i < (body.Declarants?.Count ?? 0); i++)
        {
            var d = body.Declarants![i];
            if (!TaxEstimates.TryStatus(d.Status, out var status))
                reader.Add($"declarants[{i}].status", "request.status", "A status is non_executive, executive or public.");
            declarants.Add(new TaxDeclarant(d.Name ?? "", Amount(reader, d.SalaryGross, $"declarants[{i}].salaryGross"), status,
                Amount(reader, d.OtherTaxable, $"declarants[{i}].otherTaxable"), Amount(reader, d.PerContributions, $"declarants[{i}].perContributions"),
                d.ContributionRate is null ? null : Rate(reader, d.ContributionRate, $"declarants[{i}].contributionRate"),
                d.ActualExpenses is null ? null : Amount(reader, d.ActualExpenses, $"declarants[{i}].actualExpenses")));
        }
        RentalRegime? regime = null;
        if (body.RentalRegime is not null)
        {
            if (TaxEstimates.TryRegime(body.RentalRegime, out var r)) regime = r;
            else reader.Add("rentalRegime", "request.regime", "A rental regime is micro or real.");
        }
        var inputs = new TaxInputs(
            body.Year, declarants, body.Dependents, body.ChildrenUnder6,
            Amount(reader, body.RentalGross, "rentalGross"), Amount(reader, body.RentalInterest, "rentalInterest"),
            Amount(reader, body.RentalCharges, "rentalCharges"), regime,
            Amount(reader, body.CapitalIncome, "capitalIncome"), Amount(reader, body.NonTaxableIncome, "nonTaxableIncome"),
            Amount(reader, body.PinelReduction, "pinelReduction"), Amount(reader, body.ChildcareOutsideHome, "childcareOutsideHome"),
            Amount(reader, body.HomeEmployment, "homeEmployment"), Amount(reader, body.Donations, "donations"), Amount(reader, body.DonationsAid, "donationsAid"));
        return reader.Ok ? inputs : null;
    }

    /// <summary>A percent string (<c>"23.5"</c>) between 0 and 60, as a fraction.</summary>
    private static decimal Rate(FieldReader reader, string value, string path)
    {
        var percent = reader.Decimal(value, path);
        if (percent is { } p && TaxEstimates.RateOk(p / 100)) return p / 100;
        if (percent is not null) reader.Add(path, "request.rate", "A contribution rate is a percent between 0 and 60.");
        return 0m;
    }

    /// <summary>Absent is 0; an amount is a non-negative decimal string below ten million.</summary>
    private static decimal Amount(FieldReader reader, string? value, string path)
    {
        if (value is null) return 0m;
        var amount = reader.Decimal(value, path);
        if (amount is >= 0 and < 10_000_000m) return amount.Value;
        if (amount is not null) reader.Add(path, "request.amount", "An amount is not negative and below 10 000 000.");
        return 0m;
    }
}
