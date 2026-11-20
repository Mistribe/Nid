using Nid.Banking.Application.Reconciliation;
using Nid.Banking.Contracts;
using Nid.Core.Contracts;
using Nid.Banking.Domain;
using Nid.Platform.Contracts;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Banking.Application.Operations;

/// <summary>#84: a person's note on a line of an account they write; empty or null clears it, 500 characters at most.</summary>
public sealed record SetTransactionNoteCommand(Guid TransactionId, string? Note) : ICommand<BankTransactionView>;

/// <summary>#84: cuts a line of an account the actor writes into parts that add up to it (<see cref="SplitRules"/>), replacing earlier parts.</summary>
public sealed record SplitTransactionCommand(Guid TransactionId, IReadOnlyList<PartInput> Parts) : ICommand<BankTransactionView>, ITransactional;

/// <summary>#84: puts a split line back whole; its category passes run again, as for « Laisser Nid décider ».</summary>
public sealed record UnsplitTransactionCommand(Guid TransactionId) : ICommand<BankTransactionView>, ITransactional;

internal static class SplitUseCases
{
    public static Failure Split() => new ConflictFailure("A split line has no category of its own: un-split it, or set its parts.") { Code = BankingCodes.LineSplit };
}

[RequestHandler<SetTransactionNoteCommand, BankTransactionView>]
public sealed class SetTransactionNoteHandler(BankingAccess access, BankLines lines, ISplitStore splits)
    : IRequestHandler<SetTransactionNoteCommand, BankTransactionView>
{
    public async ValueTask<Result<BankTransactionView>> HandleAsync(SetTransactionNoteCommand command, CancellationToken ct = default)
    {
        var enabled = await access.EnabledAsync(ct);
        if (!enabled.IsSuccess)
            return BankingAccess.Failed<BankTransactionView>(enabled);
        var found = await lines.FindAsync(command.TransactionId, AccessKind.Write, ct);
        if (!found.TryGetValue(out var line))
            return BankingAccess.Failed<BankTransactionView, BankTransaction>(found);
        if ((command.Note?.Trim().Length ?? 0) > SplitRules.MaxNote)
            return Result.Failure<BankTransactionView>(FieldErrors.Shape(
                [new FieldError("note", FieldErrors.ShapeCode, $"A note is {SplitRules.MaxNote} characters at most.")]));

        await splits.SetNoteAsync(line!.Id, command.Note, ct);
        return Result.Success(await lines.ViewAsync(line.Id, ct));
    }
}

[RequestHandler<SplitTransactionCommand, BankTransactionView>]
public sealed class SplitTransactionHandler(BankingAccess access, BankLines lines, ICategoryStore categories, ISplitStore splits, IClock clock)
    : IRequestHandler<SplitTransactionCommand, BankTransactionView>
{
    public async ValueTask<Result<BankTransactionView>> HandleAsync(SplitTransactionCommand command, CancellationToken ct = default)
    {
        var enabled = await access.EnabledAsync(ct);
        if (!enabled.IsSuccess)
            return BankingAccess.Failed<BankTransactionView>(enabled);
        var found = await lines.FindAsync(command.TransactionId, AccessKind.Write, ct);
        if (!found.TryGetValue(out var line))
            return BankingAccess.Failed<BankTransactionView, BankTransaction>(found);
        var valid = SplitRules.Validate(line!.Amount, command.Parts);
        if (valid.TryGetFailure(out var invalid))
            // A sum refusal carries both sums as they are (a located field error would drop them).
            return Result.Failure<BankTransactionView>(invalid.Code == BankingCodes.SplitSum ? invalid : FieldErrors.At(invalid, "parts"));
        foreach (var id in command.Parts.Select(p => p.CategoryId).OfType<Guid>().Distinct())
        {
            if (await categories.FindAsync(id, ct) is not { } category)
                return Result.Failure<BankTransactionView>(new NotFoundFailure("bank category", id.ToString()));
            // A merged category is archived too: its lines went to its target.
            if (category.ArchivedAt is not null)
                return Result.Failure<BankTransactionView>(FieldErrors.At(
                    new Failure(BankingCodes.CategoryArchived, "A part takes only a category in use.", null), "parts"));
        }

        await splits.SplitAsync(line.Id, command.Parts, $"person:{access.Person.Value:D}", clock.GetCurrentInstant(), ct);
        return Result.Success(await lines.ViewAsync(line.Id, ct));
    }
}

[RequestHandler<UnsplitTransactionCommand, BankTransactionView>]
public sealed class UnsplitTransactionHandler(
    BankingAccess access, BankLines lines, ISplitStore splits, LineRecategoriser recategoriser)
    : IRequestHandler<UnsplitTransactionCommand, BankTransactionView>
{
    public async ValueTask<Result<BankTransactionView>> HandleAsync(UnsplitTransactionCommand command, CancellationToken ct = default)
    {
        var enabled = await access.EnabledAsync(ct);
        if (!enabled.IsSuccess)
            return BankingAccess.Failed<BankTransactionView>(enabled);
        var found = await lines.FindAsync(command.TransactionId, AccessKind.Write, ct);
        if (!found.TryGetValue(out var line))
            return BankingAccess.Failed<BankTransactionView, BankTransaction>(found);

        if (await splits.UnsplitAsync(line!.Id, ct))
            await recategoriser.ApplyAsync(line, ct);
        return Result.Success(await lines.ViewAsync(line.Id, ct));
    }
}
