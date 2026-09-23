using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Nid.Core.Contracts;
using Nid.Platform.Canonical;

namespace Nid.Subscriptions.IntegrationTests.Import;

public sealed record IssueResponse(string Severity, string Code, string Message, string Path);
public sealed record CountsResponse(int Created, int Unchanged, int Conflict);
public sealed record ReportResponse(string State, Dictionary<string, CountsResponse> Sections, List<IssueResponse> Issues);
public sealed record SessionResponse(Guid Id, string State, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, ReportResponse? Report);
public sealed record BatchResponse(string Section, int Index, int Count, bool Accepted, List<IssueResponse> Issues);

/// <summary>Drives the import protocol through the real HTTP API, the way the browser and scripts do (§11.8).</summary>
public sealed class ImportClient(NidApp app)
{
    public const string Root = "/api/v1/platform/imports";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<HttpResponseMessage> StartRawAsync(PersonId actor, object? adults = null, string format = "nid.data", int version = 1)
        => await app.RawAsync(actor, HttpMethod.Post, Root, new { format, version, adults = adults ?? new { } });

    public async Task<SessionResponse> StartAsync(PersonId actor, object? adults = null)
    {
        var response = await StartRawAsync(actor, adults);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<SessionResponse>(Json))!;
    }

    /// <summary>Sends a batch with the hash a correct client computes; <paramref name="hash"/> overrides it.</summary>
    public async Task<HttpResponseMessage> PutRawAsync(PersonId actor, Guid session, string section, int index, string entitiesJson, string? hash = null)
    {
        using var entities = JsonDocument.Parse(entitiesJson);
        var body = $$"""{"hash":"{{hash ?? Rfc8785.Hash(entities.RootElement)}}","entities":{{entitiesJson}}}""";
        return await app.RawTextAsync(actor, HttpMethod.Put, $"{Root}/{session}/batches/{section}/{index}", body);
    }

    public async Task<BatchResponse> PutAsync(PersonId actor, Guid session, string section, int index, string entitiesJson)
    {
        var response = await PutRawAsync(actor, session, section, index, entitiesJson);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<BatchResponse>(Json))!;
    }

    public async Task<HttpResponseMessage> ValidateRawAsync(PersonId actor, Guid session)
        => await app.RawAsync(actor, HttpMethod.Post, $"{Root}/{session}/validate");

    public async Task<SessionResponse> ValidateAsync(PersonId actor, Guid session)
    {
        var response = await ValidateRawAsync(actor, session);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<SessionResponse>(Json))!;
    }

    public async Task<HttpResponseMessage> CommitRawAsync(PersonId actor, Guid session)
        => await app.RawAsync(actor, HttpMethod.Post, $"{Root}/{session}/commit");

    public async Task<SessionResponse> GetAsync(PersonId actor, Guid session)
        => await app.GetAsync<SessionResponse>(actor, $"{Root}/{session}");

    public static async Task<string?> CodeOf(HttpResponseMessage response)
        => (await response.Content.ReadFromJsonAsync<JsonElement>(Json)).TryGetProperty("code", out var code) ? code.GetString() : null;
}
