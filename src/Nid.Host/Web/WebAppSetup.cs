using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.FileProviders;

namespace Nid.Host.Web;

/// <summary>
/// The built SPA the host serves (single-image design §3). <see cref="Load"/> returns null when there is no
/// <c>index.html</c> to serve (development, tests), and then nothing is mounted.
/// </summary>
public sealed record WebAppOptions(string Root, IReadOnlyDictionary<string, string> Headers)
{
    public IFileProvider Files { get; } = new PhysicalFileProvider(Root);

    /// <summary><c>Web:Root</c> (default <c>wwwroot</c>) and <c>Web:SecurityHeaders</c> (default <c>security-headers.json</c>), relative to the content root.</summary>
    public static WebAppOptions? Load(IConfiguration configuration, string contentRoot)
    {
        var root = Path.GetFullPath(configuration["Web:Root"] ?? "wwwroot", contentRoot);
        if (!File.Exists(Path.Combine(root, "index.html"))) return null;
        var headersFile = Path.GetFullPath(configuration["Web:SecurityHeaders"] ?? "security-headers.json", contentRoot);
        return new WebAppOptions(root, SecurityHeadersFile.Read(headersFile));
    }
}

public static class WebAppSetup
{
    private const string Immutable = "public, max-age=31536000, immutable";
    private const string Revalidate = "no-cache";

    /// <summary>What belongs to the server, never to the SPA: the SPA does not answer for it (a missing asset is a 404).</summary>
    private static readonly string[] ServerSegments = ["api", "auth", "mcp", "health", "assets"];

    /// <summary>
    /// Compressed except what carries a secret (BREACH, #84): the CSRF token (<c>/auth/session</c>) and a new token's
    /// secret (the token routes). <c>/mcp</c> serves agents, not page loads. The routes are under <c>/api/v1/core</c>;
    /// <c>/api/v1/me/tokens</c> is the path the documents and nid-web's Caddyfile named, kept excluded too.
    /// </summary>
    private static readonly string[] SecretPaths = ["/api/v1/me/tokens", "/api/v1/core/me/tokens"];

    public static IServiceCollection AddNidWebApp(this IServiceCollection services, WebAppOptions web)
    {
        services.AddSingleton(web);
        // EnableForHttps: behind the hoster's proxy the request is HTTPS once X-Forwarded-Proto is honoured, and the
        // default would then never compress. BREACH is handled by IsCompressible.
        services.AddResponseCompression(o =>
        {
            o.EnableForHttps = true;
            o.Providers.Add<BrotliCompressionProvider>();
            o.Providers.Add<GzipCompressionProvider>();
        });
        return services;
    }

    /// <summary>The SPA, its assets and the API, but neither <c>/auth</c>, <c>/mcp</c>, <c>/health</c> nor the token routes.</summary>
    public static bool IsCompressible(PathString path)
    {
        if (path.StartsWithSegments("/auth", StringComparison.OrdinalIgnoreCase)
            || path.StartsWithSegments("/mcp", StringComparison.OrdinalIgnoreCase)
            || path.StartsWithSegments("/health", StringComparison.OrdinalIgnoreCase)) return false;
        foreach (var secret in SecretPaths)
            if (path.StartsWithSegments(secret, StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    /// <summary>True when the first segment of the path is one of the server's, whole and whatever its case (<c>/apiary</c> is not).</summary>
    public static bool IsServerPath(PathString path)
    {
        var rest = (path.Value ?? "").AsSpan().TrimStart('/');
        var end = rest.IndexOf('/');
        var first = end < 0 ? rest : rest[..end];
        foreach (var segment in ServerSegments)
            if (first.Equals(segment, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>
    /// The files of the web root (hashed assets never change, everything else is revalidated), then the SPA for what routing
    /// found no endpoint for: a GET or HEAD outside the server's own paths is a client route, answered with <c>index.html</c>.
    /// Routing has already run, so an endpoint that exists keeps its own answers, a wrong method on it the 405 (the stateless MCP
    /// endpoint relies on it) and an unknown server path the 404.
    /// </summary>
    public static WebApplication UseNidWebApp(this WebApplication app, WebAppOptions web)
    {
        app.UseWhen(context => IsCompressible(context.Request.Path), branch => branch.UseResponseCompression());
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = web.Files,
            OnPrepareResponse = context => Decorate(
                context.Context.Response, web,
                context.Context.Request.Path.StartsWithSegments("/assets", StringComparison.OrdinalIgnoreCase) ? Immutable : Revalidate),
        });
        app.Use(async (context, next) =>
        {
            var head = HttpMethods.IsHead(context.Request.Method);
            if (context.GetEndpoint() is not null || !(head || HttpMethods.IsGet(context.Request.Method)) || IsServerPath(context.Request.Path))
            {
                await next(context);
                return;
            }

            var index = web.Files.GetFileInfo("index.html");
            Decorate(context.Response, web, Revalidate);
            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.ContentLength = index.Length;
            if (!head) await context.Response.SendFileAsync(index);
        });
        return app;
    }

    private static void Decorate(HttpResponse response, WebAppOptions web, string cacheControl)
    {
        response.Headers.CacheControl = cacheControl;
        foreach (var (name, value) in web.Headers) response.Headers[name] = value;
    }
}
