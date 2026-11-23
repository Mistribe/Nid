namespace Nid.Documents.Contracts;

/// <summary>
/// #516: aimed at one document the actor must read (design §7). The document behavior answers « not found » for one that does not
/// exist or that the actor does not read (the two are not told apart), and hands the document, its links and the actor's rights to the
/// handler.
/// </summary>
public interface IReadsDocument
{
    Guid DocumentId { get; }
}

/// <summary>#516: as <see cref="IReadsDocument"/>, and forbidden for a document the actor reads but may not edit.</summary>
public interface IEditsDocument : IReadsDocument;
