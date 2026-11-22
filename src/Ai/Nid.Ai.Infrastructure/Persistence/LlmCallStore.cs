using Nid.Ai.Application;

namespace Nid.Ai.Infrastructure.Persistence;

public sealed class LlmCallStore(AiDbContext db) : ILlmCalls
{
    public async Task RecordAsync(LlmCallRecord call, CancellationToken ct)
    {
        db.LlmCalls.Add(new LlmCallRow
        {
            Id = call.Id, CreatedAt = call.CreatedAt, PersonId = call.PersonId, Task = call.Task, Use = call.Use, WorkerId = call.WorkerId,
            ConversationId = call.ConversationId, JobId = call.JobId, Model = call.Model, TokensIn = call.TokensIn, TokensOut = call.TokensOut,
            Status = call.Status, Code = call.Code, ProfileId = call.ProfileId, Provider = call.Provider, Tier = call.Tier,
        });
        await db.SaveChangesAsync(ct);
    }
}
