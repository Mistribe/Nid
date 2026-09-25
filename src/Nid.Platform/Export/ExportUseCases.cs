using System.Text.Json;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Platform.Export;

public sealed record ExportManifestQuery : IQuery<ExportManifestView>;

public sealed record ExportPageQuery(string Section, string? Cursor, int? Limit) : IQuery<ExportPageView>;

public sealed record ExportSectionCount(string Name, int Count);

/// <summary>Exposes every item of the model: the output guard then proves the whole export is the reader's to see.</summary>
public sealed record ExportManifestView(string Format, int Version, DateTimeOffset ExportedAt, string Revision, IReadOnlyList<ExportSectionCount> Sections)
    : IExposesItems
{
    internal IReadOnlyCollection<ItemId> Items { get; init; } = [];
    public IEnumerable<ItemId> ExposedItems() => Items;
}

public sealed record ExportPageView(string Section, IReadOnlyList<JsonElement> Entities, string? NextCursor) : IExposesItems
{
    internal IReadOnlyCollection<ItemId> Items { get; init; } = [];
    public IEnumerable<ItemId> ExposedItems() => Items;
}

/// <summary>The reader's model, rebuilt for each call (1D2 design §3).</summary>
public sealed class ExportBuilder(IEnumerable<IImportSection> sections, IPersonRegistry persons, IActorAccessor accessor)
{
    public const int MaxPage = 500;

    public async Task<Result<ExportModel>> BuildAsync(CancellationToken ct)
    {
        if (accessor.Current?.PersonId is not { } reader)
            return Result.Failure<ExportModel>(new UnauthorizedFailure("An export is read by a person.", null));
        return Result.Success(await ExportModel.BuildAsync(reader, await persons.ListAsync(ct), sections, ct));
    }
}

[RequestHandler<ExportManifestQuery, ExportManifestView>]
public sealed class ExportManifestHandler(ExportBuilder builder, IClock clock) : IRequestHandler<ExportManifestQuery, ExportManifestView>
{
    public async ValueTask<Result<ExportManifestView>> HandleAsync(ExportManifestQuery query, CancellationToken ct = default)
    {
        if (!(await builder.BuildAsync(ct)).TryGetValue(out var model))
            return Result.Failure<ExportManifestView>(new UnauthorizedFailure("An export is read by a person.", null));
        return Result.Success(new ExportManifestView(
            "nid.data", 1, clock.GetCurrentInstant().ToDateTimeOffset(), model!.Revision,
            model.Sections.Select(s => new ExportSectionCount(s.Name, s.Entities.Count)).ToList()) { Items = model.Items });
    }
}

[RequestHandler<ExportPageQuery, ExportPageView>]
public sealed class ExportPageHandler(ExportBuilder builder) : IRequestHandler<ExportPageQuery, ExportPageView>
{
    public async ValueTask<Result<ExportPageView>> HandleAsync(ExportPageQuery query, CancellationToken ct = default)
    {
        if (!(await builder.BuildAsync(ct)).TryGetValue(out var model))
            return Result.Failure<ExportPageView>(new UnauthorizedFailure("An export is read by a person.", null));

        var section = model!.Sections.FirstOrDefault(s => s.Name == query.Section);
        if (section is null)
            return Result.Failure<ExportPageView>(new NotFoundFailure("export section", query.Section));

        var offset = 0;
        if (query.Cursor is { } cursor)
        {
            if (!ExportCursor.TryDecode(cursor, out var revision, out offset) || offset > section.Entities.Count)
                return Result.Failure<ExportPageView>(new Failure(ExportCodes.BadCursor, "This cursor cannot be read.", null));
            if (revision != model.Revision)
                return Result.Failure<ExportPageView>(new Failure(ExportCodes.RevisionChanged,
                    "The data changed during the export: start it again.", null));
        }

        var limit = Math.Clamp(query.Limit ?? ExportBuilder.MaxPage, 1, ExportBuilder.MaxPage);
        var entities = section.Entities.Skip(offset).Take(limit).ToList();
        var next = offset + entities.Count < section.Entities.Count ? ExportCursor.Encode(model.Revision, offset + entities.Count) : null;
        return Result.Success(new ExportPageView(section.Name, entities, next) { Items = model.Items });
    }
}
