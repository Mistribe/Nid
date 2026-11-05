using Nid.Core.Application.Projection;
using Nid.Core.Application.Scenarios;
using Nid.Core.Contracts;
using UnambitiousFx.Synapse.Abstractions;
using static Nid.Host.Api.SubscriptionEndpoints;

namespace Nid.Host.Api;

public static class ScenarioEndpoints
{
    /// <summary>Property purchase scenario spec §6, and the sale scenarios (issue #416).</summary>
    public static IEndpointRouteBuilder MapScenarioEndpoints(this IEndpointRouteBuilder routes)
    {
        var api = routes.MapGroup("/api/v1/scenarios").RequireAuthorization()
            .AddEndpointFilter<MalformedInputFilter>().AddEndpointFilter<ActorFilter>();

        api.MapGet("", async (string? scope, IInvoker invoker, CancellationToken ct)
            => (await invoker.InvokeAsync(new ListScenariosQuery(ParseScope(scope)), ct)).ToHttp(list => list))
            .Returns<ScenarioList>();

        api.MapPost("", async (ScenarioBody body, IInvoker invoker, CancellationToken ct) =>
            {
                var reader = new FieldReader();
                var command = body.ToCreate(reader);
                return command is null
                    ? reader.Problem()
                    : (await invoker.InvokeAsync(command, ct)).ToHttp(id => new IdResponse(id.Value), StatusCodes.Status201Created);
            })
            .Returns<IdResponse>(StatusCodes.Status201Created);

        api.MapPost("/simulate", async (SimulateBody body, IInvoker invoker, CancellationToken ct) =>
            {
                var reader = new FieldReader();
                var document = ReadDocument(body.Document, reader);
                return document is null
                    ? reader.Problem()
                    : (await invoker.InvokeAsync(new SimulateScenarioQuery(ParseScope(body.Scope), document), ct)).ToHttp(view => view);
            })
            .Returns<ScenarioResultView>();

        api.MapGet("/defaults", async (string? scope, IInvoker invoker, CancellationToken ct)
            => (await invoker.InvokeAsync(new GetScenarioDefaultsQuery(ParseScope(scope)), ct)).ToHttp(view => view))
            .Returns<ScenarioDefaultsView>();

        // Net worth projection spec §14: the base and 1 to 3 scenarios, projections or purchases, on the request's grid.
        api.MapGet("/compare", async (string? scope, string? horizon, string? ids, IInvoker invoker, CancellationToken ct)
            => (await invoker.InvokeAsync(new CompareScenariosQuery(ParseScope(scope), ProjectionEndpoints.ParseHorizon(horizon), ParseIds(ids)), ct))
                .ToHttp(view => view))
            .Returns<ProjectionCompareView>();

        api.MapGet("/{id:guid}", async (Guid id, IInvoker invoker, CancellationToken ct)
            => (await invoker.InvokeAsync(new GetScenarioQuery(new ItemId(id)), ct)).ToHttp(view => view))
            .Returns<ScenarioView>();

        api.MapPut("/{id:guid}", async (Guid id, ScenarioBody body, IInvoker invoker, CancellationToken ct) =>
            {
                var reader = new FieldReader();
                var command = body.ToReplace(reader, new ItemId(id));
                return command is null
                    ? reader.Problem()
                    : (await invoker.InvokeAsync(command, ct)).ToHttp(view => view);
            })
            .Returns<ScenarioView>();

        api.MapDelete("/{id:guid}", async (Guid id, IInvoker invoker, CancellationToken ct) =>
            {
                var result = await invoker.InvokeAsync(new DeleteScenarioCommand(new ItemId(id)), ct);
                return result.TryGetValue(out _) ? Results.NoContent() : result.ToHttp(x => x);
            })
            .ReturnsNoContent();

        MapSaleScenarioEndpoints(routes);
        return routes;
    }

