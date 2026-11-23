using Nid.Banking.Contracts;
using Nid.Banking.Domain;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Banking.Application;

/// <summary>
/// Every category, archived ones included (the web folds them), top-level first. Any signed-in person reads them; each with its use over
/// the last 12 months, from the lines the actor reads only.
/// </summary>
public sealed record ListBankCategoriesQuery : IQuery<IReadOnlyList<BankCategoryView>>;

/// <summary><paramref name="Kind"/> (<c>expense</c>, <c>income</c> or <c>transfer</c>) is required for a top-level category; a child takes its parent's.</summary>
public sealed record CreateBankCategoryCommand(Guid? ParentId, string Name, string? Kind) : ICommand<BankCategoryView>, IRequiresBanking;

/// <summary>
/// A field left out stays; <paramref name="MoveParent"/> says <paramref name="ParentId"/> (null: top-level) is meant;
/// <paramref name="ResetName"/> puts back the starter name of a seeded category, and excludes <paramref name="Name"/>.
/// </summary>
public sealed record UpdateBankCategoryCommand(Guid Id, string? Name, Guid? ParentId, bool MoveParent, bool? Archived, bool ResetName) : ICommand<BankCategoryView>, IRequiresBanking;

/// <summary>Moves the lines and children of <paramref name="Id"/> into <paramref name="Into"/>, which is returned.</summary>
public sealed record MergeBankCategoryCommand(Guid Id, Guid Into) : ICommand<BankCategoryView>, IRequiresBanking;

/// <summary>
/// <paramref name="DefaultName"/> is the starter name of a seeded category, null for the household's own. <paramref name="Lines"/> and
/// <paramref name="LastLineOn"/> (#415): its lines over the last 12 months, its children's included, and the latest one's booking; given by the
/// list only, zero and null on a write's answer.
/// </summary>
public sealed record BankCategoryView(
    Guid Id, Guid? ParentId, string? Key, string Name, string Kind, bool Archived, Guid? MergedIntoId, string? DefaultName, int Lines = 0, LocalDate? LastLineOn = null)
{
    public static BankCategoryView Of(BankCategory c) => new(
        c.Id, c.Parent, c.Key, c.Name, BankLines.Wire(c.Kind), c.ArchivedAt is not null, c.MergedInto, c.Key is null ? null : CategorySeed.Find(c.Key)?.Name);
}

[RequestHandler<ListBankCategoriesQuery, IReadOnlyList<BankCategoryView>>]
public sealed class ListBankCategoriesHandler(
    ICategoryStore categories, BankingAccess access, IAccountStore accounts, ITransactionStore lines, IHouseholdCalendar calendar)
    : IRequestHandler<ListBankCategoriesQuery, IReadOnlyList<BankCategoryView>>
{
    public const int UseMonths = 12;

    public async ValueTask<Result<IReadOnlyList<BankCategoryView>>> HandleAsync(ListBankCategoriesQuery query, CancellationToken ct = default)
    {
        var all = await categories.AllAsync(ct);
        var readable = (await access.ReadableAsync(await accounts.AllAsync(ct), ct)).Where(r => !r.Account.Ignored).Select(r => r.Account.Id).ToList();
        var since = (await calendar.TodayAsync(ct)).PlusMonths(-UseMonths);
        var used = (await lines.CategoryUseAsync(readable, since, ct)).ToDictionary(u => u.Category);
        // A parent's use is its own lines and its children's (one level deep, `banking.category_depth`).
        return Result.Success<IReadOnlyList<BankCategoryView>>([.. all.Select(c =>
        {
            var uses = all.Where(x => x.Id == c.Id || x.Parent == c.Id).Select(x => used.GetValueOrDefault(x.Id)).OfType<CategoryUse>().ToList();
            return BankCategoryView.Of(c) with { Lines = uses.Sum(u => u.Lines), LastLineOn = uses.Count == 0 ? null : uses.Max(u => u.LastOn) };
        })]);
    }
}

[RequestHandler<CreateBankCategoryCommand, BankCategoryView>]
public sealed class CreateBankCategoryHandler(ICategoryStore categories) : IRequestHandler<CreateBankCategoryCommand, BankCategoryView>
{
    public async ValueTask<Result<BankCategoryView>> HandleAsync(CreateBankCategoryCommand command, CancellationToken ct = default)
    {
        // A child takes its parent's kind; only a top-level one needs a wire kind, exactly as written.
        var kind = Enum.GetValues<CategoryKind>().Cast<CategoryKind?>().FirstOrDefault(k => BankLines.Wire(k!.Value) == command.Kind);
        if (command.ParentId is null && kind is null)
            return Result.Failure<BankCategoryView>(FieldErrors.At(
                new Failure(BankingCodes.CategoryKind, "A top-level category is expense, income or transfer.", null), "kind"));
        return (await categories.CreateAsync(command.ParentId, command.Name, kind ?? CategoryKind.Expense, ct)).Map(BankCategoryView.Of);
    }
}

[RequestHandler<UpdateBankCategoryCommand, BankCategoryView>]
public sealed class UpdateBankCategoryHandler(ICategoryStore categories) : IRequestHandler<UpdateBankCategoryCommand, BankCategoryView>
{
    public async ValueTask<Result<BankCategoryView>> HandleAsync(UpdateBankCategoryCommand command, CancellationToken ct = default)
    {
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
public sealed class MergeBankCategoryHandler(ICategoryStore categories) : IRequestHandler<MergeBankCategoryCommand, BankCategoryView>
{
    public async ValueTask<Result<BankCategoryView>> HandleAsync(MergeBankCategoryCommand command, CancellationToken ct = default)
    {
        var merged = await categories.MergeAsync(command.Id, command.Into, ct);
        // The store answers with the source; the caller wants the target, as it now stands.
        return merged.TryGetFailure(out var failure)
            ? Result.Failure<BankCategoryView>(failure!)
            : Result.Success(BankCategoryView.Of((await categories.FindAsync(command.Into, ct))!));
    }
}
