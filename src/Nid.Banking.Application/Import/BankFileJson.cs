using System.Text.Json.Serialization;
using Nid.Banking.Contracts;
using Nid.Platform.Contracts;

namespace Nid.Banking.Application.Import;

/// <summary>This module's import-file records, source-generated for NativeAOT, with <see cref="NidDataJson"/>'s rules.</summary>
[JsonSerializable(typeof(BankAccountRecord))]
[JsonSerializable(typeof(BankTransactionRecord))]
internal sealed partial class BankFileJson : JsonSerializerContext
{
    public static BankFileJson Records { get; } = new(NidDataJson.NewOptions());
}
