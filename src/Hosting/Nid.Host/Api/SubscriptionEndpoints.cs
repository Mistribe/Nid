using Nid.Core.Contracts;
using Nid.Subscriptions.Application;
using Nid.Subscriptions.Contracts;
using NodaTime;
using NodaTime.Text;
using UnambitiousFx.Synapse.Abstractions;
using Duration = Nid.Core.Contracts.Duration;

namespace Nid.Host.Api;

public static class SubscriptionEndpoints
{
    public static IEndpointRouteBuilder MapSubscriptionEndpoints(this IEndpointRouteBuilder routes)
    {
        var api = routes.MapGroup("/api/v1/subscriptions").RequireAuthorization()
            .AddEndpointFilter<MalformedInputFilter>().AddEndpointFilter<ActorFilter>();

        api.MapGet("", async (string? scope, string? from, string? to, string? horizon, string? q, string? status, string? label, string? group,
                string? provider, IInvoker invoker, CancellationToken ct)
            => await FieldReader.Run(invoker, r =>
                    new ListSubscriptionsQuery(
                        r.Scope(scope), r.OptionalDate(from, "from"), r.OptionalDate(to, "to"), r.OptionalDate(horizon, "horizon"),
                        new ListFilter(q, ParseStatus(status), ParseLabels(label), provider), ParseGroup(group)), list => list, ct))
            .Returns<SubscriptionList>();

        api.MapGet("/overview", async (string? scope, IInvoker invoker, CancellationToken ct)
            => await FieldReader.Run(invoker, r => new GetSubscriptionsOverviewQuery(r.Scope(scope)), overview => overview, ct))
            .Returns<SubscriptionsOverview>();

        api.MapGet("/charges", async (string? scope, string? month, IInvoker invoker, CancellationToken ct)
            => await FieldReader.Run(invoker, r => new GetSubscriptionChargesQuery(r.Scope(scope), r.Month(month, "month")), c => c, ct))
            .Returns<SubscriptionCharges>();

        api.MapGet("/{id:guid}/deletion", async (Guid id, IInvoker invoker, CancellationToken ct)
            => (await invoker.InvokeAsync(new SubscriptionDeletionQuery(new ItemId(id)), ct)).ToHttp(preview => preview))
            .Returns<ItemDeletionPreview>();

        api.MapDelete("/{id:guid}", async (Guid id, uint? version, IInvoker invoker, CancellationToken ct) =>
            {
                // A missing version answers 422 at `version`: it would otherwise bind to 0 and pass for a conflict.
                if (version is null)
                {
                    var reader = new FieldReader();
                    reader.Add("version", "request.required", "The version read with it is required.");
                    return reader.Problem();
                }

                var result = await invoker.InvokeAsync(new DeleteSubscriptionCommand(new ItemId(id), version!.Value), ct);
                return result.TryGetValue(out _) ? Results.NoContent() : result.ToHttp(x => x);
            })
            .ReturnsNoContent();

        api.MapGet("/{id:guid}", async (Guid id, string? from, string? to, string? horizon, IInvoker invoker, CancellationToken ct)
            => await FieldReader.Run(invoker, r =>
                    new GetSubscriptionQuery(
                        new ItemId(id), r.OptionalDate(from, "from"), r.OptionalDate(to, "to"), r.OptionalDate(horizon, "horizon")), detail => detail, ct))
            .Returns<SubscriptionDetail>();

        // Create / open phase / add purchase were implemented early because Task 4's own acceptance test
        // creates its fixtures through the real HTTP API — see the comment atop WriteUseCases.cs.
        api.MapPost("", async (CreateSubscriptionBody body, IInvoker invoker, CancellationToken ct) =>
            {
                var reader = new FieldReader();
                var command = body.ToCommand(reader);
                return command is null
                    ? reader.Problem()
                    : (await invoker.InvokeAsync(command, ct)).ToHttp(id => new IdResponse(id.Value), StatusCodes.Status201Created);
            })
            .Returns<IdResponse>(StatusCodes.Status201Created);

        api.MapPatch("/{id:guid}", async (Guid id, EditSubscriptionBody body, IInvoker invoker, CancellationToken ct) =>
            {
                var reader = new FieldReader();
                var command = body.ToCommand(new ItemId(id), reader);
                return command is null
                    ? reader.Malformed()
                    : (await invoker.InvokeAsync(command, ct)).ToHttp(x => new IdResponse(x.Value));
            })
            .Returns<IdResponse>();

        api.MapPut("/{id:guid}/insurance", async (Guid id, InsuranceBody body, IInvoker invoker, CancellationToken ct) =>
            {
                var reader = new FieldReader();
                reader.Contradiction(body.KeepProperty && body.PropertyId is not null, "propertyId", "keepProperty");
                if (!reader.Ok)
                    return reader.Malformed();
                var details = body.Read(reader);
                return details is null
                    ? reader.Problem()
                    : (await invoker.InvokeAsync(new SetInsuranceCommand(new ItemId(id), details, body.KeepProperty), ct)).ToHttp(x => new IdResponse(x.Value));
            })
            .Returns<IdResponse>();

        api.MapDelete("/{id:guid}/insurance", async (Guid id, IInvoker invoker, CancellationToken ct)
            => (await invoker.InvokeAsync(new DeleteInsuranceCommand(new ItemId(id)), ct)).ToHttp(x => new IdResponse(x.Value)))
            .Returns<IdResponse>();

        api.MapPost("/{id:guid}/phases", async (Guid id, PhaseBody body, IInvoker invoker, CancellationToken ct) =>
            {
                var reader = new FieldReader();
                var phase = body.Read(reader, "");
                return phase is null
                    ? reader.Malformed()
                    : (await invoker.InvokeAsync(new OpenPhaseCommand(new ItemId(id), phase), ct))
                        .ToHttp(opened => new IdResponse(opened.Value), StatusCodes.Status201Created);
            })
            .Returns<IdResponse>(StatusCodes.Status201Created);

        api.MapPatch("/{id:guid}/phases/{phaseId:guid}", async (Guid id, Guid phaseId, ClosePhaseBody body, IInvoker invoker, CancellationToken ct)
            => await FieldReader.Run(invoker, r =>
                    new ClosePhaseCommand(new ItemId(id), new BillingPhaseId(phaseId), r.RequiredDate(body.EndsOn, "endsOn")), phase => new IdResponse(phase.Value), ct))
            .Returns<IdResponse>();

        api.MapPost("/{id:guid}/price-changes", async (Guid id, PriceChangeBody body, IInvoker invoker, CancellationToken ct) =>
            {
                var reader = new FieldReader();
                var command = body.ToCommand(new ItemId(id), reader);
                return command is null
                    ? reader.Problem()
                    : (await invoker.InvokeAsync(command, ct)).ToHttp(phase => new IdResponse(phase.Value), StatusCodes.Status201Created);
            })
            .Returns<IdResponse>(StatusCodes.Status201Created);

        api.MapPost("/{id:guid}/purchases", async (Guid id, PurchaseInput body, IInvoker invoker, CancellationToken ct) =>
            {
                var reader = new FieldReader();
                var draft = body.Read(reader, "");
                return draft is null
                    ? reader.Problem()
                    : (await invoker.InvokeAsync(new AddPurchaseCommand(new ItemId(id), draft), ct))
                        .ToHttp(purchase => new IdResponse(purchase.Value), StatusCodes.Status201Created);
            })
            .Returns<IdResponse>(StatusCodes.Status201Created);

        api.MapDelete("/{id:guid}/purchases/{purchaseId:guid}", async (Guid id, Guid purchaseId, IInvoker invoker, CancellationToken ct)
            => (await invoker.InvokeAsync(new DeletePurchaseCommand(new ItemId(id), new PurchaseId(purchaseId)), ct))
                .ToHttp(purchase => new IdResponse(purchase.Value)))
            .Returns<IdResponse>();

        return routes;
    }

