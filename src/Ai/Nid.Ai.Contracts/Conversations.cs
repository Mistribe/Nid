using NodaTime;

namespace Nid.Ai.Contracts;

/// <summary>A conversation in its author's list (#531, <c>docs/ai/README.md</c> §9): never anyone else's. #175: the tokens of
/// its model calls and their estimated cost in euros, a decimal string; null when no call was priced (a worker's model).</summary>
public sealed record ConversationSummary(Guid Id, string Title, string? Provider, string? Model, Instant CreatedAt, Instant UpdatedAt,
    int TokensIn = 0, int TokensOut = 0, string? CostEur = null);

public sealed record ConversationPage(IReadOnlyList<ConversationSummary> Items, int Total, int Page)
{
    public const int PageSize = 30;
}

/// <summary>One message: the person's (<c>user</c>) or the assistant's, with the tools it called and the items they
/// returned, for display. Never a tool result (decision E).</summary>
public sealed record ConversationMessageView(string Role, string Content, IReadOnlyList<string> Tools, IReadOnlyList<Guid> Items, Instant CreatedAt);

/// <remarks><see cref="PolicyClass"/> (#532): the class of its first turn's destination, which every later turn keeps (§5.3).
/// Tokens and cost (#175): as <see cref="ConversationSummary"/> sums them.</remarks>
public sealed record ConversationView(Guid Id, string Title, string? Provider, string? Model, Instant CreatedAt, IReadOnlyList<ConversationMessageView> Messages,
    string? PolicyClass = null, int TokensIn = 0, int TokensOut = 0, string? CostEur = null);
