using System.Text.Json;

namespace Nid.Host.Web;

/// <summary>
/// The security headers of the SPA, as the image build writes them (web/scripts/security-headers.mjs, from
/// web/src/security-headers.ts, the one source): a JSON object of header names to values.
/// </summary>
public static class SecurityHeadersFile
{
    /// <summary>A missing file is no headers (development, tests); one that is not an object of strings stops the host.</summary>
    public static IReadOnlyDictionary<string, string> Read(string path)
    {
        if (!File.Exists(path)) return new Dictionary<string, string>();
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new JsonException("the root is not an object");
            var headers = new Dictionary<string, string>();
            foreach (var property in document.RootElement.EnumerateObject())
                headers[property.Name] = property.Value.ValueKind == JsonValueKind.String
                    ? property.Value.GetString()!
                    : throw new JsonException($"the value of {property.Name} is not a string");
            return headers;
        }
        catch (JsonException e)
        {
            throw new InvalidOperationException($"{path} is not a JSON object of header names to string values: {e.Message}", e);
        }
    }
}
