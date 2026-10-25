using System.Text.Json.Serialization;
using Nid.RealEstate.Contracts;
using Nid.Platform.Contracts;

namespace Nid.RealEstate.Application.Import;

/// <summary>This module's import-file records, source-generated for NativeAOT, with <see cref="NidDataJson"/>'s rules.</summary>
[JsonSerializable(typeof(LoanRecord))]
[JsonSerializable(typeof(LoanPhaseRecord))]
[JsonSerializable(typeof(LoanInsuranceRecord))]
[JsonSerializable(typeof(LoanFeeRecord))]
[JsonSerializable(typeof(LoanEventRecord))]
[JsonSerializable(typeof(LoanBankScheduleRecord))]
[JsonSerializable(typeof(PropertyRecord))]
[JsonSerializable(typeof(PropertyRecurringCostRecord))]
[JsonSerializable(typeof(TenancyRecord))]
internal sealed partial class RealEstateFileJson : JsonSerializerContext
{
    public static RealEstateFileJson Records { get; } = new(NidDataJson.NewOptions());
}
