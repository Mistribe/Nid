using System.Text.Json.Serialization;
using Nid.Assets.Contracts;
using Nid.Platform.Contracts;

namespace Nid.Assets.Application.Import;

/// <summary>This module's import-file records, source-generated for NativeAOT, with <see cref="NidDataJson"/>'s rules.</summary>
[JsonSerializable(typeof(AssetImportRecord))]
internal sealed partial class AssetsFileJson : JsonSerializerContext
{
    public static AssetsFileJson Records { get; } = new(NidDataJson.NewOptions());
}
