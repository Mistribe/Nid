using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Nid.Core.Contracts;

namespace Nid.TestSupport;

/// <summary>« Mes jetons » (spec 4A §6): creates the personal access tokens the MCP and bearer tests sign in with.</summary>
public static class TokenApi
{
    public const string Tokens = "/api/v1/core/me/tokens";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public sealed record Token(Guid Id, string Name, string Preset, string Prefix, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt,
        DateTimeOffset? LastUsedAt, DateTimeOffset? RevokedAt, string Status);

    public sealed record Created(Token Token, string Secret);

    public static async Task<Created> CreateAsync(NidApp app, PersonId person, string preset = TokenPresets.Propose, int? days = null)
    {
        var response = await app.RawAsync(person, HttpMethod.Post, Tokens, new { name = "Claude", preset, expiresInDays = days });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<Created>(Json))!;
    }
}
