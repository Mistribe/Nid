using System.Net;
using Nid.Core.Contracts;
using Nid.TestSupport;

namespace Nid.Host.IntegrationTests.Mcp;

/// <summary>#533: an adult turns the assistant on for themselves before asking.</summary>
internal static class AssistantMe
{
    public static async Task EnableAsync(NidApp app, PersonId person)
        => Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(person, HttpMethod.Put, "/api/v1/ai/assistant/me/enabled", new { enabled = true })).StatusCode);

    public static async Task OffsiteAsync(NidApp app, PersonId person, string preference)
        => Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(person, HttpMethod.Put, "/api/v1/ai/assistant/me/offsite", new { preference })).StatusCode);
}
