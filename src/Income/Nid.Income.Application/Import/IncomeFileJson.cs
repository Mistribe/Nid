using System.Text.Json.Serialization;
using Nid.Income.Contracts;
using Nid.Platform.Contracts;

namespace Nid.Income.Application.Import;

/// <summary>This module's import-file records, source-generated for NativeAOT, with <see cref="NidDataJson"/>'s rules.</summary>
[JsonSerializable(typeof(IncomeSourceRecord))]
[JsonSerializable(typeof(IncomePhaseRecord))]
[JsonSerializable(typeof(IncomeOneOffTaxableRecord))]
internal sealed partial class IncomeFileJson : JsonSerializerContext
{
    public static IncomeFileJson Records { get; } = new(NidDataJson.NewOptions());
}
