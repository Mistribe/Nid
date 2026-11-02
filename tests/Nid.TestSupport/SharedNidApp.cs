namespace Nid.TestSupport;

/// <summary>
/// One host for a whole test class, for the theories whose every case is refused and writes nothing: starting a host
/// per case cost more than the case itself. A case that writes, or reads totals another case could change, starts its
/// own host with <see cref="NidApp.StartAsync"/>.
/// </summary>
public sealed class SharedNidApp : IAsyncLifetime
{
    private readonly PostgresFixture postgres = new();

    public NidApp App { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        await postgres.InitializeAsync();
        App = await NidApp.StartAsync(postgres);
    }

    public async ValueTask DisposeAsync() => await App.DisposeAsync();
}
