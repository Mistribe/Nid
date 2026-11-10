using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using Nid.Core.Contracts;
using Nid.Host.IntegrationTests.Admin;
using Nid.Platform.Contracts;
using Nid.TestSupport;

namespace Nid.Host.IntegrationTests.Mcp;

/// <summary>Issue #23: the assistant proposes deleting a goal created by mistake; only an adult's approval deletes it.</summary>
public class McpItemDeleteTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string Proposals = ProposalKindHelpers.Proposals;

    private static async Task<McpClient> ClientAsync(NidApp app, string preset)
        => await McpHarness.ClientAsync(app, (await TokenApi.CreateAsync(app, app.AdultA, preset)).Secret);

    private static async Task<(bool Error, string Text)> CallAsync(McpClient client, string tool, object args)
    {
        var result = await client.CallToolAsync(tool, JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(args))!);
        return (result.IsError == true, McpHarness.TextOf(result));
    }

    private static async Task<JsonElement> ProposeAsync(McpClient client, string tool, object args)
    {
        var (error, text) = await CallAsync(client, tool, args);
        Assert.False(error, text);
        return JsonDocument.Parse(text).RootElement.GetProperty("proposal");
    }

    private static async Task<uint> VersionAsync(NidApp app, ItemId goal)
        => JsonDocument.Parse(await app.GetRawAsync(app.AdultA, $"/api/v1/goals/{goal.Value}/deletion")).RootElement.GetProperty("version").GetUInt32();

    private static async Task<uint> GoalVersionAsync(NidApp app, ItemId goal)
        => JsonDocument.Parse(await app.GetRawAsync(app.AdultA, $"/api/v1/goals/{goal.Value}")).RootElement.GetProperty("version").GetUInt32();

    /// <summary>Read from the store: once its goal is gone, nobody reads the proposal through the API (spec: proposals are history).</summary>
    private static async Task<string> StatusAsync(NidApp app, Guid proposal)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var stored = await scope.ServiceProvider.GetRequiredService<IProposalStore>().FindAsync(proposal, TestContext.Current.CancellationToken);
        return stored!.Status.ToString().ToLowerInvariant();
    }

    /// <summary>The state of every decider's notice of the proposal (one adult here).</summary>
    private static async Task<string> NoticeAsync(NidApp app, JsonElement proposal)
    {
        await using var platform = PostgresFixture.NewPlatformContext(app.ConnectionString);
        var key = ProposalNotices.Key(proposal.GetProperty("id").GetGuid());
        return Assert.Single((await platform.Notifications.AsNoTracking().Where(n => n.Key == key).ToListAsync(TestContext.Current.CancellationToken))
            .Select(n => n.State).Distinct());
    }

    private static async Task<HttpResponseMessage> ApproveAsync(NidApp app, JsonElement proposal, object? edited)
        => await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Proposals}/{proposal.GetProperty("id").GetGuid()}/approve",
            new { version = proposal.GetProperty("version").GetUInt32(), editedPayload = edited });

    private static async Task<HttpStatusCode> GoalStatusAsync(NidApp app, ItemId goal)
        => (await app.RawAsync(app.AdultA, HttpMethod.Get, $"/api/v1/goals/{goal.Value}")).StatusCode;

    [Fact]
    public async Task Assistant_proposes_a_delete_and_nothing_is_deleted()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var goal = await app.NewGoalAsync("Voyage", app.AdultA);
        await using var client = await ClientAsync(app, TokenPresets.Propose);

        var proposal = await ProposeAsync(client, "propose_delete_goal", new { id = goal.Value, version = await VersionAsync(app, goal) });

        Assert.Equal("pending", proposal.GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.OK, await GoalStatusAsync(app, goal));
    }

    [Fact]
    public async Task Approving_deletes_the_goal_and_records_the_approval()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var goal = await app.NewGoalAsync("Voyage", app.AdultA);
        await using var client = await ClientAsync(app, TokenPresets.Propose);
        var proposal = await ProposeAsync(client, "propose_delete_goal", new { id = goal.Value, version = await VersionAsync(app, goal) });

        var response = await ApproveAsync(app, proposal, new { id = goal.Value, version = await VersionAsync(app, goal) });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("approved", await StatusAsync(app, proposal.GetProperty("id").GetGuid()));
        Assert.Equal(HttpStatusCode.NotFound, await GoalStatusAsync(app, goal));
    }

    [Fact]
    public async Task Approval_supersedes_other_pending_proposals_on_the_goal()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var goal = await app.NewGoalAsync("Voyage", app.AdultA);
        await using var client = await ClientAsync(app, TokenPresets.Propose);
        var version = await VersionAsync(app, goal);
        var edit = await ProposeAsync(client, "propose_edit_goal", new { id = goal.Value, body = new { name = "Vacances", targetAmount = "10000", version = await GoalVersionAsync(app, goal) } });
        var delete = await ProposeAsync(client, "propose_delete_goal", new { id = goal.Value, version });

        var response = await ApproveAsync(app, delete, new { id = goal.Value, version });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("superseded", await StatusAsync(app, edit.GetProperty("id").GetGuid()));
        Assert.Equal("approved", await StatusAsync(app, delete.GetProperty("id").GetGuid()));
        Assert.Equal(NotificationStates.Resolved, await NoticeAsync(app, edit));
    }

    /// <summary>Proposing runs the delete dry: the goal's other proposals keep their notice, as they stay pending.</summary>
    [Fact]
    public async Task Proposing_a_delete_keeps_the_other_proposals_notices_open()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var goal = await app.NewGoalAsync("Voyage", app.AdultA);
        await using var client = await ClientAsync(app, TokenPresets.Propose);
        var edit = await ProposeAsync(client, "propose_edit_goal", new { id = goal.Value, body = new { name = "Vacances", targetAmount = "10000", version = await GoalVersionAsync(app, goal) } });

        await ProposeAsync(client, "propose_delete_goal", new { id = goal.Value, version = await VersionAsync(app, goal) });

        Assert.Equal("pending", await StatusAsync(app, edit.GetProperty("id").GetGuid()));
        Assert.Equal(NotificationStates.Open, await NoticeAsync(app, edit));
    }

    [Fact]
    public async Task Approval_with_a_stale_version_fails_and_keeps_the_goal()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var goal = await app.NewGoalAsync("Voyage", app.AdultA);
        await using var client = await ClientAsync(app, TokenPresets.Propose);
        var stale = await VersionAsync(app, goal);
        var proposal = await ProposeAsync(client, "propose_delete_goal", new { id = goal.Value, version = stale });
        var rename = await app.RawAsync(app.AdultA, HttpMethod.Put, $"/api/v1/goals/{goal.Value}",
            new { name = "Vacances", targetAmount = "10000", version = await GoalVersionAsync(app, goal) });
        Assert.True(rename.IsSuccessStatusCode, await rename.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        var response = await ApproveAsync(app, proposal, null);

        Assert.False(response.IsSuccessStatusCode);
        Assert.Equal(HttpStatusCode.OK, await GoalStatusAsync(app, goal));
        Assert.Equal("pending", await StatusAsync(app, proposal.GetProperty("id").GetGuid()));
    }

    [Fact]
    public async Task Direct_apply_by_a_write_direct_token_is_refused()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var goal = await app.NewGoalAsync("Voyage", app.AdultA);
        await using var client = await ClientAsync(app, TokenPresets.Direct);

        var (error, text) = await CallAsync(client, "propose_delete_goal", new { id = goal.Value, version = await VersionAsync(app, goal), apply = true });

        Assert.True(error);
        Assert.Contains(BrowserOnlyCodes.Required, text);
        Assert.Equal(HttpStatusCode.OK, await GoalStatusAsync(app, goal));
    }

    [Fact]
    public async Task Approving_deletes_the_income_source()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var source = await app.NewIncomeSourceAsync("Salaire", app.AdultA);
        async Task<uint> SourceVersionAsync()
            => JsonDocument.Parse(await app.GetRawAsync(app.AdultA, $"/api/v1/income/sources/{source.Value}/deletion")).RootElement.GetProperty("version").GetUInt32();
        await using var client = await ClientAsync(app, TokenPresets.Propose);
        var proposal = await ProposeAsync(client, "propose_delete_income_source", new { id = source.Value, version = await SourceVersionAsync() });

        var response = await ApproveAsync(app, proposal, new { id = source.Value, version = await SourceVersionAsync() });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("approved", await StatusAsync(app, proposal.GetProperty("id").GetGuid()));
        Assert.Equal(HttpStatusCode.NotFound, (await app.RawAsync(app.AdultA, HttpMethod.Get, $"/api/v1/income/sources/{source.Value}")).StatusCode);
    }

    [Fact]
    public async Task Approving_deletes_the_envelope()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var envelope = await app.NewEnvelopeAsync("PEA", app.AdultA, Visibility.Shared);
        async Task<uint> EnvelopeVersionAsync()
            => JsonDocument.Parse(await app.GetRawAsync(app.AdultA, $"/api/v1/investments/envelopes/{envelope.Value}/deletion")).RootElement.GetProperty("version").GetUInt32();
        await using var client = await ClientAsync(app, TokenPresets.Propose);
        var proposal = await ProposeAsync(client, "propose_delete_envelope", new { id = envelope.Value, version = await EnvelopeVersionAsync() });

        var response = await ApproveAsync(app, proposal, new { id = envelope.Value, version = await EnvelopeVersionAsync() });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("approved", await StatusAsync(app, proposal.GetProperty("id").GetGuid()));
        Assert.Equal(HttpStatusCode.NotFound, (await app.RawAsync(app.AdultA, HttpMethod.Get, $"/api/v1/investments/envelopes/{envelope.Value}")).StatusCode);
    }
}
