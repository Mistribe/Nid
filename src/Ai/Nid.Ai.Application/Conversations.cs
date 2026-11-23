using Microsoft.Extensions.Logging;
using Nid.Ai.Contracts;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Ai.Application;

/// <summary>A message to keep: who spoke, what, and for an answer the tools it called and the items they returned.</summary>
public sealed record NewConversationMessage(string Role, string Content, IReadOnlyList<string> Tools, IReadOnlyList<Guid> Items);

/// <summary>
/// The assistant's conversations (#531, decisions E and O, <c>docs/ai/README.md</c> §9): private to their author, admin
/// included; never a tool result. Every read and delete takes the author: another person's conversation does not exist.
/// </summary>
public interface IConversations
{
    Task<ConversationView?> FindAsync(Guid id, PersonId author, CancellationToken ct);

    Task<(IReadOnlyList<ConversationSummary> Items, int Total)> PageAsync(PersonId author, int skip, int take, CancellationToken ct);

    /// <summary>A new conversation, titled after its first question, with its first exchange.</summary>
    Task StartAsync(Guid id, PersonId author, string title, string? policyClass, IReadOnlyList<NewConversationMessage> messages, string? provider, string? model,
        Instant at, CancellationToken ct);

    /// <summary>An exchange appended to the author's conversation; false when it is not theirs or no longer exists.</summary>
    Task<bool> AppendAsync(Guid id, PersonId author, IReadOnlyList<NewConversationMessage> messages, string? provider, string? model,
        Instant at, CancellationToken ct);

    Task<bool> DeleteAsync(Guid id, PersonId author, CancellationToken ct);

    /// <summary>Conversations last updated before <paramref name="before"/>, with their messages; the number deleted.</summary>
    Task<int> PurgeAsync(Instant before, CancellationToken ct);
}

public static class ConversationRules
{
    /// <summary>The household's setting (§9): 30 days by default, from 1 day to a year.</summary>
    public const int DefaultRetentionDays = 30;
    public const int MaxRetentionDays = 365;
    public const int MaxTitle = 120;

    public const string NotFound = "ai.conversation_not_found";
    public const string InvalidRetention = "ai.invalid_retention";

    public static string Title(string question)
    {
        var line = question.Trim().ReplaceLineEndings(" ");
        return line.Length <= MaxTitle ? line : line[..(MaxTitle - 1)] + "…";
    }
}

public sealed record ListConversationsQuery(int? Page) : IQuery<ConversationPage>;

public sealed record GetConversationQuery(Guid Id) : IQuery<ConversationView>;

public sealed record DeleteConversationCommand(Guid Id) : ICommand<Guid>, IBrowserOnly;

/// <summary>Any adult sets how long conversations are kept (§12).</summary>
public sealed record SetConversationRetentionCommand(int Days) : ICommand<AiSettingsView>, IBrowserOnly;

[RequestHandler<ListConversationsQuery, ConversationPage>]
public sealed class ListConversationsHandler(IConversations conversations, IActorAccessor actors) : IRequestHandler<ListConversationsQuery, ConversationPage>
{
    public async ValueTask<Result<ConversationPage>> HandleAsync(ListConversationsQuery query, CancellationToken ct = default)
    {
        var (page, skip) = ListPage.Of(query.Page, ConversationPage.PageSize);
        var (items, total) = await conversations.PageAsync(actors.Current!.PersonId!.Value, skip, ConversationPage.PageSize, ct);
        return Result.Success(new ConversationPage(items, total, page));
    }
}

[RequestHandler<GetConversationQuery, ConversationView>]
public sealed class GetConversationHandler(IConversations conversations, IActorAccessor actors) : IRequestHandler<GetConversationQuery, ConversationView>
{
    public async ValueTask<Result<ConversationView>> HandleAsync(GetConversationQuery query, CancellationToken ct = default)
        => await conversations.FindAsync(query.Id, actors.Current!.PersonId!.Value, ct) is { } found
            ? Result.Success(found)
            : Result.Failure<ConversationView>(new NotFoundFailure("conversation", query.Id.ToString()));
}

[RequestHandler<DeleteConversationCommand, Guid>]
public sealed class DeleteConversationHandler(IConversations conversations, IActorAccessor actors) : IRequestHandler<DeleteConversationCommand, Guid>
{
    public async ValueTask<Result<Guid>> HandleAsync(DeleteConversationCommand command, CancellationToken ct = default)
        => await conversations.DeleteAsync(command.Id, actors.Current!.PersonId!.Value, ct)
            ? Result.Success(command.Id)
            : Result.Failure<Guid>(new NotFoundFailure("conversation", command.Id.ToString()));
}

[RequestHandler<SetConversationRetentionCommand, AiSettingsView>]
public sealed class SetConversationRetentionHandler(IAiSettings settings) : IRequestHandler<SetConversationRetentionCommand, AiSettingsView>
{
    public async ValueTask<Result<AiSettingsView>> HandleAsync(SetConversationRetentionCommand command, CancellationToken ct = default)
    {
        if (command.Days is < 1 or > ConversationRules.MaxRetentionDays)
            return Result.Failure<AiSettingsView>(new Failure(ConversationRules.InvalidRetention, "1 to 365 days.", null));
        await settings.SetConversationRetentionDaysAsync(command.Days, ct);
        return Result.Success(await AiSettingsViews.ReadAsync(settings, ct));
    }
}

/// <summary>Deletes the conversations older than the household's retention (§9). Their text is not audited: the pass logs its
/// count only.</summary>
public interface IConversationPurge
{
    Task<int> RunOnceAsync(CancellationToken ct);
}

public sealed partial class ConversationPurge(IConversations conversations, IAiSettings settings, IClock clock, ILogger<ConversationPurge> logger)
    : IConversationPurge
{
    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        var days = await settings.ConversationRetentionDaysAsync(ct);
        var deleted = await conversations.PurgeAsync(clock.GetCurrentInstant() - NodaTime.Duration.FromDays(days), ct);
        if (deleted > 0)
            LogPurged(deleted);
        return deleted;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Deleted {Count} assistant conversations past their retention")]
    private partial void LogPurged(int count);
}
