namespace Nid.Banking.Tests.Files;

internal static class Vectors
{
    public static byte[] Read(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "vectors", "bankfiles", name));
}
