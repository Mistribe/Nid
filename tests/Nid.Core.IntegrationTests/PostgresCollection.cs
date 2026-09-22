using Nid.TestSupport;

namespace Nid.Core.IntegrationTests;

/// <summary>
/// xUnit collection definitions must live in the same assembly as the test classes that use them, even
/// though the fixture type itself is shared from <see cref="Nid.TestSupport"/>.
/// </summary>
[CollectionDefinition("postgres")]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>;
