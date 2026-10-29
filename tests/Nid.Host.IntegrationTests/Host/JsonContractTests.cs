using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Nid.TestSupport;
using JsonOptions = Microsoft.AspNetCore.Http.Json.JsonOptions;

namespace Nid.Host.IntegrationTests.Host;

/// <summary>
/// Every body an endpoint reads and every type it declares it answers (<c>Returns&lt;T&gt;</c>) has source-generated
/// JSON metadata (Api/Json): what a NativeAOT build needs, checked for the endpoints no test calls too. NidApp's host
/// has no reflection fallback, so a type missing here would fail.
/// </summary>
public class JsonContractTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task Every_endpoint_type_has_source_generated_metadata()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var options = new System.Text.Json.JsonSerializerOptions(app.Services.GetRequiredService<IOptions<JsonOptions>>().Value.SerializerOptions);
        for (var i = options.TypeInfoResolverChain.Count - 1; i >= 0; i--)
            if (options.TypeInfoResolverChain[i] is System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver)
                options.TypeInfoResolverChain.RemoveAt(i);

        // /mcp answers through the SDK's own options; an upload body (a stream, a multipart form) is not JSON.
        var types = app.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .Where(e => e is not RouteEndpoint { RoutePattern.RawText: var route } || !route!.StartsWith("/mcp", StringComparison.Ordinal))
            .SelectMany(e => e.Metadata.OfType<IProducesResponseTypeMetadata>().Select(m => m.Type)
                .Concat(e.Metadata.OfType<IAcceptsMetadata>().Where(m => !m.ContentTypes.Contains("multipart/form-data")).Select(m => m.RequestType)))
            .OfType<Type>()
            .Where(t => t != typeof(void) && t != typeof(Stream))
            .Distinct()
            .ToList();
        var missing = types.Where(t => !Resolves(t)).Select(t => t.FullName).Order().ToList();

        Assert.NotEmpty(types);
        Assert.Empty(missing);

        bool Resolves(Type type)
        {
            try
            {
                options.GetTypeInfo(type);
                return true;
            }
            catch (NotSupportedException)
            {
                return false;
            }
        }
    }
}
