using System.Text.Json;
using System.Text.Json.Serialization;
using Nid.Core.Contracts;
using NodaTime;
using NodaTime.Text;

namespace Nid.Host.Api;

/// <summary>
/// The read views (<c>Nid.Subscriptions.Application.Views</c>) carry <see cref="ItemId"/> and
/// <see cref="LocalDate"/> as-is — System.Text.Json has no built-in mapping for either, and without these
/// converters an <see cref="ItemId"/> serializes as <c>{"value":"..."}</c> instead of a plain GUID string,
/// and a <see cref="LocalDate"/> fails to serialize at all. Registered once, for every response.
/// </summary>
public sealed class ItemIdJsonConverter : JsonConverter<ItemId>
{
    public override ItemId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => new(reader.GetGuid());

    public override void Write(Utf8JsonWriter writer, ItemId value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.Value);
}

public sealed class LocalDateJsonConverter : JsonConverter<LocalDate>
{
    public override LocalDate Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => LocalDatePattern.Iso.Parse(reader.GetString() ?? "").Value;

    public override void Write(Utf8JsonWriter writer, LocalDate value, JsonSerializerOptions options)
        => writer.WriteStringValue(LocalDatePattern.Iso.Format(value));
}
