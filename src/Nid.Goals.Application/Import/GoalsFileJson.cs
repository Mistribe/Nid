using System.Text.Json.Serialization;
using Nid.Goals.Contracts;
using Nid.Platform.Contracts;

namespace Nid.Goals.Application.Import;

/// <summary>This module's import-file records, source-generated for NativeAOT, with <see cref="NidDataJson"/>'s rules.</summary>
[JsonSerializable(typeof(GoalRecord))]
internal sealed partial class GoalsFileJson : JsonSerializerContext
{
    public static GoalsFileJson Records { get; } = new(NidDataJson.NewOptions());
}
