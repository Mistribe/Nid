using Microsoft.Extensions.DependencyInjection;
using Nid.Host;
using Npgsql;

namespace Nid.Core.IntegrationTests;

public class DataSourceLifetimeTests
{
    /// <summary>
    /// The one data source of the process is disposed with the provider that holds it; each test host would
    /// otherwise leave a connection pool behind. The host named here does not exist: a live data source fails
    /// to reach it, only a disposed one refuses before trying.
    /// </summary>
    [Fact]
    public async Task The_data_source_is_disposed_with_the_container()
    {
        var provider = new ServiceCollection().AddNid("Host=nid-nowhere.invalid;Database=none;Timeout=1").BuildServiceProvider();
        var dataSource = provider.GetRequiredService<NpgsqlDataSource>();

        await provider.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await dataSource.OpenConnectionAsync(TestContext.Current.CancellationToken));
    }
}
