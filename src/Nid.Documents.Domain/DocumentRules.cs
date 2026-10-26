using Nid.Documents.Contracts;

namespace Nid.Documents.Domain;

public sealed record LinkFacts(string TargetKind, Guid TargetId, Guid? AnchorItemId);

/// <summary>The actor is always an adult: dependents cannot sign in.</summary>
public sealed record ActorFacts(
    Guid Person,
    IReadOnlySet<Guid> Dependents,
    IReadOnlySet<Guid> ReadableItems,
    IReadOnlySet<Guid> WritableItems);

public static class DocumentRules
{
    public static bool CanReadLink(LinkFacts link, ActorFacts actor) => Allows(link, actor, actor.ReadableItems);

    public static bool CanWriteLink(LinkFacts link, ActorFacts actor) => Allows(link, actor, actor.WritableItems);

    public static bool CanRead(Guid uploadedBy, IEnumerable<LinkFacts> links, ActorFacts actor)
        => uploadedBy == actor.Person || links.Any(l => CanReadLink(l, actor));

    public static bool CanEdit(Guid uploadedBy, IEnumerable<LinkFacts> links, ActorFacts actor)
        => uploadedBy == actor.Person || links.Any(l => CanWriteLink(l, actor));

    // A link with an anchor is judged on the anchor, whatever its kind.
    private static bool Allows(LinkFacts link, ActorFacts actor, IReadOnlySet<Guid> items) => link switch
    {
        { AnchorItemId: { } anchor } => items.Contains(anchor),
        { TargetKind: TargetKinds.CorePerson } => link.TargetId == actor.Person || actor.Dependents.Contains(link.TargetId),
        { TargetKind: TargetKinds.CoreHousehold } => true,
        _ => false,
    };
}