    /// <summary>An absent or unknown status is "all", like the web's missing <c>status</c>.</summary>
    internal static ListStatus ParseStatus(string? status) => status?.ToLowerInvariant() switch
    {
        "active" => ListStatus.Active,
        "ended" => ListStatus.Ended,
        _ => ListStatus.All,
    };

    /// <summary>An absent or unknown group is "none": a flat list.</summary>
    internal static ListGroup ParseGroup(string? group) => group?.ToLowerInvariant() switch
    {
        "provider" => ListGroup.Provider,
        "label" => ListGroup.Label,
        "person" => ListGroup.Person,
        _ => ListGroup.None,
    };

    /// <summary><c>label=a,b</c>: a label key never holds a comma. Blanks and duplicates are dropped.</summary>
    internal static IReadOnlyList<string> ParseLabels(string? label)
        => (label ?? "").Split(',').Select(k => k.Trim()).Where(k => k != "").Distinct().ToList();

    public sealed record IdResponse(Guid Id);

    public sealed record MoneyBody(string Amount, string Currency);

    public sealed record DurationBody(int Count, string Unit);

    public sealed record PayerBody(string Type, Guid? PersonId);

    public sealed record OwnerBody(Guid PersonId, int ShareBp);

    public sealed record CreateSubscriptionBody(
        string DisplayName, string? Visibility, IReadOnlyList<OwnerBody> Owners,
        string? ProviderKey, bool AutoRenew, IReadOnlyList<Guid>? Users, string? Note,
        IReadOnlyList<PhaseInput>? Phases = null, IReadOnlyList<PurchaseInput>? Purchases = null, string? ShareLevel = null)
    {
        internal CreateSubscriptionCommand? ToCommand(FieldReader reader)
        {
            var visibility = reader.Visibility(Visibility, "visibility");
            var level = reader.ShareLevel(ShareLevel, "shareLevel");
            var phases = (Phases ?? []).Select((p, i) => p.Read(reader, $"phases[{i}]")).ToList();
            var purchases = (Purchases ?? []).Select((p, i) => p.Read(reader, $"purchases[{i}]")).ToList();
            return reader.Ok
                ? new CreateSubscriptionCommand(
                    DisplayName, visibility!.Value,
                    (Owners ?? []).Select(o => new OwnerShare(new Core.Contracts.PersonId(o.PersonId), o.ShareBp)).ToList(),
                    ProviderKey, AutoRenew,
                    (Users ?? []).Select(u => new Core.Contracts.PersonId(u)).ToList(),
                    Note, phases.Select(p => p!).ToList(), purchases.Select(p => p!).ToList(), level!.Value)
                : null;
        }
    }

