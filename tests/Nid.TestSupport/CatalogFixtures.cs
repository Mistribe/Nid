namespace Nid.TestSupport;

/// <summary>
/// The two files of <c>Mistribe/SubTracker-Data</c> at the pinned commit, fetched once into <c>tests/fixtures/catalog</c>
/// (1C2b Task 2) and copied next to the test assembly that reads them.
/// </summary>
public static class CatalogFixtures
{
    public const string PinnedCommit = "df0146a8f3532f5c790aa54a34d4871e96b62a95";

    public static byte[] RealLabels() => File.ReadAllBytes(PathOf("labels.json"));

    public static byte[] RealProviders() => File.ReadAllBytes(PathOf("providers.json"));

    private static string PathOf(string name) => Path.Combine(AppContext.BaseDirectory, "fixtures", "catalog", name);
}
