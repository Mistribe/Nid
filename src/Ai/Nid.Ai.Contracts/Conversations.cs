using NodaTime;

namespace Nid.Ai.Contracts;

/// <summary>A conversation in its author's list (#531, <c>docs/ai/README.md</c> §9): never anyone else's.</summary>
public sealed record ConversationSummary(Guid Id, string Title, string? Provider, string? Model, Instant CreatedAt, Instant UpdatedAt);

public sealed record ConversationPage(IReadOnlyList<ConversationSummary> Items, int Total, int Page)
{
    public const int PageSize = 30;
}

/// <summary>One message: the person's (<c>user</c>) or the assistant's, with the tools it called and the items they
/// returned, for display. Never a tool result (decision E).</summary>
public sealed record ConversationMessageView(string Role, string Content, IReadOnlyList<string> Tools, IReadOnlyList<Guid> Items, Instant CreatedAt);

/// <remarks><see cref="PolicyClass"/> (#532): the class of its first turn's destination, which every later turn keeps (§5.3).</remarks>
public sealed record ConversationView(Guid Id, string Title, string? Provider, string? Model, Instant CreatedAt, IReadOnlyList<ConversationMessageView> Messages,
    string? PolicyClass = null);