    public sealed record EditSubscriptionBody(
        bool? AutoRenew, string? CancelledAt, bool ClearCancelledAt, string? Note, string? ProviderKey = null, bool ClearProviderKey = false,
        string? AnniversaryOn = null, bool ClearAnniversaryOn = false, DurationBody? Notice = null, bool ClearNotice = false,
        string? CommitmentEndsOn = null, bool ClearCommitmentEndsOn = false, string? CancellableFrom = null, bool ClearCancellableFrom = false,
        string? DisplayName = null)
    {
        /// <summary>A value and its <c>clearX</c> flag are one tri-state sent two ways: both at once is refused at the flag.</summary>
        internal EditSubscriptionCommand? ToCommand(ItemId id, FieldReader reader)
        {
            reader.Contradiction(ClearCancelledAt && CancelledAt is not null, "cancelledAt", "clearCancelledAt");
            reader.Contradiction(ClearProviderKey && ProviderKey is not null, "providerKey", "clearProviderKey");
            reader.Contradiction(ClearAnniversaryOn && AnniversaryOn is not null, "anniversaryOn", "clearAnniversaryOn");
            reader.Contradiction(ClearNotice && Notice is not null, "notice", "clearNotice");
            reader.Contradiction(ClearCommitmentEndsOn && CommitmentEndsOn is not null, "commitmentEndsOn", "clearCommitmentEndsOn");
            reader.Contradiction(ClearCancellableFrom && CancellableFrom is not null, "cancellableFrom", "clearCancellableFrom");

            var anniversaryOn = reader.OptionalDate(AnniversaryOn, "anniversaryOn");
            var notice = reader.Duration(Notice, "notice", required: false);
            var commitmentEndsOn = reader.OptionalDate(CommitmentEndsOn, "commitmentEndsOn");
            var cancellableFrom = reader.OptionalDate(CancellableFrom, "cancellableFrom");
            var cancelledAt = reader.OptionalDate(CancelledAt, "cancelledAt");
            if (!reader.Ok)
                return null;

            var touched = AnniversaryOn is not null || ClearAnniversaryOn || Notice is not null || ClearNotice
                          || CommitmentEndsOn is not null || ClearCommitmentEndsOn || CancellableFrom is not null || ClearCancellableFrom;
            var terms = touched
                ? new ContractTermsChange(
                    anniversaryOn, ClearAnniversaryOn, notice, ClearNotice,
                    commitmentEndsOn, ClearCommitmentEndsOn, cancellableFrom, ClearCancellableFrom)
                : null;
            return new(id, AutoRenew, cancelledAt, ClearCancelledAt, Note, ProviderKey, ClearProviderKey, terms, DisplayName);
        }
    }

