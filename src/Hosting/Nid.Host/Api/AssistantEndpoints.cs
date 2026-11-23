using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Nid.Ai.Protocol.Contracts;
using Nid.Core.Contracts;
using Nid.Host.Api.Json;
using Nid.Host.Assistant;
using Nid.Platform.Contracts;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;

namespace Nid.Host.Api;

/// <summary>
/// #501: one assistant turn as server-sent events, for the signed-in person in the browser only. The turn is stateless: the
/// question and the person's and the assistant's earlier words (decision O), never a tool result. Events: <c>text</c>,
/// <c>tool</c> (its name, never its result), <c>held_back</c>, <c>done</c> (provider, model, why it stopped, and the links Nid
/// built from the ids the tools returned, decision P) and <c>error</c> (an <c>ai.*</c> code).
/// </summary>
public static class AssistantEndpoints
{
    public const int MaxQuestion = 4_000;
    public const int MaxHistory = 40;
    public const int MaxMessage = 16_000;
    public const int MaxLinks = 10;

    public sealed record AssistantMessageBody(string? Role, string? Content);

    public sealed record AssistantTurnBody(string? Question, IReadOnlyList<AssistantMessageBody>? History);

    public sealed record AssistantTextEvent(string Text);

    public sealed record AssistantToolEvent(string Tool);

    public sealed record AssistantHeldBackEvent(int Count, string Destination, string Placement);

    /// <summary>A page of Nid the answer names: <see cref="Module"/> in the web app's terms (<c>loans</c>, <c>goals</c>…).</summary>
    public sealed record AssistantLinkView(Guid Id, string Module, string Name);

    public sealed record AssistantDoneEvent(string Provider, string Placement, string Model, string Reason, IReadOnlyList<AssistantLinkView> Links);

    public sealed record AssistantErrorEvent(string Code);

    public static void MapAssistantEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapPost("/api/v1/ai/assistant/turn", (AssistantTurnBody body, IActorAccessor actors, AssistantLoop loop, IItemRegistry items,
                    IAccessPolicy policy, CancellationToken ct) =>
                {
                    // Browser-only, as IBrowserOnly: a token never asks the assistant (§8.1).
                    if (actors.Current is not { PersonId: { } person, TokenId: null, Via: ActorOrigin.Ui })
                        return Result.Failure<object>(new Failure(BrowserOnlyCodes.Required, "The assistant is only available from a browser session.", null))
                            .ToHttp(x => x);
                    var reader = new FieldReader();
                    var question = reader.Text(body.Question, "question");
                    if (question is { Length: > MaxQuestion })
                        reader.Add("question", "too_long", $"{MaxQuestion} characters at most.");
                    var history = new List<ChatMessage>();
                    if (body.History is { Count: > MaxHistory })
                        reader.Add("history", "too_long", $"{MaxHistory} messages at most.");
                    foreach (var (message, i) in (body.History ?? []).Select((m, i) => (m, i)))
                    {
                        var role = message.Role switch { "user" => ChatRole.User, "assistant" => ChatRole.Assistant, _ => (ChatRole?)null };
                        if (role is null || message.Content is not { Length: > 0 and <= MaxMessage })
                            reader.Add($"history[{i}]", "invalid", $"A role (user or assistant) and a content of 1 to {MaxMessage} characters.");
                        else
                            history.Add(new ChatMessage(role.Value, message.Content));
                    }
                    return reader.Ok
                        ? TypedResults.ServerSentEvents(StreamAsync(loop, items, policy, person, history, question!, ct))
                        : reader.Problem();
                })
            .RequireAuthorization()
            .AddEndpointFilter<MalformedInputFilter>().AddEndpointFilter<ActorFilter>()
            // The stream's text: its events are described above, read by the web app's own parser.
            .Produces<string>(StatusCodes.Status200OK, contentType: "text/event-stream");
    }

    private static async IAsyncEnumerable<SseItem<string>> StreamAsync(AssistantLoop loop, IItemRegistry items, IAccessPolicy policy, PersonId person,
        IReadOnlyList<ChatMessage> history, string question, [EnumeratorCancellation] CancellationToken ct)
    {
        var answer = new StringBuilder();
        await foreach (var e in loop.RunAsync(person, history, question, ct))
        {
            switch (e)
            {
                case AssistantText text:
                    answer.Append(text.Text);
                    yield return Item("text", JsonSerializer.Serialize(new AssistantTextEvent(text.Text), AiJsonContext.Default.AssistantTextEvent));
                    break;
                case AssistantToolCalled tool:
                    yield return Item("tool", JsonSerializer.Serialize(new AssistantToolEvent(tool.Tool), AiJsonContext.Default.AssistantToolEvent));
                    break;
                case AssistantHeldBack held:
                    yield return Item("held_back", JsonSerializer.Serialize(
                        new AssistantHeldBackEvent(held.Count, held.Destination, Placement(loop)), AiJsonContext.Default.AssistantHeldBackEvent));
                    break;
                case AssistantDone done:
                    var links = await LinksAsync(items, policy, person, loop.Seen, answer.ToString(), ct);
                    yield return Item("done", JsonSerializer.Serialize(
                        new AssistantDoneEvent(done.Provider, Placement(loop), done.Model, done.Reason, links), AiJsonContext.Default.AssistantDoneEvent));
                    break;
                case AssistantFailed failed:
                    yield return Item("error", JsonSerializer.Serialize(new AssistantErrorEvent(failed.Code), AiJsonContext.Default.AssistantErrorEvent));
                    break;
            }
        }
    }

    private static SseItem<string> Item(string type, string json) => new(json, type);

    private static string Placement(AssistantLoop loop) => loop.Destination?.Placement.ToString().ToLowerInvariant() ?? "";

    /// <summary>
    /// Decision P: the items the tools returned that the person reads and the answer names, at most <see cref="MaxLinks"/>, in the
    /// order the tools gave them. A URL the model writes is never one of them: the web app builds each link from its module.
    /// </summary>
    public static async Task<IReadOnlyList<AssistantLinkView>> LinksAsync(
        IItemRegistry items, IAccessPolicy policy, PersonId person, IReadOnlyList<Guid> seen, string answer, CancellationToken ct)
    {
        if (seen.Count == 0 || answer.Length == 0)
            return [];
        var ids = seen.Select(id => new ItemId(id)).ToList();
        var readable = await policy.CheckManyAsync(person, ids, AccessKind.Read, ct);
        var found = await items.FindManyAsync([.. ids.Where(readable.Contains)], ct);
        return [.. ids.Where(found.ContainsKey).Select(id => found[id])
            .Where(item => item.DisplayName.Length >= 2 && answer.Contains(item.DisplayName, StringComparison.OrdinalIgnoreCase))
            .Select(item => WebModule(item) is { } module ? new AssistantLinkView(item.Id.Value, module, item.DisplayName) : null)
            .OfType<AssistantLinkView>().Take(MaxLinks)];
    }

    /// <summary>The web app's route family of an item (<c>itemRoute</c>); null for one without a page (a scenario).</summary>
    private static string? WebModule(ItemAccess item) => (item.Module, item.Kind) switch
    {
        ("realestate", "loan") => "loans",
        ("realestate", "property") => "properties",
        ("childcare", "nanny") => "nannies",
        ("investments" or "income" or "subscriptions" or "goals" or "childcare", _) => item.Module,
        _ => null,
    };
}
