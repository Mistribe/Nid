using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using Nid.Host.Api;
using Nid.Host.Auth;

namespace Nid.Host.Mcp;

/// <summary>
/// #309: Nid's Claude Desktop extension. The image builds <c>mcpb/</c> (the manifest template, the bridge and its vendored
/// <c>mcp-remote</c>); this zips it with the manifest filled in for this instance: its version, its <c>/mcp</c> as the
/// default address, the instance as the privacy policy. No token is ever in the file: Claude Desktop asks for it.
/// </summary>
public static class McpBundle
{
    public const string Path = "/api/v1/mcp/nid.mcpb";
    public const string RootKey = "Mcp:BundleRoot";
    public const string ContentType = "application/zip";

    public static IEndpointRouteBuilder MapMcpBundle(this IEndpointRouteBuilder routes)
    {
        routes.MapGet(Path, (HttpRequest request, IConfiguration configuration, IWebHostEnvironment environment) =>
            {
                // An adult signed in to the browser; a token is for the assistant, not for fetching its own setup.
                if (SessionClaims.ReadPersonId(request.HttpContext.User) is null)
                    return Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "A signed-in adult downloads the extension.");
                var root = configuration[RootKey] ?? System.IO.Path.Combine(environment.ContentRootPath, "mcpb");
                // A development checkout without `npm ci` in mcpb/: nothing to hand out.
                if (!File.Exists(System.IO.Path.Combine(root, "manifest.json")) || !Directory.Exists(System.IO.Path.Combine(root, "node_modules")))
                    return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "The Claude Desktop extension is not built on this server.");

                var origin = (configuration["Auth:PublicUrl"] is { Length: > 0 } url ? url : $"{request.Scheme}://{request.Host}").TrimEnd('/');
                // ponytail: zipped on each download (a few MB, a click from the Sécurité page now and then); cache it if that changes.
                return Results.File(Build(root, origin, PlatformEndpoints.Version), ContentType, "nid.mcpb");
            })
            .RequireAuthorization()
            .Produces(StatusCodes.Status200OK, typeof(Stream), ContentType)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);
        return routes;
    }

    /// <summary>The bundle: <paramref name="root"/>'s files, its manifest filled in, without the build-time ones.</summary>
    public static byte[] Build(string root, string origin, string version)
    {
        var manifest = JsonNode.Parse(File.ReadAllText(System.IO.Path.Combine(root, "manifest.json")))!.AsObject();
        manifest["version"] = version;
        manifest["privacy_policies"] = new JsonArray($"{origin}/");
        manifest["user_config"]!["url"]!["default"] = $"{origin}{McpSetup.Path}";

        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var entry = zip.CreateEntry("manifest.json").Open())
                JsonSerializer.Serialize(entry, manifest, BundleJson.Default.JsonObject);
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                var name = System.IO.Path.GetRelativePath(root, file).Replace('\\', '/');
                if (Shipped(name))
                    zip.CreateEntryFromFile(file, name, CompressionLevel.Optimal);
            }
        }
        return buffer.ToArray();
    }

    /// <summary>The bridge, its package and its dependencies; never the template, the build's check, nor npm's links.</summary>
    private static bool Shipped(string name) =>
        name is "package.json" || name.StartsWith("server/", StringComparison.Ordinal)
        || (name.StartsWith("node_modules/", StringComparison.Ordinal) && !name.StartsWith("node_modules/.", StringComparison.Ordinal));
}

[System.Text.Json.Serialization.JsonSerializable(typeof(JsonObject))]
internal sealed partial class BundleJson : System.Text.Json.Serialization.JsonSerializerContext;
