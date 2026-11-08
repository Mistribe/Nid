using System.Text.Json.Serialization;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;

namespace Nid.Core.Application.Import;

/// <summary>This module's import-file records, source-generated for NativeAOT, with <see cref="NidDataJson"/>'s rules.</summary>
[JsonSerializable(typeof(ValuationRecord))]
[JsonSerializable(typeof(FlowRecord))]
[JsonSerializable(typeof(PersonRecord))]
internal sealed partial class CoreFileJson : JsonSerializerContext
{
    public static CoreFileJson Records { get; } = new(NidDataJson.NewOptions());
}
