using System.Net.Http.Json;
using System.Text.Json;
using Nid.Core.Contracts;

namespace Nid.Subscriptions.IntegrationTests.Export;

public sealed record SectionCountResponse(string Name, int Count);
public sealed record ManifestResponse(string Format, int Version, DateTimeOffset ExportedAt, string Revision, List<SectionCountResponse> Sections);
public sealed record PageResponse(string Section, List<JsonElement> Entities, string? NextCursor);

/// <summary>Drives the export through the real HTTP API, the way the browser will (import-format.md §10).</summary>
public sealed class ExportClient(NidApp app)
{
    public const string Root = "/api/v1/platform/export";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public Task<ManifestResponse> ManifestAsync(PersonId reader) => app.GetAsync<ManifestResponse>(reader, $"{Root}/manifest");

    public Task<HttpResponseMessage> PageRawAsync(PersonId reader, string section, string? cursor = null, int? limit = null)
    {
        var query = new List<string>();
        if (cursor is not null) query.Add("cursor=" + Uri.EscapeDataString(cursor));
        if (limit is not null) query.Add("limit=" + limit);
        return app.RawAsync(reader, HttpMethod.Get, $"{Root}/{section}" + (query.Count > 0 ? "?" + string.Join("&", query) : ""));
    }

    public async Task<PageResponse> PageAsync(PersonId reader, string section, string? cursor = null, int? limit = null)
    {
        var response = await PageRawAsync(reader, section, cursor, limit);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<PageResponse>(Json))!;
    }

    /// <summary>Every section, every page, in manifest order.</summary>
    public async Task<Dictionary<string, List<JsonElement>>> ExportAllAsync(PersonId reader, int? limit = null)
    {
        var manifest = await ManifestAsync(reader);
        var all = new Dictionary<string, List<JsonElement>>();
        foreach (var section in manifest.Sections)
        {
            var entities = new List<JsonElement>();
            string? cursor = null;
            do
            {
                var page = await PageAsync(reader, section.Name, cursor, limit);
                entities.AddRange(page.Entities);
                cursor = page.NextCursor;
            } while (cursor is not null);
            Assert.Equal(section.Count, entities.Count);
            all[section.Name] = entities;
        }
        return all;
    }
}
