using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using Nid.Ai.Application;
using Nid.Ai.Contracts;
using Nid.Core.Contracts;
using Nid.Host.Assistant;
using Nid.TestSupport;

namespace Nid.Host.IntegrationTests.Mcp;

/// <summary>#499, decision I (<c>docs/ai/README.md</c> §8.2): the assistant's tools, their data classes, and the in-process call.</summary>
public sealed class AssistantToolTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static JsonElement Args(object value) => JsonSerializer.SerializeToElement(value);

    [Fact]
    public async Task Every_mcp_tool_is_either_allowed_or_excluded()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var tools = app.Services.GetServices<McpServerTool>().Select(t => t.ProtocolTool.Name).ToHashSet();

        // Equal to empty, not Assert.Empty: the failure names the tools.
        Assert.Equal([], tools.Where(t => !AssistantCatalogue.Allowed.ContainsKey(t) && !AssistantCatalogue.Excluded.Contains(t)).Order());
        Assert.Equal([], AssistantCatalogue.Allowed.Keys.Where(AssistantCatalogue.Excluded.Contains).Order());
        Assert.Equal([], AssistantCatalogue.Allowed.Keys.Concat(AssistantCatalogue.Excluded).Where(t => !tools.Contains(t)).Order());
        Assert.Subset(AssistantCatalogue.Excluded.ToHashSet(),
            new HashSet<string> { "upload_document", "request_document_upload", "delete_proposal", "read_document" });
    }

    [Fact]
    public void Every_allowed_tool_declares_known_data_classes()
        => Assert.All(AssistantCatalogue.Allowed, tool =>
        {
            Assert.NotEmpty(tool.Value);
            Assert.All(tool.Value, c => Assert.True(DataClasses.IsKnown(c) && c != DataClasses.None, $"{tool.Key}: {c}"));
        });

    [Fact]
    public async Task No_offered_schema_lets_the_assistant_apply_or_link_a_document()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var offered = app.Services.GetRequiredService<IAssistantTools>().Offered;

        Assert.Equal(AssistantCatalogue.Allowed.Keys.Order(), offered.Select(t => t.Schema.Name).Order());
        Assert.All(offered, tool =>
        {
            Assert.True(tool.Schema.IsValid(), tool.Schema.Name);
            var schema = tool.Schema.Parameters.GetRawText();
            Assert.DoesNotContain("\"apply\"", schema);
            Assert.DoesNotContain("\"documentId\"", schema);
            Assert.DoesNotContain("apply=", tool.Schema.Description);
            Assert.EndsWith(Nid.Host.Mcp.McpSetup.Untrusted, tool.Schema.Description);
            Assert.Equal(AssistantCatalogue.Allowed[tool.Schema.Name], tool.DataClasses);
        });
    }

    [Fact]
    public void A_call_never_carries_apply_or_a_document()
    {
        var arguments = AssistantCatalogue.Arguments(Args(new { apply = true, documentId = Guid.NewGuid(), transactionId = Guid.Empty }));

        Assert.Equal(["transactionId"], arguments.Keys);
    }

    [Fact]
    public async Task A_call_reads_only_what_its_person_can_read()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var alices = await McpSeed.IncomeSourceAsync(app, app.AdultA);
        var bobsPrivate = await McpSeed.IncomeSourceAsync(app, app.AdultB, "private");
        var tools = app.Services.GetRequiredService<IAssistantTools>();
        var ct = TestContext.Current.CancellationToken;

        var list = await tools.CallAsync(app.AdultA, false, "list_income_sources", Args(new { }), 100_000, ct);
        var bobs = await tools.CallAsync(app.AdultA, false, "get_income_source", Args(new { id = bobsPrivate }), 100_000, ct);

        Assert.False(list.Error, list.Text);
        Assert.Contains(alices.ToString(), list.Text);
        Assert.DoesNotContain(bobsPrivate.ToString(), list.Text);
        Assert.True(bobs.Error);
        Assert.DoesNotContain(bobsPrivate.ToString(), bobs.Text);
    }

    [Fact]
    public async Task A_tool_not_offered_is_refused_without_running()
    {
        await using var app = await NidApp.StartAsync(fixture);

        var result = await app.Services.GetRequiredService<IAssistantTools>()
            .CallAsync(app.AdultA, false, "delete_proposal", Args(new { id = Guid.NewGuid(), version = 1 }), 100_000, TestContext.Current.CancellationToken);

        Assert.True(result.Error);
        Assert.Empty(await CallsAsync(app));
    }

    [Fact]
    public async Task A_call_with_malformed_arguments_answers_an_error()
    {
        await using var app = await NidApp.StartAsync(fixture);

        var result = await app.Services.GetRequiredService<IAssistantTools>()
            .CallAsync(app.AdultA, false, "get_income_source", Args(new { id = "not-a-uuid" }), 100_000, TestContext.Current.CancellationToken);

        Assert.True(result.Error);
    }

    [Fact]
    public async Task An_answer_over_the_bound_asks_for_a_narrower_request_instead_of_being_cut()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await McpSeed.IncomeSourceAsync(app, app.AdultA);

        var result = await app.Services.GetRequiredService<IAssistantTools>()
            .CallAsync(app.AdultA, false, "list_income_sources", Args(new { }), 20, TestContext.Current.CancellationToken);

        Assert.True(result.Error);
        Assert.Contains("narrow the request", result.Text);
        Assert.DoesNotContain("Salaire", result.Text);
    }

    [Fact]
    public async Task Every_call_is_audited_via_the_assistant_with_the_tool_and_ids_only()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var source = await McpSeed.IncomeSourceAsync(app, app.AdultA);

        await app.Services.GetRequiredService<IAssistantTools>()
            .CallAsync(app.AdultA, false, "get_income_source", Args(new { id = source, from = "2025-01-01" }), 100_000, TestContext.Current.CancellationToken);

        var call = Assert.Single(await CallsAsync(app));
        Assert.Equal(("assistant", app.AdultA.Value), (call.Via, call.PersonId!.Value));
        Assert.Equal([source], call.ItemIds);
        Assert.Equal(new Dictionary<string, string> { ["tool"] = "get_income_source" }, JsonSerializer.Deserialize<Dictionary<string, string>>(call.Data));
    }

    private static async Task<List<Nid.Platform.Infrastructure.Audit.AuditEntryRow>> CallsAsync(NidApp app)
    {
        await using var platform = PostgresFixture.NewPlatformContext(app.ConnectionString);
        return await platform.AuditLog.Where(r => r.Category == "security" && r.Action == "mcp_tool_called")
            .OrderBy(r => r.Seq).ToListAsync(TestContext.Current.CancellationToken);
    }
}
