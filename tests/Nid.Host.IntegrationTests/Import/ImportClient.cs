using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Nid.Core.Contracts;
using Nid.Platform.Canonical;
using Nid.TestSupport;

namespace Nid.Host.IntegrationTests.Import;

public sealed record IssueResponse(string Severity, string Code, string Message, string Path);
public sealed record CountsResponse(int Created, int Unchanged, int Conflict, int Replaced = 0);
public sealed record ReportResponse(string State, Dictionary<string, CountsResponse> Sections, List<IssueResponse> Issues);
public sealed record AuditResponse(DateTimeOffset CommittedAt, string ContentHash, string Format, int Version, bool Replace);
public sealed record SessionResponse(Guid Id, string State, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, ReportResponse? Report, AuditResponse? Audit = null);
public sealed record BatchResponse(string Section, int Index, int Count, bool Accepted, List<IssueResponse> Issues);

/// <summary>Drives the import protocol through the real HTTP API, the way the browser and scripts do (§11.8).</summary>
public sealed class ImportClient(NidApp app)
{
    public const string Root = "/api/v1/platform/imports";

    /// <summary>The commit order the runner applies sections in (§11.4: Order, then module, then each module's sections):
    /// <c>persons</c>, the subscriptions module's <c>providers</c>, <c>subscriptions</c>, <c>billing_phases</c>,
    /// <c>purchases</c>, then <c>envelopes</c>, then the income module's <c>income_sources</c> and <c>income_phases</c>,
    /// then the real estate module's sections, then the Core's <c>valuations</c> and <c>flows</c>, then the income module's <c>income_one_off_taxable</c>, then the envelopes' holdings, then the goals, then the bank accounts and their transactions.</summary>
    private static readonly IReadOnlyList<string> CommitOrder =
        ["persons", "providers", "subscriptions", "billing_phases", "purchases", "envelopes", "income_sources", "income_phases",
         "properties", "property_recurring_costs", "tenancies", "loans", "loan_phases", "loan_insurances", "loan_fees", "loan_events", "loan_bank_schedule", "subscription_insurances", "valuations", "flows", "income_one_off_taxable",
         "holdings", "holding_snapshots", "holding_movements", "security_breakdowns", "goals", "bank_accounts", "bank_transactions", "bank_links", "bank_rules"];

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Every batch hash this client has sent, by session then section then batch index.</summary>
    private readonly Dictionary<Guid, Dictionary<string, SortedDictionary<int, string>>> sentHashes = [];

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
        var sent = hash ?? Rfc8785.Hash(entities.RootElement);
        var body = $$"""{"hash":"{{sent}}","entities":{{entitiesJson}}}""";
        if (!sentHashes.TryGetValue(session, out var bySection))
            sentHashes[session] = bySection = new Dictionary<string, SortedDictionary<int, string>>(StringComparer.Ordinal);
        if (!bySection.TryGetValue(section, out var byIndex))
            bySection[section] = byIndex = [];
        byIndex[index] = sent;
        return await app.RawTextAsync(actor, HttpMethod.Put, $"{Root}/{session}/batches/{section}/{index}", body);
    }

    public async Task<BatchResponse> PutAsync(PersonId actor, Guid session, string section, int index, string entitiesJson)
    {
        var response = await PutRawAsync(actor, session, section, index, entitiesJson);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<BatchResponse>(Json))!;
    }

    public async Task<HttpResponseMessage> ValidateRawAsync(PersonId actor, Guid session, bool replace = false)
        => await app.RawAsync(actor, HttpMethod.Post, $"{Root}/{session}/validate", replace ? new { replace = true } : null);

    public async Task<SessionResponse> ValidateAsync(PersonId actor, Guid session, bool replace = false)
    {
        var response = await ValidateRawAsync(actor, session, replace);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<SessionResponse>(Json))!;
    }

    public async Task<HttpResponseMessage> CommitRawAsync(PersonId actor, Guid session, bool replace = false)
        => await app.RawAsync(actor, HttpMethod.Post, $"{Root}/{session}/commit", replace ? new { replace = true } : null);

    public async Task<HttpResponseMessage> CancelRawAsync(PersonId actor, Guid session)
        => await app.RawAsync(actor, HttpMethod.Delete, $"{Root}/{session}");

    public async Task<SessionResponse> GetAsync(PersonId actor, Guid session)
        => await app.GetAsync<SessionResponse>(actor, $"{Root}/{session}");

    /// <summary>
    /// The hashes this client sent for <paramref name="session"/>, in the commit order the runner applies sections
    /// (§11.4): the sections that were actually sent, batch index ascending within each.
    /// </summary>
    public Task<IReadOnlyList<string>> BatchHashesInCommitOrderAsync(PersonId actor, Guid session)
    {
        var bySection = sentHashes.TryGetValue(session, out var found) ? found : [];
        var order = CommitOrder.Select((name, index) => (name, index)).ToDictionary(p => p.name, p => p.index, StringComparer.Ordinal);
        IReadOnlyList<string> hashes = bySection
            .OrderBy(p => order.GetValueOrDefault(p.Key, int.MaxValue))
            .SelectMany(p => p.Value.OrderBy(e => e.Key).Select(e => e.Value))
            .ToList();
        return Task.FromResult(hashes);
    }

    /// <summary>Starts a session, sends each section as batch 0, and validates. Each batch must be accepted.</summary>
    public async Task<SessionResponse> RunAsync(PersonId actor, object adults, IReadOnlyDictionary<string, string> sections)
    {
        var session = await StartAsync(actor, adults);
        foreach (var (section, entities) in sections)
        {
            var batch = await PutAsync(actor, session.Id, section, 0, entities);
            Assert.True(batch.Accepted, string.Join("; ", batch.Issues.Select(i => $"{i.Code} at {i.Path}: {i.Message}")));
        }
        return await ValidateAsync(actor, session.Id);
    }

    public static async Task<string?> CodeOf(HttpResponseMessage response)
        => (await response.Content.ReadFromJsonAsync<JsonElement>(Json)).TryGetProperty("code", out var code) ? code.GetString() : null;
}
