namespace Nid.Core.Contracts;

/// <summary>
/// 3F1 spec §3: what the module owning an item kind allows of that kind's flows, declared once and read by the Core's
/// <c>flows</c> import section. <paramref name="Section"/> is the import section that creates items of the kind (a fact's
/// <c>item</c> names one of its entities); <paramref name="Categories"/> lists the flow kinds allowed and, for each, its
/// categories — a kind absent is refused; <paramref name="Code"/> is the module's refusal code.
/// </summary>
public sealed record FactConvention(
    string Module, string Kind, string Section, string Code, bool CategoryRequired,
    IReadOnlyDictionary<FlowKind, IReadOnlyList<string>> Categories)
{
    /// <summary>Why a flow breaks the convention: the flow's member at fault and a sentence; null when it keeps it.</summary>
    public (string Member, string Message)? Refusal(FlowKind kind, string? category)
    {
        if (!Categories.TryGetValue(kind, out var allowed))
            return ("kind", $"A {Kind}'s flow is {string.Join(" or ", Categories.Keys.Select(Wire))}.");
        if (category is null)
            return CategoryRequired ? ("category", $"A {Kind}'s {Wire(kind)} has a category: one of {string.Join(", ", allowed)}.") : null;
        return allowed.Contains(category) ? null : ("category", $"A {Kind}'s {Wire(kind)} category is one of {string.Join(", ", allowed)}.");
    }

    private static string Wire(FlowKind kind) => kind.ToString().ToLowerInvariant();
}

/// <summary>3F1 spec §3: a module's conventions for the flows of the item kinds it owns. Kinds no module declares keep any category.</summary>
public interface IFactConventions
{
    IReadOnlyList<FactConvention> Conventions { get; }
}
