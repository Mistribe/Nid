using System.Text.Json.Serialization;
using Nid.Childcare.Contracts;
using Nid.Platform.Contracts;

namespace Nid.Childcare.Application.Import;

/// <summary>This module's import-file records, source-generated for NativeAOT, with <see cref="NidDataJson"/>'s rules.</summary>
[JsonSerializable(typeof(ArrangementRecord))]
[JsonSerializable(typeof(StatementRecord))]
public sealed partial class ChildcareFileJson : JsonSerializerContext
{
    public static ChildcareFileJson Records { get; } = new(NidDataJson.NewOptions());
}
