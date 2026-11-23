using Microsoft.EntityFrameworkCore;
using Nid.Ai.Application;
using Nid.Ai.Contracts;
using Nid.Core.Contracts;
using NodaTime;

namespace Nid.Ai.Infrastructure.Persistence;

/// <summary><see cref="IConversations"/>: every query filters on the author in SQL, so another person's conversation is never
/// loaded, admin included (§9).</summary>
public sealed class ConversationStore(AiDbContext db) : IConversations
{
    public async Task<ConversationView?> FindAsync(Guid id, PersonId author, CancellationToken ct)
    {
        var conversation = await db.Conversations.AsNoTracking().SingleOrDefaultAsync(c => c.Id == id && c.PersonId == author.Value, ct);
        if (conversation is null)
            return null;
        var messages = await db.ConversationMessages.AsNoTracking().Where(m => m.ConversationId == id).OrderBy(m => m.Seq).ToListAsync(ct);
        return new ConversationView(conversation.Id, conversation.Title, conversation.Provider, conversation.Model, conversation.CreatedAt,
            [.. messages.Select(m => new ConversationMessageView(m.Role, m.Content, m.Tools, m.Items, m.CreatedAt))]);
    }

    public async Task<(IReadOnlyList<ConversationSummary> Items, int Total)> PageAsync(PersonId author, int skip, int take, CancellationToken ct)
    {
        var mine = db.Conversations.AsNoTracking().Where(c => c.PersonId == author.Value);
        var items = await mine.OrderByDescending(c => c.UpdatedAt).ThenByDescending(c => c.Id).Skip(skip).Take(take)
            .Select(c => new ConversationSummary(c.Id, c.Title, c.Provider, c.Model, c.CreatedAt, c.UpdatedAt)).ToListAsync(ct);
        return (items, await mine.CountAsync(ct));
    }

    public async Task StartAsync(Guid id, PersonId author, string title, IReadOnlyList<NewConversationMessage> messages, string? provider, string? model,
        Instant at, CancellationToken ct)
    {
        var conversation = new ConversationRow
        {
            Id = id, PersonId = author.Value, Title = ConversationRules.Title(title), Provider = provider, Model = model,
            CreatedAt = at, UpdatedAt = at,
        };
        db.Conversations.Add(conversation);
        Add(conversation.Id, 0, messages, at);
        await db.SaveChangesAsync(ct);
    }

    public async Task<bool> AppendAsync(Guid id, PersonId author, IReadOnlyList<NewConversationMessage> messages, string? provider, string? model,
        Instant at, CancellationToken ct)
    {
        var conversation = await db.Conversations.SingleOrDefaultAsync(c => c.Id == id && c.PersonId == author.Value, ct);
        if (conversation is null)
            return false; // deleted or purged during the turn
        var next = await db.ConversationMessages.Where(m => m.ConversationId == id).Select(m => (int?)m.Seq).MaxAsync(ct) + 1 ?? 0;
        Add(id, next, messages, at);
        (conversation.Provider, conversation.Model, conversation.UpdatedAt) = (provider ?? conversation.Provider, model ?? conversation.Model, at);
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> DeleteAsync(Guid id, PersonId author, CancellationToken ct)
        => await db.Conversations.Where(c => c.Id == id && c.PersonId == author.Value).ExecuteDeleteAsync(ct) > 0;

    public Task<int> PurgeAsync(Instant before, CancellationToken ct)
        => db.Conversations.Where(c => c.UpdatedAt < before).ExecuteDeleteAsync(ct);

    private void Add(Guid conversation, int first, IReadOnlyList<NewConversationMessage> messages, Instant at)
    {
        foreach (var (message, i) in messages.Select((m, i) => (m, i)))
            db.ConversationMessages.Add(new ConversationMessageRow
            {
                Id = Guid.CreateVersion7(), ConversationId = conversation, Seq = first + i, Role = message.Role, Content = message.Content,
                Tools = [.. message.Tools], Items = [.. message.Items], CreatedAt = at,
            });
    }
}
