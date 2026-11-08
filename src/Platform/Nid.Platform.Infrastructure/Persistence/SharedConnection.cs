using Npgsql;

namespace Nid.Platform.Infrastructure.Persistence;

/// <summary>
/// The one connection every <c>DbContext</c> of a request uses, so a unit of work can put them all in one
/// transaction. EF opens and closes it as it needs; the scope disposes it.
/// </summary>
public sealed class SharedConnection(NpgsqlDataSource dataSource) : IAsyncDisposable
{
    public NpgsqlConnection Connection { get; } = dataSource.CreateConnection();

    public ValueTask DisposeAsync() => Connection.DisposeAsync();
}
