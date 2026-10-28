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
