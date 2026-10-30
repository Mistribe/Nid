using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Nid.TestSupport;

namespace Nid.Host.IntegrationTests.Host;

/// <summary>
/// The SPA's client is generated from the committed document (design §3), so an endpoint that declares no
/// typed response is an endpoint the browser cannot call without writing HTTP by hand.
/// </summary>
public class OpenApiTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task Every_api_endpoint_declares_a_typed_success_response()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var endpoints = app.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText is { } raw && (raw.StartsWith("/api/") || raw == "/auth/session"))
            // A browser navigation the SPA never calls (the bank's callback): left out of the document, so out of the client.
            .Where(e => e.Metadata.GetMetadata<IExcludeFromDescriptionMetadata>()?.ExcludeFromDescription != true)
            .ToList();

        Assert.NotEmpty(endpoints);
        Assert.All(endpoints, endpoint =>
        {
            var success = endpoint.Metadata.OfType<IProducesResponseTypeMetadata>()
                .Where(p => p.StatusCode is >= 200 and < 300)
                .ToList();
            Assert.True(success.Count > 0, $"{endpoint.DisplayName} declares no success response");
            // A 204 carries no body, so it needs no type; every other success status does.
            Assert.True(
                success.All(p => p.StatusCode == StatusCodes.Status204NoContent || p.Type is not null && p.Type != typeof(void)),
                $"{endpoint.DisplayName} declares no typed success response");
        });
    }

    // Both facts below read the document the build just regenerated, not the committed one: the drift gate
    // between the two is the CI `web` job's `pnpm check:api`.
    [Fact]
    public void The_generated_document_describes_amounts_ids_and_dates_as_strings()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepositoryRoot(), "web", "src", "api", "openapi.json")));
        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");

        var amount = schemas.GetProperty("MoneyView").GetProperty("properties").GetProperty("amount");
        Assert.Equal("string", amount.GetProperty("type").GetString());

        var summary = schemas.GetProperty("SubscriptionSummary").GetProperty("properties");
        Assert.Equal("uuid", summary.GetProperty("id").GetProperty("format").GetString());
        Assert.Equal("date", FirstFormat(summary.GetProperty("nextChargeOn")));

        // Web defaults read numbers from strings too, and the generator then documents every integer as
        // "integer or string": the TypeScript client would type freeDays as number | string.
        Assert.Equal(JsonValueKind.String, summary.GetProperty("freeDays").GetProperty("type").ValueKind);
        Assert.Equal("integer", summary.GetProperty("freeDays").GetProperty("type").GetString());
    }

    [Fact]
    public void The_generated_document_marks_every_non_nullable_property_required()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepositoryRoot(), "web", "src", "api", "openapi.json")));
        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");

        // Without it the generated client types every field optional (amount?: string), and every screen
        // then handles an undefined the server never sends. A nullable property stays optional.
        var money = Required(schemas.GetProperty("MoneyView"));
        Assert.Contains("amount", money);
        Assert.Contains("currency", money);

        var detail = Required(schemas.GetProperty("SubscriptionDetail"));
        Assert.Contains("id", detail);
        Assert.Contains("freeDays", detail);
        Assert.DoesNotContain("cancelledAt", detail);
    }

    private static List<string?> Required(JsonElement schema)
        => schema.TryGetProperty("required", out var required)
            ? required.EnumerateArray().Select(r => r.GetString()).ToList()
            : [];

    private static string? FirstFormat(JsonElement property)
        => property.TryGetProperty("format", out var format) ? format.GetString()
            : property.TryGetProperty("oneOf", out var oneOf) || property.TryGetProperty("anyOf", out oneOf)
                ? oneOf.EnumerateArray().Select(FirstFormatOrNull).FirstOrDefault(f => f is not null)
                : null;

    private static string? FirstFormatOrNull(JsonElement e) => e.TryGetProperty("format", out var f) ? f.GetString() : null;

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Nid.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Nid.slnx not found above the test output.");
    }
}