    public sealed record GuaranteeBody(string? Label, string? Ceiling, string? Deductible);

    /// <summary>An insurance as the PUT reads it: it replaces the whole, every refusal located.</summary>
    public sealed record InsuranceBody(
        string? Kind, Guid? PropertyId, string? InsuredLabel, string? PolicyNumber, string? InsurerContact, IReadOnlyList<GuaranteeBody>? Guarantees,
        bool KeepProperty = false)
    {
        private static readonly Dictionary<string, InsuranceKind> Kinds = new(StringComparer.Ordinal)
        {
            ["home"] = InsuranceKind.Home, ["car"] = InsuranceKind.Car, ["health"] = InsuranceKind.Health,
            ["provident"] = InsuranceKind.Provident, ["other"] = InsuranceKind.Other,
        };

        internal InsuranceDetails? Read(FieldReader reader)
        {
            InsuranceKind? kind = Kind is not null && Kinds.TryGetValue(Kind, out var k) ? k : null;
            if (kind is null)
                reader.Add("kind", "request.kind", "An insurance kind is home, car, health, provident or other.");
            if (Guarantees?.Any(g => g is null) == true)
                reader.Add("guarantees", "request.invalid", "A guarantee is an object with a label.");
            return reader.Ok
                ? new InsuranceDetails(
                    kind!.Value, PropertyId is { } p ? new ItemId(p) : null, Blank(InsuredLabel), Blank(PolicyNumber), Blank(InsurerContact),
                    (Guarantees ?? []).Select(g => new InsuranceGuarantee(g.Label ?? "", Blank(g.Ceiling), Blank(g.Deductible))).ToList())
                : null;
        }

        private static string? Blank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    public sealed record ClosePhaseBody(string EndsOn);

    public sealed record PhaseBody(
        string StartsOn, string? EndsOn, MoneyBody Price, DurationBody Every, DurationBody? Covers,
        int? MaxCycles, PayerBody Payer, string? Note)
    {
        internal BillingPhase? Read(FieldReader reader, string path)
            => new PhaseInput(StartsOn, EndsOn, Price, Every, Covers, MaxCycles, Payer).Read(reader, path);
    }

    /// <summary>A phase as the creation and the price change read it: every refusal located (1E2a design §2.1).</summary>
    public sealed record PhaseInput(
        string? StartsOn, string? EndsOn, MoneyBody? Price, DurationBody? Every, DurationBody? Covers, int? MaxCycles, PayerBody? Payer)
    {
        internal BillingPhase? Read(FieldReader reader, string path)
        {
            string At(string field) => path == "" ? field : $"{path}.{field}";
            var startsOn = reader.Date(StartsOn, At("startsOn"));
            var endsOn = reader.Date(EndsOn, At("endsOn"), required: false);
            var price = reader.Money(Price, At("price"));
            var every = reader.Duration(Every, At("every"));
            var covers = reader.Duration(Covers, At("covers"), required: false);
            var payer = reader.Payer(Payer, At("payer"));
            return startsOn is { } s && price is { } p && every is { } e && payer is { } y && reader.Ok
                ? new BillingPhase(BillingPhaseId.New(), s, endsOn, p, e, covers, MaxCycles, y)
                : null;
        }
    }

    public sealed record PriceChangeBody(
        string? StartsOn, MoneyBody? Price, DurationBody? Every, DurationBody? Covers, int? MaxCycles, PayerBody? Payer)
    {
        internal ChangePriceCommand? ToCommand(ItemId id, FieldReader reader)
        {
            var startsOn = reader.Date(StartsOn, "startsOn");
            var price = reader.Money(Price, "price");
            var every = reader.Duration(Every, "every", required: false);
            var covers = reader.Duration(Covers, "covers", required: false);
            var payer = Payer is null ? null : reader.Payer(Payer, "payer");
            return reader.Ok ? new ChangePriceCommand(id, startsOn!.Value, price!.Value, every, covers, MaxCycles, payer) : null;
        }
    }

    public sealed record GrantInput(Guid? SubscriptionId, DurationBody? Duration, string? Source, string? StartsOn);

    /// <summary>A purchase as the creation and the purchase route read it: every refusal located (1E2b1 design §3).</summary>
    public sealed record PurchaseInput(
        string? Date, string? Label, MoneyBody? Price, PayerBody? Payer, string? Kind,
        IReadOnlyList<GrantInput>? Grants, string? AllocatedAmount, string? Note)
    {
        private static readonly Dictionary<string, PurchaseKind> Kinds = new(StringComparer.Ordinal)
        {
            ["prepaid_term"] = PurchaseKind.PrepaidTerm, ["bundle"] = PurchaseKind.Bundle, ["gift"] = PurchaseKind.Gift,
            ["adjustment"] = PurchaseKind.Adjustment, ["refund"] = PurchaseKind.Refund,
        };
        private static readonly Dictionary<string, CoverageSource> Sources = new(StringComparer.Ordinal)
        {
            ["paid"] = CoverageSource.Paid, ["bonus"] = CoverageSource.Bonus, ["included"] = CoverageSource.Included,
            ["trial"] = CoverageSource.Trial, ["gift"] = CoverageSource.Gift,
        };

        private static string At(string prefix, string field) => prefix == "" ? field : $"{prefix}.{field}";

        internal PurchaseDraft? Read(FieldReader reader, string prefix)
        {
            var date = reader.Date(Date, At(prefix, "date"));
            var label = reader.Text(Label, At(prefix, "label"));
            var price = reader.Money(Price, At(prefix, "price"));
            var payer = reader.Payer(Payer, At(prefix, "payer"));
            PurchaseKind? kind = Kind is not null && Kinds.TryGetValue(Kind, out var k) ? k : null;
            if (kind is null)
                reader.Add(At(prefix, "kind"), "request.kind", "A purchase kind is prepaid_term, bundle, gift, adjustment or refund.");
            var allocated = reader.Decimal(AllocatedAmount, At(prefix, "allocatedAmount"));

            var grants = new List<GrantDraft>();
            var list = Grants ?? [];
            for (var j = 0; j < list.Count; j++)
            {
                var path = At(prefix, $"grants[{j}]");
                var duration = reader.Duration(list[j].Duration, path + ".duration");
                CoverageSource? source = list[j].Source is { } s && Sources.TryGetValue(s, out var found) ? found : null;
                if (source is null)
                    reader.Add(path + ".source", "request.source", "A grant source is paid, bonus, included, trial or gift.");
                var startsOn = reader.Date(list[j].StartsOn, path + ".startsOn", required: false);
                if (duration is { } d && source is { } src)
                    grants.Add(new GrantDraft(list[j].SubscriptionId is { } id ? new ItemId(id) : null, d, src, startsOn));
            }

            return reader.Ok
                ? new PurchaseDraft(date!.Value, label!, price!.Value, payer!, kind!.Value, grants, allocated, Note)
                : null;
        }
    }
}
