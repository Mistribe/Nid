using Nid.Banking.Application;
using Nid.Banking.Contracts;
using Nid.Core.Contracts;
using Nid.Investments.Application;
using Nid.Platform.Contracts;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Host.Composition;

/// <summary>
/// #346: an envelope made from the « Régler » dialog of a bank account, in the account's currency with its IBAN's last four characters as
/// its hint, and the account linked to it. Banking does not reach the investments module's commands, so the composition root runs both in one
/// transaction: a refused link (another currency, a card, an account the actor may not move) leaves no envelope behind.
/// <paramref name="Joint"/>: every adult owns it in equal shares; <paramref name="DependentId"/> (#354, before <paramref name="Joint"/>): that
/// child owns it all, shared, so both adults manage it; otherwise the actor alone. <paramref name="Visibility"/> (#383): the form presets it from
/// the owners; a private one owned by a child or by every adult is refused like any other item's (<c>OwnershipRules</c>).
/// </summary>
public sealed record CreateBankAccountEnvelopeCommand(
    Guid AccountId, string DisplayName, string Kind, bool Joint, Guid? DependentId = null, Visibility Visibility = Visibility.Shared)
    : ICommand<ItemId>, ITransactional, IBankAccountScoped
{
    Guid IBankAccountScoped.BankAccountId => AccountId;

    public AccessKind Access => AccessKind.Write;
}

[RequestHandler<CreateBankAccountEnvelopeCommand, ItemId>]
public sealed class CreateBankAccountEnvelopeHandler(IContextAccessor context, BankingAccess access, IPersonRegistry persons, IInvoker invoker)
    : IRequestHandler<CreateBankAccountEnvelopeCommand, ItemId>
{
    public async ValueTask<Result<ItemId>> HandleAsync(CreateBankAccountEnvelopeCommand command, CancellationToken ct = default)
    {
        // Checked by the banking behavior: an account the actor does not write is refused before anything is created.
        var account = CheckedResources.Of<BankAccount>(context, command);

        IReadOnlyList<OwnerShare> owners;
        if (command.DependentId is { } dependent)
        {
            if (!(await persons.ListAsync(ct)).Any(p => p.Id.Value == dependent && p.Kind == PersonKind.Dependent))
                return Result.Failure<ItemId>(FieldErrors.At(new Failure("core.owner_unknown", "No such dependent in the household.", null), "dependentId"));
            owners = [new OwnerShare(new PersonId(dependent), 10_000)];
        }
        else
            owners = command.Joint ? Equal(await persons.AdultIdsAsync(ct)) : [new OwnerShare(access.Person, 10_000)];
        var created = await invoker.InvokeAsync(new CreateEnvelopeCommand(
            command.DisplayName, command.Kind, account!.Currency, command.Visibility, owners, null, account.IbanMasked, null, null, null, null, null, null), ct);
        if (!created.TryGetValue(out var envelope))
            return created;

        // Under its own savepoint: its refusal fails this command, whose transaction then takes the envelope back too.
        var linked = await invoker.InvokeAsync(new UpdateBankAccountCommand(account.Id, envelope.Value, false, null, false, null, null), ct);
        return linked.TryGetFailure(out var refused) ? Result.Failure<ItemId>(refused!) : Result.Success(envelope);
    }

    /// <summary>10 000 basis points shared out, the remainder to the first: 3 adults hold 3 334, 3 333 and 3 333.</summary>
    internal static IReadOnlyList<OwnerShare> Equal(IReadOnlyList<PersonId> adults)
        => adults.Select((p, i) => new OwnerShare(p, 10_000 / adults.Count + (i == 0 ? 10_000 % adults.Count : 0))).ToList();
}
