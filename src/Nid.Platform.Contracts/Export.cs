using Nid.Core.Contracts;

namespace Nid.Platform.Contracts;

/// <summary>
/// What sections share while one export model is built (1D2 design §2). Platform numbers every person the reader can
/// see (§5.4); a section asks for the ref of each person it writes, which marks that person for <c>persons</c>, and
/// names every item it exports, which the output guard then checks against what the reader may see.
/// </summary>
public sealed class ExportContext(PersonId reader, IReadOnlyDictionary<PersonId, (string Ref, PersonInfo Info)> persons)
{
    private readonly HashSet<PersonId> referenced = [];
    private readonly HashSet<ItemId> exposed = [];

    public PersonId Reader { get; } = reader;

    public string PersonRef(PersonId person)
    {
        referenced.Add(person);
        return persons[person].Ref;
    }

    public PersonInfo Person(PersonId person) => persons[person].Info;

    public IReadOnlyCollection<PersonId> Referenced => referenced;

    public void Expose(ItemId item) => exposed.Add(item);

    public IReadOnlyCollection<ItemId> Exposed => exposed;
}

public static class ExportCodes
{
    /// <summary>A page asked with a cursor from another revision: the data moved during the export (design §3).</summary>
    public const string RevisionChanged = "export.revision_changed";

    public const string BadCursor = "export.bad_cursor";
}
