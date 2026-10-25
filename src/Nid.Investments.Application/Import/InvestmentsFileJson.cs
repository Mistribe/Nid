using System.Text.Json.Serialization;
using Nid.Investments.Contracts;
using Nid.Platform.Contracts;

namespace Nid.Investments.Application.Import;

/// <summary>This module's import-file records, source-generated for NativeAOT, with <see cref="NidDataJson"/>'s rules.</summary>
[JsonSerializable(typeof(EnvelopeRecord))]
[JsonSerializable(typeof(HoldingRecord))]
[JsonSerializable(typeof(HoldingSnapshotRecord))]
[JsonSerializable(typeof(HoldingMovementRecord))]
internal sealed partial class InvestmentsFileJson : JsonSerializerContext
{
    public static InvestmentsFileJson Records { get; } = new(NidDataJson.NewOptions());
}
