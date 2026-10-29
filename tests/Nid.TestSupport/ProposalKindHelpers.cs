using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Nid.TestSupport;

namespace Nid.TestSupport;

/// <summary>The REST helpers the proposal-kind tests (statement, payslip, invoice) share: submit, read, approve, edit.</summary>
public static class ProposalKindHelpers
{
    public const string Proposals = "/api/v1/core/proposals";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task<JsonElement> JsonOf(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>(Json);

    /// <summary>Submits as adult A and expects 201.</summary>
    public static async Task<JsonElement> SubmitAsync(NidApp app, object body)
    {
        var response = await app.RawAsync(app.AdultA, HttpMethod.Post, ProposalKindHelpers.Proposals, body);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return await JsonOf(response);
    }

    /// <summary>The proposal's detail (`proposal`, `preview`, `canDecide`).</summary>
    public static async Task<JsonElement> ReadAsync(NidApp app, JsonElement proposal)
        => await JsonOf(await app.RawAsync(app.AdultA, HttpMethod.Get, $"{ProposalKindHelpers.Proposals}/{proposal.GetProperty("id").GetGuid()}"));

    public static Task<HttpResponseMessage> ApproveAsync(NidApp app, JsonElement proposal, JsonNode? edited = null)
        => app.RawAsync(app.AdultA, HttpMethod.Post, $"{ProposalKindHelpers.Proposals}/{proposal.GetProperty("id").GetGuid()}/approve",
            edited is null
                ? new { version = proposal.GetProperty("version").GetUInt32() }
                : (object)new { version = proposal.GetProperty("version").GetUInt32(), editedPayload = edited });

    /// <summary>The proposal's payload with <paramref name="excluded"/>: what « À valider » sends after unchecking movements.</summary>
    public static JsonNode Edited(JsonElement proposal, params int[] excluded)
    {
        var payload = JsonNode.Parse(proposal.GetProperty("payload").GetRawText())!;
        payload["excluded"] = new JsonArray(excluded.Select(i => (JsonNode?)i).ToArray());
        return payload;
    }

    public static async Task<JsonElement> EnvelopeAsync(NidApp app, Guid id)
        => JsonDocument.Parse(await app.GetRawAsync(app.AdultA, $"/api/v1/investments/envelopes/{id}")).RootElement;

    public static async Task RecordFlowAsync(NidApp app, Guid envelope, object flow)
        => (await app.RawAsync(app.AdultA, HttpMethod.Post, $"/api/v1/investments/envelopes/{envelope}/flows", flow)).EnsureSuccessStatusCode();
}
