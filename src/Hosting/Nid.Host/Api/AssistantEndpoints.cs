using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Nid.Ai.Application;
using Nid.Ai.Contracts;
using Nid.Ai.Protocol.Contracts;
using Nid.Core.Contracts;
using Nid.Host.Api.Json;
using Nid.Host.Assistant;
using Nid.Platform.Contracts;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Host.Api;

/// <summary>
/// #501: one assistant turn as server-sent events, for the signed-in person in the browser only. #531: a turn belongs to a
/// conversation, its author's alone: the context is rebuilt from its stored messages, the person's and the assistant's words
/// only (decision O), never a tool result. Events: <c>text</c>, <c>tool</c> (its name, never its result), <c>held_back</c>,
/// <c>done</c> (provider, model, why it stopped, the conversation, and the links Nid built from the ids the tools returned,
/// decision P) and <c>error</c> (an <c>ai.*</c> code).
/// </summary>
public static class AssistantEndpoints
{
    public const int MaxQuestion = 4_000;
    public const int MaxLinks = 10;

    public sealed record AssistantTurnBody(string? Question, Guid? ConversationId);

    public sealed record ConversationRetentionBody(int? Days);

    public sealed record AssistantEnabledBody(bool? Enabled);

    public sealed record AssistantOffsiteBody(string? Preference);

    public sealed record AssistantTextEvent(string Text);

    public sealed record AssistantToolEvent(string Tool);

    public sealed record AssistantHeldBackEvent(int Count, string Destination, string Placement);

    /// <summary>A page of Nid the answer names: <see cref="Module"/> in the web app's terms (<c>loans</c>, <c>goals</c>…).</summary>
    public sealed record AssistantLinkView(Guid Id, string Module, string Name);

    /// <summary><see cref="ConversationId"/>: the conversation the exchange was kept in; null when nothing was answered.</summary>
    public sealed record AssistantDoneEvent(string Provider, string Placement, string Model, string Reason, IReadOnlyList<AssistantLinkView> Links,
        Guid? ConversationId);

    /// <summary><see cref="AvailableOn"/> (#175): when paid calls come back, for a budget block (the 1st of next month).</summary>
    public sealed record AssistantErrorEvent(string Code, LocalDate? AvailableOn = null);

    public static void MapAssistantEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapPost("/api/v1/ai/assistant/turn", async (AssistantTurnBody body, IActorAccessor actors, AssistantLoop loop, IConversations conversations,
                    IAssistantPeople people, IItemRegistry items, IAccessPolicy policy, IClock clock, CancellationToken ct) =>
                {
                    // Browser-only, as IBrowserOnly: a token never asks the assistant (§8.1).
                    if (actors.Current is not { PersonId: { } person, TokenId: null, Via: ActorOrigin.Ui })
                        return Result.Failure<object>(new Failure(BrowserOnlyCodes.Required, "The assistant is only available from a browser session.", null))
                            .ToHttp(x => x);
                    // #533: each adult turns it on for themselves (§6.2): that is their consent for their own data.
                    if (!(await people.GetAsync(person, ct)).Enabled)
                        return Result.Failure<object>(new Failure(AssistantPersonCodes.Off, "Turn the assistant on for yourself in Settings > AI first.", null))
                            .ToHttp(x => x);
                    var reader = new FieldReader();
                    var question = reader.Text(body.Question, "question");
                    if (question is { Length: > MaxQuestion })
                        reader.Add("question", "too_long", $"{MaxQuestion} characters at most.");
                    if (!reader.Ok)
                        return reader.Problem();
                    IReadOnlyList<ChatMessage> history = [];
                    string? pinned = null;
                    if (body.ConversationId is { } id)
                    {
                        if (await conversations.FindAsync(id, person, ct) is not { } conversation)
                            return Result.Failure<object>(new NotFoundFailure("conversation", id.ToString())).ToHttp(x => x);
                        pinned = conversation.PolicyClass; // #532: the class of its first turn, never another
                        history = [.. conversation.Messages.Where(m => m.Content.Length > 0)
                            .Select(m => new ChatMessage(m.Role == "user" ? ChatRole.User : ChatRole.Assistant, m.Content))];
                    }
                    return TypedResults.ServerSentEvents(StreamAsync(loop, conversations, items, policy, clock, person, history, question!, body.ConversationId, pinned, ct));
                })
            .RequireAuthorization()
            .AddEndpointFilter<MalformedInputFilter>().AddEndpointFilter<ActorFilter>()
            // The stream's text: its events are described above, read by the web app's own parser.
            .Produces<string>(StatusCodes.Status200OK, contentType: "text/event-stream");

        var conversations = routes.MapGroup("/api/v1/ai/assistant/conversations").RequireAuthorization()
            .AddEndpointFilter<MalformedInputFilter>().AddEndpointFilter<ActorFilter>();
        conversations.MapGet("", async (int? page, IInvoker invoker, CancellationToken ct)
                => (await invoker.InvokeAsync(new ListConversationsQuery(page), ct)).ToHttp(p => p))
            .Returns<ConversationPage>();
        conversations.MapGet("/{id:guid}", async (Guid id, IInvoker invoker, CancellationToken ct)
                => (await invoker.InvokeAsync(new GetConversationQuery(id), ct)).ToHttp(c => c))
            .Returns<ConversationView>();
        conversations.MapDelete("/{id:guid}", async (Guid id, IInvoker invoker, CancellationToken ct) =>
            {
                var deleted = await invoker.InvokeAsync(new DeleteConversationCommand(id), ct);
                return deleted.TryGetValue(out _) ? Results.NoContent() : deleted.ToHttp(v => v);
            })
            .ReturnsNoContent();

