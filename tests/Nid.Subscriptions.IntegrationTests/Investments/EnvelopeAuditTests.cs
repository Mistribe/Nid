using System.Text.Json;
using Nid.Core.Contracts;
using Nid.TestSupport;

namespace Nid.Subscriptions.IntegrationTests.Investments;

/// <summary>2B1a spec §8, Important 2 of the final review: the audit of an envelope change.</summary>
[Collection("postgres")]
public class EnvelopeAuditTests(PostgresFixture fixture)
{
    private static JsonElement Json(string body) => JsonDocument.Parse(body).RootElement;

    [Fact]
    public async Task Editing_the_note_then_closing_shows_both_in_the_items_history()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await app.NewEnvelopeAsync("Livret", app.AdultA, Visibility.Shared);
        var url = $"/api/v1/investments/envelopes/{id.Value}";
        (await app.RawAsync(app.AdultA, HttpMethod.Patch, url, new { note = "a note" })).EnsureSuccessStatusCode();
        (await app.RawAsync(app.AdultA, HttpMethod.Post, $"{url}/close", new { closedOn = "2025-09-30" })).EnsureSuccessStatusCode();

        var page = Json(await app.GetRawAsync(app.AdultA, $"/api/v1/platform/audit/items/{id.Value}"));
        var entries = page.GetProperty("entries").EnumerateArray().ToList();

        var envelopeUpdate = Assert.Single(entries, e => e.GetProperty("entity").GetString() == "investments.envelope" && e.GetProperty("action").GetString() == "update");
        Assert.Equal("a note", envelopeUpdate.GetProperty("after").GetProperty("note").GetString());

        var itemUpdate = Assert.Single(entries, e => e.GetProperty("entity").GetString() == "core.item" && e.GetProperty("action").GetString() == "update");
        Assert.NotEqual(JsonValueKind.Null, itemUpdate.GetProperty("after").GetProperty("archived_at").ValueKind);
    }
}
