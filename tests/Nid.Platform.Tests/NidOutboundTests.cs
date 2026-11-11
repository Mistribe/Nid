using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using UnambitiousFx.Myelin;

namespace Nid.Platform.Tests;

/// <summary>Myelin's own suite covers the client; this pins what Nid sets on top of it.</summary>
public class NidOutboundTests
{
    [Fact]
    public void The_registered_client_keeps_Nid_s_hosts_user_agent_timeout_and_redirect_limit()
    {
        using var provider = new ServiceCollection().AddLogging().AddMyelin(NidOutbound.Apply).BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<MyelinOptions>>().Value;

        Assert.Equal(
            ["raw.githubusercontent.com", "www.ecb.europa.eu", "1000logos.net", "upload.wikimedia.org", "www.nounou-top.fr", "www.boursorama.com", "query2.finance.yahoo.com", "bdm.insee.fr", "discord.com", "discordapp.com", "api.enablebanking.com"],
            options.AllowedHosts);
        Assert.Equal("Nid/1.0 (+https://github.com/Mistribe/Nid)", options.UserAgent);
        Assert.Equal(TimeSpan.FromSeconds(30), options.Timeout);
        Assert.Equal(3, options.MaxRedirects);
        Assert.NotNull(provider.GetRequiredService<SafeHttpClient>());
    }

    [Fact]
    public void Options_is_a_copy_so_a_test_cannot_change_the_defaults()
    {
        NidOutbound.Options().AllowedHosts[0] = "changed.example";
        Assert.Equal("raw.githubusercontent.com", NidOutbound.Options().AllowedHosts[0]);
    }
}
