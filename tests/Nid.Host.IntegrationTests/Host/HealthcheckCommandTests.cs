using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Nid.Host.Web;

namespace Nid.Host.IntegrationTests.Host;

/// <summary>Single-image design §2: `nid healthcheck`, the container's HEALTHCHECK (the chiseled image has no shell or wget).</summary>
public class HealthcheckCommandTests
{
    [Fact]
    public void The_default_url_is_readiness_on_port_8080() =>
        Assert.Equal("http://127.0.0.1:8080/health/ready", HealthcheckCommand.DefaultUrl(null).ToString());

    [Fact]
    public void The_default_url_follows_the_first_http_port() =>
        Assert.Equal("http://127.0.0.1:9090/health/ready", HealthcheckCommand.DefaultUrl("9090;9091").ToString());

    [Theory]
    [InlineData(200, 0)]
    [InlineData(503, 1)]
    public async Task The_exit_code_follows_the_readiness_status(int status, int expectedExit)
    {
        await using var server = await StartAsync(status);

        var exit = await HealthcheckCommand.RunAsync(new Uri(server.Url + "health/ready"), TimeSpan.FromSeconds(5));

        Assert.Equal(expectedExit, exit);
    }

    [Fact]
    public async Task Nothing_listening_is_a_failure()
    {
        string url;
        await using (var server = await StartAsync(200)) url = server.Url;

        var exit = await HealthcheckCommand.RunAsync(new Uri(url + "health/ready"), TimeSpan.FromSeconds(2));

        Assert.Equal(1, exit);
    }

    private static async Task<Server> StartAsync(int status)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        app.MapGet("/health/ready", () => Results.StatusCode(status));
        await app.StartAsync();
        var url = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return new Server(app, url.TrimEnd('/') + "/");
    }

    private sealed class Server(WebApplication app, string url) : IAsyncDisposable
    {
        public string Url { get; } = url;
        public async ValueTask DisposeAsync() => await app.DisposeAsync();
    }
}
