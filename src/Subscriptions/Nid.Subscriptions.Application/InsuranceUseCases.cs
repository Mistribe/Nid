using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.Subscriptions.Contracts;
using Nid.Subscriptions.Domain;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Subscriptions.Application;

/// <summary>Design 2026-10-30-contract-terms-insurance §7 and §10: what the subscription's policy covers, replaced whole.</summary>
/// <remarks><paramref name="KeepProperty"/> with no <c>PropertyId</c> leaves the stored link as it is: a writer who cannot read the property cannot send its id.</remarks>
public sealed record SetInsuranceCommand(ItemId ItemId, InsuranceDetails Details, bool KeepProperty = false) : ICommand<ItemId>, IItemScoped
{
    public AccessKind Access => AccessKind.Write;
}

public sealed record DeleteInsuranceCommand(ItemId ItemId) : ICommand<ItemId>, IItemScoped
{
    public AccessKind Access => AccessKind.Write;
}

[RequestHandler<SetInsuranceCommand, ItemId>]
public sealed class SetInsuranceHandler(ISubscriptionStore store, IItemRegistry items, IAccessPolicy policy, IActorAccessor actor)
    : IRequestHandler<SetInsuranceCommand, ItemId>
{
    public const string InvalidPropertyCode = "subscriptions.invalid_property";

    public async ValueTask<Result<ItemId>> HandleAsync(SetInsuranceCommand command, CancellationToken ct = default)
    {
        if (InsuranceRules.Validate(command.Details) is { } issue)
            return Result.Failure<ItemId>(FieldErrors.At(new Failure(issue.Code, issue.Message, null), InsurancePaths.Api(issue)));

        var details = command.Details;
        if (command.KeepProperty && details.PropertyId is null)
        {
            // Spec §10: the link the author cannot see stays, whoever it was made by; nothing is checked, nothing is revealed.
            var kept = (await store.FindInsuranceAsync(command.ItemId, ct))?.PropertyId;
            var keeping = await store.SetInsuranceAsync(command.ItemId, details with { PropertyId = kept }, null, ct);
            return keeping.TryGetFailure(out var keepFailure) ? Result.Failure<ItemId>(keepFailure!) : Result.Success(command.ItemId);
        }

        // Spec §8: keeping the link it already has is not a new link, even one its author can no longer read.
        if (command.Details.PropertyId is { } property
            && (await store.FindInsuranceAsync(command.ItemId, ct))?.PropertyId != property
            && !await IsLinkableAsync(property, ct))
            return Result.Failure<ItemId>(FieldErrors.At(
                new Failure(InvalidPropertyCode, "This is not a property you can read.", null), "propertyId"));

        var result = await store.SetInsuranceAsync(command.ItemId, command.Details, null, ct);
        return result.TryGetFailure(out var failure) ? Result.Failure<ItemId>(failure!) : Result.Success(command.ItemId);
    }

    /// <summary>A refusal never tells an unreadable property from an unknown id or an item that is no property.</summary>
    private async Task<bool> IsLinkableAsync(ItemId property, CancellationToken ct)
    {
        // No actor, or one bound to no person (a system actor), links nothing: HTTP and approved proposals always carry a person,
        // and the import (Task 9) writes the store directly.
        if (actor.Current?.PersonId is not { } person || (await policy.CheckAsync(person, property, AccessKind.Read, ct)).TryGetFailure(out _))
            return false;
        return await items.FindAsync(property, ct) is { Module: "realestate", Kind: "property" };
    }
}

[RequestHandler<DeleteInsuranceCommand, ItemId>]
public sealed class DeleteInsuranceHandler(ISubscriptionStore store) : IRequestHandler<DeleteInsuranceCommand, ItemId>
{
    public async ValueTask<Result<ItemId>> HandleAsync(DeleteInsuranceCommand command, CancellationToken ct = default)
    {
        var result = await store.DeleteInsuranceAsync(command.ItemId, ct);
        return result.TryGetFailure(out var failure) ? Result.Failure<ItemId>(failure!) : Result.Success(command.ItemId);
    }
}

/// <summary>The API's camelCase path of a refused member, in the indexed style of the other bodies (<c>guarantees[2].label</c>).</summary>
public static class InsurancePaths
{
    public static string Api(InsuranceIssue issue) => issue.Field switch
    {
        InsuranceField.Guarantees => "guarantees",
        InsuranceField.GuaranteeLabel => $"guarantees[{issue.Index}].label",
        InsuranceField.GuaranteeCeiling => $"guarantees[{issue.Index}].ceiling",
        InsuranceField.GuaranteeDeductible => $"guarantees[{issue.Index}].deductible",
        InsuranceField.InsuredLabel => "insuredLabel",
        InsuranceField.PolicyNumber => "policyNumber",
        _ => "insurerContact",
    };
}