    /// <summary>Issue #416: the sale scenarios' own routes; the list, the deletion and « Comparer » are the purchases'.</summary>
    private static void MapSaleScenarioEndpoints(IEndpointRouteBuilder routes)
    {
        var api = routes.MapGroup("/api/v1/scenarios/sale").RequireAuthorization()
            .AddEndpointFilter<MalformedInputFilter>().AddEndpointFilter<ActorFilter>();

        api.MapPost("/simulate", async (SimulateSaleBody body, IInvoker invoker, CancellationToken ct) =>
            {
                var reader = new FieldReader();
                var document = ReadSale(body.Document, reader);
                return document is null
                    ? reader.Problem()
                    : (await invoker.InvokeAsync(new SimulateSaleScenarioQuery(ParseScope(body.Scope), document), ct)).ToHttp(view => view);
            })
            .Returns<SaleResultView>();

        api.MapPost("", async (SaleScenarioBody body, IInvoker invoker, CancellationToken ct) =>
            {
                var reader = new FieldReader();
                var (name, visibility, document) = body.Fields(reader);
                return !reader.Ok
                    ? reader.Problem()
                    : (await invoker.InvokeAsync(new CreateSaleScenarioCommand(name!, visibility, (body.Owners ?? [])
                        .Select(o => new OwnerShare(new PersonId(o.PersonId), o.ShareBp)).ToList(), ParseScope(body.Scope), document!), ct))
                        .ToHttp(id => new IdResponse(id.Value), StatusCodes.Status201Created);
            })
            .Returns<IdResponse>(StatusCodes.Status201Created);

        api.MapGet("/{id:guid}", async (Guid id, IInvoker invoker, CancellationToken ct)
            => (await invoker.InvokeAsync(new GetSaleScenarioQuery(new ItemId(id)), ct)).ToHttp(view => view))
            .Returns<SaleScenarioView>();

        api.MapPut("/{id:guid}", async (Guid id, SaleScenarioBody body, IInvoker invoker, CancellationToken ct) =>
            {
                var reader = new FieldReader();
                var (name, visibility, document) = body.Fields(reader);
                return !reader.Ok
                    ? reader.Problem()
                    : (await invoker.InvokeAsync(new ReplaceSaleScenarioCommand(new ItemId(id), name!, visibility, ParseScope(body.Scope), document!), ct))
                        .ToHttp(view => view);
            })
            .Returns<SaleScenarioView>();
    }

    public sealed record SimulateSaleBody(string? Scope, SaleScenarioDocView? Document);

    /// <summary>The document is the shape the reads answer with (<see cref="SaleScenarioDocView"/>); owners are read on creation only.</summary>
    public sealed record SaleScenarioBody(string? Name, string? Visibility, IReadOnlyList<OwnerBody>? Owners, string? Scope, SaleScenarioDocView? Document)
    {
        internal (string? Name, Visibility Visibility, SaleScenario? Document) Fields(FieldReader reader)
        {
            var name = reader.Text(Name, "name");
            var visibility = reader.Visibility(Visibility ?? "shared", "visibility");
            var document = ReadSale(Document, reader);
            return (name, visibility ?? default, document);
        }
    }

    private static SaleScenario? ReadSale(SaleScenarioDocView? body, FieldReader reader)
    {
        if (body is null) { reader.Add("document", "request.required", "A document is required."); return null; }
        if (body.Property is null) reader.Add("property", "request.required", "A property is required.");
        // A missing date binds to the default LocalDate; an unreadable one never gets here (the binder answers 400).
        if (body.SaleOn == default) reader.Add("saleOn", "request.required", "A sale date is required.");
        var document = new SaleScenario(
            body.Version, body.Property ?? default, body.SaleOn, Amount(reader, body.Price, "price"), Amount(reader, body.AgencyFees, "agencyFees"),
            Amount(reader, body.Penalties, "penalties"), body.HorizonYears);
        return reader.Ok ? document : null;
    }

    /// <summary>Spec §14.1: comma-separated ids, in order; a part that is no id is a bad request (the count is the handler's rule).</summary>
    private static List<ItemId> ParseIds(string? ids)
        => (ids ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => Guid.TryParse(part, out var id) ? new ItemId(id) : throw new BadHttpRequestException($"'{part}' is not a scenario id."))
            .ToList();

    /// <summary>The document is the same shape as the one the reads answer with; a missing member is read as such, never passed on as null.</summary>
    public sealed record ScenarioBody(string? Name, string? Visibility, IReadOnlyList<OwnerBody>? Owners, string? Scope, PropertyPurchaseView? Document)
    {
        internal CreateScenarioCommand? ToCreate(FieldReader reader)
            => Fields(reader) is var (name, visibility, document)
                ? new CreateScenarioCommand(name, visibility, (Owners ?? []).Select(o => new OwnerShare(new PersonId(o.PersonId), o.ShareBp)).ToList(),
                    ParseScope(Scope), document)
                : null;

        internal ReplaceScenarioCommand? ToReplace(FieldReader reader, ItemId id)
            => Fields(reader) is var (name, visibility, document) ? new ReplaceScenarioCommand(id, name, visibility, ParseScope(Scope), document) : null;

        private (string, Visibility, PropertyPurchase)? Fields(FieldReader reader)
        {
            var name = reader.Text(Name, "name");
            var visibility = reader.Visibility(Visibility ?? "shared", "visibility");
            var document = ReadDocument(Document, reader);
            return reader.Ok ? (name!, visibility!.Value, document!) : null;
        }
    }

    public sealed record SimulateBody(string? Scope, PropertyPurchaseView? Document);

