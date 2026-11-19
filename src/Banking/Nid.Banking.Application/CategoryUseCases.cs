using Nid.Banking.Contracts;
using Nid.Banking.Domain;
using Nid.Platform.Contracts;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Banking.Application;

/// <summary>Every category, archived ones included (the web folds them), top-level first. Any signed-in person reads them.</summary>
public sealed record ListBankCategoriesQuery : IQuery<IReadOnlyList<BankCategoryView>>;

/// <summary><paramref name="Kind"/> (<c>expense</c>, <c>income</c> or <c>transfer</c>) is required for a top-level category; a child takes its parent's.</summary>
public sealed record CreateBankCategoryCommand(Guid? ParentId, string Name, string? Kind) : ICommand<BankCategoryView>;

/// <summary>
/// A field left out stays; <paramref name="MoveParent"/> says <paramref name="ParentId"/> (null: top-level) is meant;
/// <paramref name="ResetName"/> puts back the starter name of a seeded category, and excludes <paramref name="Name"/>.
/// </summary>
public sealed record UpdateBankCategoryCommand(Guid Id, string? Name, Guid? ParentId, bool MoveParent, bool? Archived, bool ResetName) : ICommand<BankCategoryView>;

/// <summary>Moves the lines and children of <paramref name="Id"/> into <paramref name="Into"/>, which is returned.</summary>
public sealed record MergeBankCategoryCommand(Guid Id, Guid Into) : ICommand<BankCategoryView>;

/// <summary><paramref name="DefaultName"/> is the starter name of a seeded category, null for the household's own.</summary>
public sealed record BankCategoryView(Guid Id, Guid? ParentId, string? Key, string Name, string Kind, bool Archived, Guid? MergedIntoId, string? DefaultName)
{
    public static BankCategoryView Of(BankCategory c) => new(
        c.Id, c.Parent, c.Key, c.Name, BankLines.Wire(c.Kind), c.ArchivedAt is not null, c.MergedInto, c.Key is null ? null : CategorySeed.Find(c.Key)?.Name);
}

[RequestHandler<ListBankCategoriesQuery, IReadOnlyList<BankCategoryView>>]
public sealed class ListBankCategoriesHandler(ICategoryStore categories) : IRequestHandler<ListBankCategoriesQuery, IReadOnlyList<BankCategoryView>>
{
    public async ValueTask<Result<IReadOnlyList<BankCategoryView>>> HandleAsync(ListBankCategoriesQuery query, CancellationToken ct = default)
        => Result.Success<IReadOnlyList<BankCategoryView>>([.. (await categories.AllAsync(ct)).Select(BankCategoryView.Of)]);
}

[RequestHandler<CreateBankCategoryCommand, BankCategoryView>]
public sealed class CreateBankCategoryHandler(BankingAccess access, ICategoryStore categories) : IRequestHandler<CreateBankCategoryCommand, BankCategoryView>
{
    public async ValueTask<Result<BankCategoryView>> HandleAsync(CreateBankCategoryCommand command, CancellationToken ct = default)
    {
        var enabled = await access.EnabledAsync(ct);
        if (!enabled.IsSuccess)
            return BankingAccess.Failed<BankCategoryView>(enabled);
        // A child takes its parent's kind; only a top-level one needs a wire kind, exactly as written.
        var kind = Enum.GetValues<CategoryKind>().Cast<CategoryKind?>().FirstOrDefault(k => BankLines.Wire(k!.Value) == command.Kind);
        if (command.ParentId is null && kind is null)
            return Result.Failure<BankCategoryView>(FieldErrors.At(
                new Failure(BankingCodes.CategoryKind, "A top-level category is expense, income or transfer.", null), "kind"));
        return (await categories.CreateAsync(command.ParentId, command.Name, kind ?? CategoryKind.Expense, ct)).Map(BankCategoryView.Of);
    }
}

[RequestHandler<UpdateBankCategoryCommand, BankCategoryView>]
public sealed class UpdateBankCategoryHandler(BankingAccess access, ICategoryStore categories) : IRequestHandler<UpdateBankCategoryCommand, BankCategoryView>
{
    public async ValueTask<Result<BankCategoryView>> HandleAsync(UpdateBankCategoryCommand command, CancellationToken ct = default)
    {
        var enabled = await access.EnabledAsync(ct);
        if (!enabled.IsSuccess)
            return BankingAccess.Failed<BankCategoryView>(enabled);
        var name = command.Name;
        if (command.ResetName)
        {
            if (name is not null)
                return Result.Failure<BankCategoryView>(FieldErrors.At(
                    new Failure(BankingCodes.CategoryName, "A name and a reset of the name are two answers: send one.", null), "name"));
            if (await categories.FindAsync(command.Id, ct) is not { } found)
                return Result.Failure<BankCategoryView>(new NotFoundFailure("bank category", command.Id.ToString()));
            if (found.Key is null || CategorySeed.Find(found.Key) is not { } seed)
                return Result.Failure<BankCategoryView>(FieldErrors.At(
                    new Failure(BankingCodes.CategoryName, "Only a starter category has a name to go back to.", null), "name"));
            name = seed.Name;
        }
        return (await categories.UpdateAsync(command.Id, name, command.ParentId, command.MoveParent, command.Archived, ct)).Map(BankCategoryView.Of);
    }
}

[RequestHandler<MergeBankCategoryCommand, BankCategoryView>]
public sealed class MergeBankCategoryHandler(BankingAccess access, ICategoryStore categories) : IRequestHandler<MergeBankCategoryCommand, BankCategoryView>
{
    public async ValueTask<Result<BankCategoryView>> HandleAsync(MergeBankCategoryCommand command, CancellationToken ct = default)
    {
        var enabled = await access.EnabledAsync(ct);
        if (!enabled.IsSuccess)
            return BankingAccess.Failed<BankCategoryView>(enabled);
        var merged = await categories.MergeAsync(command.Id, command.Into, ct);
        // The store answers with the source; the caller wants the target, as it now stands.
        return merged.TryGetFailure(out var failure)
            ? Result.Failure<BankCategoryView>(failure!)
            : Result.Success(BankCategoryView.Of((await categories.FindAsync(command.Into, ct))!));
    }
}
