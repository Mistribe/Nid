using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
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

/// <summary>
/// Issue #45: a list that also reads a lone object as a list of one, so a switch body written before multi-leg switches (one
/// <c>from</c>, one <c>to</c>) still reads — a proposal stored then replays the same.
/// </summary>
public sealed class OneOrManyJsonConverter<T> : JsonConverter<IReadOnlyList<T>>
{
    public override IReadOnlyList<T>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType == JsonTokenType.StartObject
            ? [JsonSerializer.Deserialize(ref reader, Info<T>(options))!]
            : JsonSerializer.Deserialize(ref reader, Info<List<T>>(options));

    public override void Write(Utf8JsonWriter writer, IReadOnlyList<T> value, JsonSerializerOptions options)
        => JsonSerializer.Serialize(writer, value.ToList(), Info<List<T>>(options));

    private static JsonTypeInfo<TValue> Info<TValue>(JsonSerializerOptions options) => (JsonTypeInfo<TValue>)options.GetTypeInfo(typeof(TValue));
}

public sealed class LocalDateJsonConverter : JsonConverter<LocalDate>
{
    public override LocalDate Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => LocalDatePattern.Iso.Parse(reader.GetString() ?? "").Value;

    public override void Write(Utf8JsonWriter writer, LocalDate value, JsonSerializerOptions options)
        => writer.WriteStringValue(LocalDatePattern.Iso.Format(value));
}