    private static PropertyPurchase? ReadDocument(PropertyPurchaseView? body, FieldReader reader)
    {
        if (body is null) { reader.Add("document", "request.required", "A document is required."); return null; }
        // A missing date binds to the default LocalDate; an unreadable one never gets here (the binder answers 400).
        if (body.PurchaseOn == default) reader.Add("purchaseOn", "request.required", "A purchase date is required.");
        PurchaseUse use = default;
        if (body.Use == "primary") use = PurchaseUse.Primary;
        else if (body.Use == "rental") use = PurchaseUse.Rental;
        else reader.Add("use", "request.use", "A use is primary or rental.");

        var funding = (body.Funding ?? []).Select((f, i) => new FundingLine(f.Item, Amount(reader, f.Amount, $"funding[{i}].amount"))).ToList();
        var loans = new List<ScenarioLoan>();
        for (var i = 0; i < (body.Loans?.Count ?? 0); i++)
        {
            var l = body.Loans![i];
            var at = $"loans[{i}]";
            ScenarioDeferral? deferral = null;
            if (l.Deferral is { } d)
            {
                var kind = d.Kind switch { "partial" => DeferralKind.Partial, "total" => DeferralKind.Total, _ => (DeferralKind?)null };
                if (kind is null) reader.Add($"{at}.deferral.kind", "request.kind", "A deferral is partial or total.");
                deferral = new ScenarioDeferral(kind ?? default, d.Months);
            }
            ScenarioInsurance insurance = new(default, 0m);
            if (l.Insurance is null) reader.Add($"{at}.insurance", "request.required", "An insurance is required.");
            else if (l.Insurance.Kind == "rate_on_initial")
                insurance = new(ScenarioInsuranceKind.RateOnInitial, Rate(reader, l.Insurance.Value, $"{at}.insurance.value", 0m));
            else if (l.Insurance.Kind == "fixed_monthly")
                insurance = new(ScenarioInsuranceKind.FixedMonthly, Amount(reader, l.Insurance.Value, $"{at}.insurance.value"));
            else reader.Add($"{at}.insurance.kind", "request.kind", "An insurance is rate_on_initial or fixed_monthly.");
            loans.Add(new ScenarioLoan(
                l.Name ?? "", Amount(reader, l.Amount, $"{at}.amount"), Rate(reader, l.Rate, $"{at}.rate", 0m), l.Months, deferral, insurance,
                Amount(reader, l.FileFees, $"{at}.fileFees"), Amount(reader, l.GuaranteeFees, $"{at}.guaranteeFees")));
        }

        ScenarioAssumptions? assumptions = null;
        if (body.Assumptions is not { } a) reader.Add("assumptions", "request.required", "Assumptions are required.");
        else if (a.ClassRates is null) reader.Add("assumptions.classRates", "request.required", "Class rates are required.");
        else
            assumptions = new ScenarioAssumptions(
                a.HorizonYears, a.ClassRates.ToDictionary(r => r.Key, r => Rate(reader, r.Value, $"assumptions.classRates.{r.Key}", -0.20m)),
                a.PropertyGrowth is null ? null : Rate(reader, a.PropertyGrowth, "assumptions.propertyGrowth", -0.20m),
                Amount(reader, a.MonthlySavings, "assumptions.monthlySavings"), a.SavingsClass ?? "");

        var document = new PropertyPurchase(
            body.Version, use, body.PurchaseOn, Amount(reader, body.Price, "price"), Amount(reader, body.NotaryFees, "notaryFees"),
            Amount(reader, body.AgencyFees, "agencyFees"), Amount(reader, body.OtherCosts, "otherCosts"),
            Amount(reader, body.MonthlyCharges, "monthlyCharges"), Amount(reader, body.CurrentRent, "currentRent"),
            Amount(reader, body.ExpectedRent, "expectedRent"), body.VacancyMonths, body.RentalRegime, funding, loans, assumptions!);
        return reader.Ok ? document : null;
    }

    /// <summary>A percent string as a fraction, from <paramref name="min"/> to 20 %; absent is 0.</summary>
    private static decimal Rate(FieldReader reader, string? value, string path, decimal min)
    {
        var percent = reader.Decimal(value, path);
        if (percent is { } p && p / 100 >= min && p / 100 <= 0.20m) return p / 100;
        if (percent is not null) reader.Add(path, "request.rate", $"A rate is a percent from {min * 100:0} to 20.");
        return 0m;
    }

    /// <summary>Absent is 0; an amount is a non-negative decimal string below 10¹².</summary>
    private static decimal Amount(FieldReader reader, string? value, string path)
    {
        var amount = reader.Decimal(value, path);
        if (amount is >= 0 and < 1_000_000_000_000m) return amount.Value;
        if (amount is not null) reader.Add(path, "request.amount", "An amount is not negative and below 10^12.");
        return 0m;
    }
}
