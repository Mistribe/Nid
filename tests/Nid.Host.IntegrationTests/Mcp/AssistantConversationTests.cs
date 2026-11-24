using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nid.Ai.Application;
using Nid.Ai.Infrastructure.Persistence;
using Nid.Ai.Protocol.Contracts;
using Nid.Host.IntegrationTests.Export;
using Nid.Platform.Contracts;
using Nid.TestSupport;
using NodaTime;

namespace Nid.Host.IntegrationTests.Mcp;

/// <summary>#531, decisions E and O (<c>docs/ai/README.md</c> §9): the assistant's conversations, kept for their author alone,
/// rebuilt from the person's and the assistant's words, purged with their retention.</summary>
public sealed class AssistantConversationTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string Turn = "/api/v1/ai/assistant/turn";
    private const string Conversations = "/api/v1/ai/assistant/conversations";
    private static readonly LlmDestination Salon = new(LlmPlacement.Local, "salon", WorkerId: Guid.NewGuid());

    private static ScriptedGateway Model() => new(Salon,
        request => request.Messages.Any(m => m.Role == ChatRole.Tool) || request.Messages.Count(m => m.Role == ChatRole.User) > 1
            ? [new(Text: "Vous avez une enveloppe."), new(Usage: new ChatUsage(9, 4, ChatStop.End, "llama3.2"))]
            : [new(ToolCall: new ChatToolCall("c1", "list_envelopes", "{}")), new(Usage: new ChatUsage(5, 1, ChatStop.ToolCalls, "llama3.2"))]);

    private async Task<NidApp> StartAsync(ScriptedGateway model)
    {
        var app = await NidApp.StartAsync(fixture, s => s.Replace(ServiceDescriptor.Scoped<ILlmGateway>(_ => model)));
        await AssistantMe.EnableAsync(app, app.AdultA);
        await AssistantMe.EnableAsync(app, app.AdultB);
        return app;
    }

    [Fact]
    public async Task A_conversation_keeps_its_exchanges_and_rebuilds_the_context_from_words_only()
    {
        var model = Model();
        await using var app = await StartAsync(model);

        var first = await AskAsync(app, app.AdultA, "Mes enveloppes ?");
        await AskAsync(app, app.AdultA, "Et ensuite ?", first);

        // #532: the next turn is pinned to the class of the first from its first step.
        Assert.Equal([null, "local", "local"], model.Requests.Select(r => r.Pin));
        var resumed = model.Requests[^1].Messages;
        Assert.Equal([ChatRole.System, ChatRole.User, ChatRole.Assistant, ChatRole.User], resumed.Select(m => m.Role));
        Assert.Equal(["Mes enveloppes ?", "Vous avez une enveloppe.", "Et ensuite ?"], resumed.Skip(1).Select(m => m.Content));
        var kept = JsonDocument.Parse(await (await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Conversations}/{first}")).Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement;
        Assert.Equal("Mes enveloppes ?", kept.GetProperty("title").GetString());
        Assert.Equal(4, kept.GetProperty("messages").GetArrayLength());
        Assert.Equal("list_envelopes", kept.GetProperty("messages")[1].GetProperty("tools")[0].GetString());
        await using var scope = app.Services.CreateAsyncScope();
        var calls = await scope.ServiceProvider.GetRequiredService<AiDbContext>().LlmCalls.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken);
        Assert.All(calls, call => Assert.Equal(first, call.ConversationId));
    }

    [Fact]
    public async Task Another_adult_neither_lists_reads_continues_nor_deletes_it()
    {
        await using var app = await StartAsync(Model());
        var alices = await AskAsync(app, app.AdultA, "Mes enveloppes ?");

        Assert.Equal(0, (await PageAsync(app, app.AdultB)).GetProperty("total").GetInt32());
        Assert.Equal(HttpStatusCode.NotFound, (await app.RawAsync(app.AdultB, HttpMethod.Get, $"{Conversations}/{alices}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await app.RawAsync(app.AdultB, HttpMethod.Post, Turn, new { question = "Et ?", conversationId = alices })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await app.RawAsync(app.AdultB, HttpMethod.Delete, $"{Conversations}/{alices}")).StatusCode);

        Assert.Equal(1, (await PageAsync(app, app.AdultA)).GetProperty("total").GetInt32());
        Assert.Equal(HttpStatusCode.NoContent, (await app.RawAsync(app.AdultA, HttpMethod.Delete, $"{Conversations}/{alices}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Conversations}/{alices}")).StatusCode);
    }

    [Fact]
    public async Task A_conversation_past_the_retention_is_purged_with_its_messages()
    {
        await using var app = await StartAsync(Model());
        var old = await AskAsync(app, app.AdultA, "Mes enveloppes ?");
        var recent = await AskAsync(app, app.AdultA, "Et aujourd'hui ?");
        // #585: the retention is the administration's.
        Assert.Equal(HttpStatusCode.Forbidden, (await app.RawAsync(app.AdultB, HttpMethod.Put, "/api/v1/ai/settings/conversation-retention", new { days = 7 })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Put, "/api/v1/ai/settings/conversation-retention", new { days = 7 })).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await app.RawAsync(app.AdultA, HttpMethod.Put, "/api/v1/ai/settings/conversation-retention", new { days = 0 })).StatusCode);
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AiDbContext>();
        var eightDaysAgo = scope.ServiceProvider.GetRequiredService<IClock>().GetCurrentInstant() - Duration.FromDays(8);
        await db.Conversations.Where(c => c.Id == old).ExecuteUpdateAsync(s => s.SetProperty(c => c.UpdatedAt, eightDaysAgo), TestContext.Current.CancellationToken);

        Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<IConversationPurge>().RunOnceAsync(TestContext.Current.CancellationToken));

        Assert.Equal([recent], await db.Conversations.AsNoTracking().Select(c => c.Id).ToListAsync(TestContext.Current.CancellationToken));
        Assert.DoesNotContain(await db.ConversationMessages.AsNoTracking().Select(m => m.ConversationId).ToListAsync(TestContext.Current.CancellationToken), id => id == old);
    }

    [Fact]
    public async Task A_conversation_still_in_use_loses_its_messages_past_the_retention()
    {
        // #626: used once a month, a conversation is never purged whole; its old messages are.
        await using var app = await StartAsync(Model());
        var id = await AskAsync(app, app.AdultA, "Mes enveloppes ?");
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AiDbContext>();
        var lastYear = scope.ServiceProvider.GetRequiredService<IClock>().GetCurrentInstant() - Duration.FromDays(365);
        await db.ConversationMessages.Where(m => m.ConversationId == id).ExecuteUpdateAsync(s => s.SetProperty(m => m.CreatedAt, lastYear),
            TestContext.Current.CancellationToken);
        await AskAsync(app, app.AdultA, "Et aujourd'hui ?", id);

        Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<IConversationPurge>().RunOnceAsync(TestContext.Current.CancellationToken));

        var kept = await db.ConversationMessages.AsNoTracking().Where(m => m.ConversationId == id).Select(m => m.Content)
            .ToListAsync(TestContext.Current.CancellationToken);
        Assert.Contains("Et aujourd'hui ?", kept);
        Assert.DoesNotContain("Mes enveloppes ?", kept);
    }

    [Fact]
    public async Task Deleting_a_document_removes_the_answers_that_read_it_in_everyones_conversations()
    {
        // #617, decision R (§6.6): an answer whose tools returned the document goes with it; the question and other answers stay.
        await using var app = await StartAsync(Model());
        var document = await DocumentApi.UploadedAsync(app, app.AdultA, DocumentApi.Pdf, "fileName=bail.pdf&targetKind=core.household&role=other");
        var alices = await KeptAsync(app, app.AdultA, ("Mon bail ?", "Le bail dit 900 euros.", [document]), ("Mes enveloppes ?", "Une enveloppe.", []));
        var bobs = await KeptAsync(app, app.AdultB, ("Le bail ?", "Loyer de 900 euros.", [document, Guid.NewGuid()]));

        await DocumentApi.DeletedAsync(app, app.AdultA, document);

        var alice = await MessagesAsync(app, app.AdultA, alices);
        Assert.Equal(["Mon bail ?", "", "Mes enveloppes ?", "Une enveloppe."], alice.Select(m => m.GetProperty("content").GetString()));
        Assert.Equal([false, true, false, false], alice.Select(m => m.GetProperty("removed").GetBoolean()));
        Assert.Empty(alice[1].GetProperty("items").EnumerateArray());
        var bob = await MessagesAsync(app, app.AdultB, bobs);
        Assert.Equal(["Le bail ?", ""], bob.Select(m => m.GetProperty("content").GetString()));
        Assert.True(bob[1].GetProperty("removed").GetBoolean());
    }

    /// <summary>A conversation kept as the assistant keeps one: each answer with the ids its tools returned.</summary>
    private static async Task<Guid> KeptAsync(NidApp app, Core.Contracts.PersonId author, params (string Question, string Answer, Guid[] Items)[] exchanges)
    {
        var id = Guid.NewGuid();
        await using var scope = app.Services.CreateAsyncScope();
        var conversations = scope.ServiceProvider.GetRequiredService<IConversations>();
        var at = scope.ServiceProvider.GetRequiredService<IClock>().GetCurrentInstant();
        foreach (var (question, answer, items) in exchanges)
        {
            IReadOnlyList<NewConversationMessage> exchange = [new("user", question, [], []), new("assistant", answer, ["get_subscription"], items)];
            if (!await conversations.AppendAsync(id, author, exchange, "salon", "llama3.2", at, TestContext.Current.CancellationToken))
                await conversations.StartAsync(id, author, question, "local", exchange, "salon", "llama3.2", at, TestContext.Current.CancellationToken);
        }
        return id;
    }

    private static async Task<JsonElement[]> MessagesAsync(NidApp app, Core.Contracts.PersonId author, Guid conversation)
    {
        var response = await app.RawAsync(author, HttpMethod.Get, $"{Conversations}/{conversation}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return [.. (await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken)).GetProperty("messages").EnumerateArray()];
    }

    [Fact]
    public async Task The_export_carries_no_conversation()
    {
        await using var app = await StartAsync(Model());
        await AskAsync(app, app.AdultA, "Question tres distinctive ZZCONVERSATION ?");

        var exported = await new ExportClient(app).ExportAllAsync(app.AdultA);

        Assert.DoesNotContain("ZZCONVERSATION", JsonSerializer.Serialize(exported));
    }

    private static async Task<Guid> AskAsync(NidApp app, Core.Contracts.PersonId person, string question, Guid? conversation = null)
    {
        var response = await app.RawAsync(person, HttpMethod.Post, Turn, new { question, conversationId = conversation });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var done = (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Split("\n\n")
            .Single(block => block.StartsWith("event: done", StringComparison.Ordinal));
        return JsonDocument.Parse(done[(done.IndexOf("data: ", StringComparison.Ordinal) + 6)..]).RootElement.GetProperty("conversationId").GetGuid();
    }

    private static async Task<JsonElement> PageAsync(NidApp app, Core.Contracts.PersonId person)
        => await (await app.RawAsync(person, HttpMethod.Get, Conversations)).Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
}
