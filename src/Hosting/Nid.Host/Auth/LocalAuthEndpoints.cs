using Microsoft.Extensions.DependencyInjection.Extensions;
using Nid.Host.Api;

namespace Nid.Host.Auth;

/// <summary>
/// Issue 215, local-accounts.md §14.2: the routes without a session. Mapped only when local accounts are on, never in demo
/// mode. Each is behind the login CSRF check (<see cref="LoginCsrf"/>); their bodies are never logged.
/// </summary>
public static class LocalAuthEndpoints
{
    /// <summary>Where a password link leads: the web app's page reads the secret from the fragment.</summary>
    public const string LinkPagePath = "/password";

    public static IServiceCollection AddLocalAccounts(this IServiceCollection services)
    {
        services.TryAddSingleton<LocalPasswordHasher>();
        services.TryAddSingleton(sp => new PasswordRules(sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<NidAuthOptions>>().Value.Local));
        services.TryAddSingleton<LoginFailureLog>();
        services.TryAddSingleton<SetupToken>();
        services.TryAddScoped<LocalSignIn>();
        return services;
    }

    /// <summary>The address a password link opens, from its secret.</summary>
    public static string LinkUrl(string publicUrl, string token) => $"{publicUrl.TrimEnd('/')}{LinkPagePath}#{token}";

    public static IEndpointRouteBuilder MapLocalAuth(this IEndpointRouteBuilder routes)
    {
        routes.MapPost("/auth/login/password", (HttpContext http, PasswordLoginBody body, LocalSignIn signIn) => signIn.PasswordAsync(http, body))
            .Produces<PasswordLoginResult>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status400BadRequest);

        routes.MapPost("/auth/setup", (HttpContext http, LocalSetupBody body, LocalSignIn signIn) => signIn.SetupAsync(http, body))
            .Produces<LocalLoginView>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);

        routes.MapPost("/auth/link/inspect", (HttpContext http, PasswordLinkTokenBody body, LocalSignIn signIn) => signIn.InspectLinkAsync(http, body))
            .Produces<PasswordLinkInfoView>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        routes.MapPost("/auth/link/complete", (HttpContext http, CompletePasswordLinkBody body, LocalSignIn signIn) => signIn.CompleteLinkAsync(http, body))
            .Produces<LocalLoginView>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        return routes;
    }
}
