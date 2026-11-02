using System.Net;
using System.Text.Json;
using ModelContextProtocol.Client;
using Nid.Core.Contracts;
using Nid.Host.IntegrationTests.Admin;
using Nid.TestSupport;

namespace Nid.Host.IntegrationTests.Mcp;

/// <summary>Tax assessments spec §6 (issue #182): the notice tools, proposed by default, approved by replaying the route.</summary>
public class McpTaxAssessmentToolTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string Route = "/api/v1/tax/assessments";

    private static async Task<McpClient> ClientAsync(NidApp app, string preset)
        => await McpHarness.ClientAsync(app, (await TokenApi.CreateAsync(app, app.AdultA, preset)).Secret);

    private static async Task<(bool Error, string Text)> CallAsync(McpClient client, string tool, object args)
    {
        var result = await client.CallToolAsync(tool, JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(args))!);
        return (result.IsError == true, McpHarness.TextOf(result));
    }

    private static async Task<JsonElement> AnswerAsync(McpClient client, string tool, object args)
    {
        var (error, text) = await CallAsync(client, tool, args);
        Assert.False(error, text);
        return JsonDocument.Parse(text).RootElement;
    }

    private static async Task<JsonElement> PendingAsync(NidApp app, PersonId person)
        => JsonDocument.Parse(await app.GetRawAsync(person, ProposalKindHelpers.Proposals)).RootElement.GetProperty("items");

    private static async Task<HttpResponseMessage> ApproveAsync(NidApp app, PersonId person, Guid proposal)
    {
        var detail = JsonDocument.Parse(await app.GetRawAsync(person, $"{ProposalKindHelpers.Proposals}/{proposal}")).RootElement;
        return await app.RawAsync(person, HttpMethod.Post, $"{ProposalKindHelpers.Proposals}/{proposal}/approve",
            new { version = detail.GetProperty("proposal").GetProperty("version").GetUInt32() });
    }

    private static object Notice(NidApp app, int year, Guid? documentId = null)
        => new
        {
            year,
            declarants = new[] { new { personId = app.AdultA.Value, rank = 1 }, new { personId = app.AdultB.Value, rank = 2 } },
            documentId,
        };

    private static async Task<JsonElement> ItemsAsync(NidApp app, PersonId person, string query = "?scope=household")
        => JsonDocument.Parse(await app.GetRawAsync(person, Route + query)).RootElement.GetProperty("items");

    [Fact]
    public async Task Propose_add_tax_year_waits_in_review_then_applies_on_approval()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await using var propose = await ClientAsync(app, TokenPresets.Propose);
        var pdf = await DocumentApi.UploadedAsync(app, app.AdultA, DocumentApi.Pdf);

        var proposed = await AnswerAsync(propose, "propose_add_tax_year", new { body = Notice(app, 2023, pdf) });

        Assert.Equal("proposed", proposed.GetProperty("outcome").GetString());
        Assert.Equal(0, (await ItemsAsync(app, app.AdultA)).GetArrayLength());
        Assert.Equal(0, (await PendingAsync(app, app.AdultB)).GetArrayLength());
        var pending = Assert.Single((await PendingAsync(app, app.AdultA)).EnumerateArray());
        Assert.Equal("tax.add_tax_year", pending.GetProperty("kind").GetString());

        Assert.Equal(HttpStatusCode.OK, (await ApproveAsync(app, app.AdultA, pending.GetProperty("id").GetGuid())).StatusCode);
        Assert.Equal(2023, Assert.Single((await ItemsAsync(app, app.AdultA)).EnumerateArray()).GetProperty("year").GetInt32());
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultB, HttpMethod.Get, $"/api/v1/documents/{pdf}")).StatusCode);
    }

    [Fact]
    public async Task A_proposal_dry_run_leaves_no_tax_notice_link()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await using var propose = await ClientAsync(app, TokenPresets.Propose);
        var pdf = await DocumentApi.UploadedAsync(app, app.AdultA, DocumentApi.Pdf);

        await AnswerAsync(propose, "propose_add_tax_year", new { body = Notice(app, 2023, pdf) });

        Assert.Equal(0, (await ItemsAsync(app, app.AdultA)).GetArrayLength());
        var document = JsonDocument.Parse(await app.GetRawAsync(app.AdultA, $"/api/v1/documents/{pdf}")).RootElement.GetProperty("document");
        Assert.Equal(0, document.GetProperty("links").GetArrayLength());
    }

    [Fact]
    public async Task A_refused_body_names_the_payload_field()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await using var propose = await ClientAsync(app, TokenPresets.Propose);

        var (error, text) = await CallAsync(propose, "propose_add_tax_year", new { body = Notice(app, 2026) });

        Assert.True(error);
        Assert.Contains("payload.body.year:", text);
        Assert.Equal(0, (await PendingAsync(app, app.AdultA)).GetArrayLength());
    }

    [Fact]
    public async Task Correct_and_delete_target_no_item()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await using var propose = await ClientAsync(app, TokenPresets.Propose);
        var created = JsonDocument.Parse(await (await app.RawAsync(app.AdultA, HttpMethod.Post, Route, Notice(app, 2023))).Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement;
        var id = created.GetProperty("id").GetGuid();

        var correct = await AnswerAsync(propose, "propose_correct_tax_year", new { id, body = new
        {
            year = 2023, declarants = new[] { new { personId = app.AdultA.Value, rank = 1 } }, version = created.GetProperty("version").GetUInt32(),
        } });
        var delete = await AnswerAsync(propose, "propose_delete_tax_year", new { id });

        Assert.All(new[] { correct, delete }, p => Assert.Equal(JsonValueKind.Null, p.GetProperty("proposal").GetProperty("targetItemId").ValueKind));
        Assert.Equal(HttpStatusCode.OK, (await ApproveAsync(app, app.AdultA, delete.GetProperty("proposal").GetProperty("id").GetGuid())).StatusCode);
        Assert.Equal(0, (await ItemsAsync(app, app.AdultA)).GetArrayLength());
    }

    [Fact]
    public async Task List_tax_years_reads_as_rest_does()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await using var read = await ClientAsync(app, TokenPresets.Read);
        await app.RawAsync(app.AdultA, HttpMethod.Post, Route, Notice(app, 2023));
        await app.RawAsync(app.AdultB, HttpMethod.Post, Route, new { year = 2022, declarants = new[] { new { personId = app.AdultB.Value, rank = 1 } } });

        var (error, text) = await CallAsync(read, "list_tax_years", new { scope = "me" });

        Assert.False(error, text);
        Assert.Equal(JsonDocument.Parse(await app.GetRawAsync(app.AdultA, $"{Route}?scope=me")).RootElement.ToString(), JsonDocument.Parse(text).RootElement.ToString());
        Assert.DoesNotContain("2022", JsonDocument.Parse(text).RootElement.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("year").ToString()));
    }

    [Fact]
    public async Task Proposals_about_the_other_adults_solo_notice_answer_not_found()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await using var propose = await ClientAsync(app, TokenPresets.Propose);
        var created = JsonDocument.Parse(await (await app.RawAsync(app.AdultB, HttpMethod.Post, Route,
            new { year = 2018, declarants = new[] { new { personId = app.AdultB.Value, rank = 1 } } })).Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement;
        var id = created.GetProperty("id").GetGuid();

        var correct = await CallAsync(propose, "propose_correct_tax_year", new { id, body = new
        {
            year = 2018, declarants = new[] { new { personId = app.AdultA.Value, rank = 1 } }, version = created.GetProperty("version").GetUInt32(),
        } });
        var delete = await CallAsync(propose, "propose_delete_tax_year", new { id });

        Assert.All(new[] { correct, delete }, r => { Assert.True(r.Error); Assert.Contains("Not found", r.Text); });
        Assert.Equal(0, (await PendingAsync(app, app.AdultA)).GetArrayLength());
    }
}
