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

/// <summary>One owner of an item. <paramref name="ShareBp"/> is in basis points: 10 000 = 100 %.</summary>
public sealed record Owner(PersonId PersonId, PersonKind Kind, int ShareBp);
