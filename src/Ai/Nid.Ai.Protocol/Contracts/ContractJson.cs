using System.Text.Json;
using System.Text.Json.Schema;
using System.Text.Json.Serialization;

namespace Nid.Ai.Protocol.Contracts;

/// <summary>A contract checked by more than its shape (ranges, fields one capability needs). List elements may be
/// <c>null</c> here: the serializer does not check them.</summary>
public interface IContract
{
    bool IsValid();
}

/// <summary>How both sides read and write the contracts: camelCase, kebab-case enums, no unknown field, every
/// constructor parameter present, nullability respected. Anything else is <c>contract_invalid</c>.</summary>
public static class ContractJson
{
    public static JsonSerializerOptions Strict { get; } = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        NumberHandling = JsonNumberHandling.Strict,
        // Set, not left to the first serialization: the schema exporter reads it before any (the schemas test alone).
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower, allowIntegerValues: false) },
    };

    public static T Read<T>(ReadOnlySpan<byte> json) where T : IContract
    {
        T? contract;
        try
        {
            contract = JsonSerializer.Deserialize<T>(json, Strict);
        }
        catch (JsonException)
        {
            throw new ProtocolException("contract_invalid");
        }

        return contract is not null && contract.IsValid() ? contract : throw new ProtocolException("contract_invalid");
    }

    public static byte[] Write<T>(T contract) where T : IContract => JsonSerializer.SerializeToUtf8Bytes(contract, Strict);

    /// <summary>The committed schema of a contract (<c>contracts/</c>): indented, ending with a newline.</summary>
    public static string SchemaText(Type contract)
    {
        var schema = JsonSchemaExporter.GetJsonSchemaAsNode(Strict, contract,
            new JsonSchemaExporterOptions { TreatNullObliviousAsNonNullable = true });
        return schema.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";
    }
}