        var me = routes.MapGroup("/api/v1/ai/assistant/me").RequireAuthorization()
            .AddEndpointFilter<MalformedInputFilter>().AddEndpointFilter<ActorFilter>();
        me.MapGet("", async (IInvoker invoker, CancellationToken ct)
                => (await invoker.InvokeAsync(new GetAssistantMeQuery(), ct)).ToHttp(view => view))
            .Returns<AssistantMeView>();
        me.MapPut("/enabled", async (AssistantEnabledBody body, IInvoker invoker, CancellationToken ct) =>
            {
                if (body.Enabled is { } enabled)
                    return (await invoker.InvokeAsync(new SetAssistantEnabledCommand(enabled), ct)).ToHttp(change => change.View);
                var reader = new FieldReader();
                reader.Add("enabled", "request.required", "This field is required.");
                return reader.Problem();
            })
            .Returns<AssistantMeView>();
        me.MapPut("/offsite", async (AssistantOffsiteBody body, IInvoker invoker, CancellationToken ct) =>
            {
                var reader = new FieldReader();
                var preference = reader.Text(body.Preference, "preference");
                return reader.Ok
                    ? (await invoker.InvokeAsync(new SetAssistantOffsiteCommand(preference!), ct)).ToHttp(change => change.View)
                    : reader.Problem();
            })
            .Returns<AssistantMeView>();

        routes.MapPut("/api/v1/ai/settings/conversation-retention", async (ConversationRetentionBody body, IInvoker invoker, CancellationToken ct) =>
            {
                if (body.Days is { } days)
                    return (await invoker.InvokeAsync(new SetConversationRetentionCommand(days), ct)).ToHttp(view => view);
                var reader = new FieldReader();
                reader.Add("days", "request.required", "This field is required.");
                return reader.Problem();
            })
            .RequireAuthorization()
            .AddEndpointFilter<MalformedInputFilter>().AddEndpointFilter<ActorFilter>()
            .Returns<AiSettingsView>();
    }

    private static async IAsyncEnumerable<SseItem<string>> StreamAsync(AssistantLoop loop, IConversations conversations, IItemRegistry items,
        IAccessPolicy policy, IClock clock, PersonId person, IReadOnlyList<ChatMessage> history, string question, Guid? conversationId,
        string? pinned, [EnumeratorCancellation] CancellationToken ct)
    {
        var id = conversationId ?? Guid.CreateVersion7(); // known before the turn, so every llm_call of it names it
        var answer = new StringBuilder();
        var called = new List<string>();
        await foreach (var e in loop.RunAsync(person, history, question, ct, id, pinned))
        {
            switch (e)
            {
                case AssistantText text:
                    answer.Append(text.Text);
                    yield return Item("text", JsonSerializer.Serialize(new AssistantTextEvent(text.Text), AiJsonContext.Default.AssistantTextEvent));
                    break;
                case AssistantToolCalled tool:
                    called.Add(tool.Tool);
                    yield return Item("tool", JsonSerializer.Serialize(new AssistantToolEvent(tool.Tool), AiJsonContext.Default.AssistantToolEvent));
                    break;
                case AssistantHeldBack held:
                    yield return Item("held_back", JsonSerializer.Serialize(
                        new AssistantHeldBackEvent(held.Count, held.Destination, Placement(loop)), AiJsonContext.Default.AssistantHeldBackEvent));
                    break;
                case AssistantDone done:
                    var kept = await KeepAsync(conversations, clock, person, id, conversationId is null, loop.Destination?.Class, question,
                        answer.ToString(), called, loop.Seen, done.Provider, done.Model, ct);
                    var links = await LinksAsync(items, policy, person, loop.Seen, answer.ToString(), ct);
                    yield return Item("done", JsonSerializer.Serialize(
                        new AssistantDoneEvent(done.Provider, Placement(loop), done.Model, done.Reason, links, kept ? id : conversationId),
                        AiJsonContext.Default.AssistantDoneEvent));
                    break;
                case AssistantFailed failed:
                    LocalDate? back = failed.Code == JobCodes.BudgetBlocked
                        ? clock.GetCurrentInstant().InUtc().Date.With(DateAdjusters.StartOfMonth).PlusMonths(1)
                        : null;
                    yield return Item("error", JsonSerializer.Serialize(new AssistantErrorEvent(failed.Code, back), AiJsonContext.Default.AssistantErrorEvent));
                    break;
            }
        }
    }

    /// <summary>The exchange, once answered: the question, then the answer with the tools it called and the items they returned.
    /// A turn that wrote nothing keeps nothing.</summary>
    private static async Task<bool> KeepAsync(IConversations conversations, IClock clock, PersonId person, Guid id, bool first, string? policyClass,
        string question, string answer, IReadOnlyList<string> tools, IReadOnlyList<Guid> items, string provider, string model, CancellationToken ct)
    {
        if (answer.Length == 0)
            return false;
        IReadOnlyList<NewConversationMessage> exchange = [new("user", question, [], []), new("assistant", answer, tools, items)];
        var at = clock.GetCurrentInstant();
        if (first)
        {
            await conversations.StartAsync(id, person, question, policyClass, exchange, Blank(provider), Blank(model), at, ct);
            return true;
        }
        return await conversations.AppendAsync(id, person, exchange, Blank(provider), Blank(model), at, ct);
    }

    private static string? Blank(string value) => value.Length == 0 ? null : value;

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
        ("assets", "asset") => "assets",
        ("childcare", "nanny") => "nannies",
        ("investments" or "income" or "subscriptions" or "goals" or "childcare", _) => item.Module,
        _ => null,
    };
}
