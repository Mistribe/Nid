using System.Text.Json.Serialization;

namespace Nid.Core.Contracts;

// Records of import-format.md shared by every module's section.

public sealed record MoneyRecord
{
    /// <summary>A string or a JSON number, read as decimal either way — never as a double (§2); written as a string by the export.</summary>
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.WriteAsString)]
    public required decimal Amount { get; init; }

    public required string Currency { get; init; }
}

public sealed record ShareRecord
{
    public required string Person { get; init; }
    public required int ShareBp { get; init; }
}
