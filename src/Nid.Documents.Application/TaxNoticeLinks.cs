using Nid.Core.Contracts;
using Nid.Documents.Contracts;
using Nid.Platform.Contracts;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Documents.Application;

/// <summary>
/// Tax assessments spec §5: the source document of a notice follows it to each declarant's <c>core.person</c>, which the general
/// link rule refuses for another adult. The declarants are checked here, not through the document's target rules.
/// </summary>
public sealed record LinkTaxNoticeCommand(Guid DocumentId, Guid AssessmentId) : ICommand<LinkAdded>;

[RequestHandler<LinkTaxNoticeCommand, LinkAdded>]
public sealed class LinkTaxNoticeHandler(
    DocumentAccess access, IDocumentStore store, ITaxAssessmentDeclarants declarants, IActorAccessor actor, IClock clock)
    : IRequestHandler<LinkTaxNoticeCommand, LinkAdded>
{
    public async ValueTask<Result<LinkAdded>> HandleAsync(LinkTaxNoticeCommand command, CancellationToken ct = default)
    {
        var person = actor.Current!.PersonId!.Value;
        if (await declarants.DeclarantsAsync(command.AssessmentId, ct) is not { } who || !who.Contains(person.Value))
            return Result.Failure<LinkAdded>(new NotFoundFailure("tax assessment", command.AssessmentId.ToString()));

        var read = await access.ReadAsync(command.DocumentId, person, ct);
        if (!read.TryGetValue(out var found))
            return Result.Failure<LinkAdded>(read.TryGetFailure(out var failure) ? failure! : throw new InvalidOperationException());
        if (!found!.CanEdit)
            return Result.Failure<LinkAdded>(new UnauthorizedFailure("The document is read-only for this person.", null));

        var now = clock.GetCurrentInstant();
        var links = found.Links.ToList();
        foreach (var declarant in who.Where(d => !links.Any(l => l.TargetKind == TargetKinds.CorePerson && l.TargetId == d)))
        {
            var link = new LinkRecord(command.DocumentId, TargetKinds.CorePerson, declarant, null, DocumentRoles.TaxNotice, person.Value, now);
            if (await store.AddLinkAsync(link, ct))
                links.Add(link);
        }

        var uploadedBy = found.Document.UploadedBy;
        var before = await access.ReadersAsync(uploadedBy, found.Links.Select(DocumentAccess.Facts).ToList(), ct);
        var after = await access.ReadersAsync(uploadedBy, links.Select(DocumentAccess.Facts).ToList(), ct);
        return Result.Success(new LinkAdded(after.Except(before).Select(p => p.Value).ToList()));
    }
}

/// <summary>
/// What <see cref="LinkTaxNoticeCommand"/> would add, before the notice is saved: the household adults among the declarants who cannot
/// read the document today and would through a <c>tax_notice</c> person link. Writes nothing.
/// </summary>
public sealed record PreviewTaxNoticeLinkQuery(Guid DocumentId, IReadOnlyList<Guid> Declarants) : IQuery<LinkPreview>;

[RequestHandler<PreviewTaxNoticeLinkQuery, LinkPreview>]
public sealed class PreviewTaxNoticeLinkHandler(DocumentAccess access, IPersonRegistry persons, IActorAccessor actor, IClock clock)
    : IRequestHandler<PreviewTaxNoticeLinkQuery, LinkPreview>
{
    public async ValueTask<Result<LinkPreview>> HandleAsync(PreviewTaxNoticeLinkQuery query, CancellationToken ct = default)
    {
        var person = actor.Current!.PersonId!.Value;
        if (!query.Declarants.Contains(person.Value))
            return Result.Failure<LinkPreview>(new NotFoundFailure("declarants", person.Value.ToString()));

        var adults = (await persons.ListAsync(ct)).Where(p => p.Kind == PersonKind.Adult).ToDictionary(p => p.Id.Value);
        if (query.Declarants.Any(d => !adults.ContainsKey(d)))
            return Result.Failure<LinkPreview>(new NotFoundFailure("declarants", string.Join(',', query.Declarants.Where(d => !adults.ContainsKey(d)))));

        var read = await access.ReadAsync(query.DocumentId, person, ct);
        if (!read.TryGetValue(out var found))
            return Result.Failure<LinkPreview>(read.TryGetFailure(out var failure) ? failure! : throw new InvalidOperationException());
        if (!found!.CanEdit)
            return Result.Failure<LinkPreview>(new UnauthorizedFailure("The document is read-only for this person.", null));

        var now = clock.GetCurrentInstant();
        var links = found.Links.ToList();
        links.AddRange(query.Declarants.Where(d => !links.Any(l => l.TargetKind == TargetKinds.CorePerson && l.TargetId == d))
            .Select(d => new LinkRecord(query.DocumentId, TargetKinds.CorePerson, d, null, DocumentRoles.TaxNotice, person.Value, now)));

        var uploadedBy = found.Document.UploadedBy;
        var before = await access.ReadersAsync(uploadedBy, found.Links.Select(DocumentAccess.Facts).ToList(), ct);
        var after = await access.ReadersAsync(uploadedBy, links.Select(DocumentAccess.Facts).ToList(), ct);
        return Result.Success(new LinkPreview(after.Except(before).Where(p => adults.ContainsKey(p.Value))
            .Select(p => new ReaderView(p.Value, adults[p.Value].DisplayName)).ToList()));
    }
}
