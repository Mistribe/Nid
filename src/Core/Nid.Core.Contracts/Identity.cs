namespace Nid.Core.Contracts;

public readonly record struct PersonId(Guid Value)
{
    public static PersonId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}

public readonly record struct ItemId(Guid Value)
{
    public static ItemId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}

public enum PersonKind
{
    Adult,
    Dependent,
}

public enum Visibility
{
    Shared,
    Private,
}

/// <summary>
/// #294: what a shared item lets an adult who is not its owner do (<c>authorization.md</c> §5). A private item has no
/// level: it is stored, and read, as <see cref="View"/>.
/// </summary>
public enum ShareLevel
{
    /// <summary>Every adult reads it, only its owners write it.</summary>
    View,

    /// <summary>Every adult reads it and writes it, without becoming an owner.</summary>
    Edit,
}

/// <summary>One owner of an item. <paramref name="ShareBp"/> is in basis points: 10 000 = 100 %.</summary>
public sealed record Owner(PersonId PersonId, PersonKind Kind, int ShareBp);
