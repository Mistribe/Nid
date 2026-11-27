using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using NodaTime;
using NodaTime.Text;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Assets.Application;

/// <summary>#650: removes a wrong mileage reading, which would skew the cost per km.</summary>
public sealed record DeleteMileageReadingCommand(ItemId ItemId, LocalDate On) : ICommand<LocalDate>, IItemScoped, ITransactional
{
    public AccessKind Access => AccessKind.Write;
}

[RequestHandler<DeleteMileageReadingCommand, LocalDate>]
public sealed class DeleteMileageReadingHandler(IAssetRecordStore assets, IItemRegistry items) : IRequestHandler<DeleteMileageReadingCommand, LocalDate>
{
    public async ValueTask<Result<LocalDate>> HandleAsync(DeleteMileageReadingCommand command, CancellationToken ct = default)
    {
        // Locked with the edit's lock: an edit cannot write the current mileage while its reading goes.
        var loaded = await AssetCheck.LockAsync(assets, items, command.ItemId, ct);
        if (!loaded.TryGetValue(out _))
            return Result.Failure<LocalDate>(loaded.TryGetFailure(out var missing) ? missing! : throw new InvalidOperationException());
        return await assets.RemoveReadingAsync(command.ItemId, command.On, ct)
            ? Result.Success(command.On)
            : Result.Failure<LocalDate>(new NotFoundFailure("mileage_reading", LocalDatePattern.Iso.Format(command.On)));
    }
}
