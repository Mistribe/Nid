namespace Nid.Core.Contracts;

/// <summary>An entity of the <c>persons</c> section (<c>import-format.md</c> §5.1).</summary>
public sealed record PersonRecord
{
    public required string Ref { get; init; }
    public required string Kind { get; init; }
    public required string DisplayName { get; init; }
    public string? Email { get; init; }
}
