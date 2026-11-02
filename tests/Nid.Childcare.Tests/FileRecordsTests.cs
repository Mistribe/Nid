using System.Text.Json;
using Nid.Childcare.Application.Import;
using Nid.Childcare.Contracts;

namespace Nid.Childcare.Tests;

/// <summary>Childcare decision 24: the two nid.data records, snake_case, amounts as strings.</summary>
public class FileRecordsTests
{
    [Fact]
    public void Records_round_trip()
    {
        const string arrangement = """
            { "id": "cc-1", "name": "Garde de Léa", "child": "dependent-1", "carer": { "name": "Martine", "pajemploi": "P1", "approval": null },
              "terms": [ { "from": "2025-09-01", "to": null, "net_hourly_rate": "4.30", "gross_hourly_rate": null, "overtime_pct": "10", "upkeep_per_9h": "3.80",
                "meal_price": "3.20", "programmed_weeks": 46, "schedule": [ { "day": 1, "arrival": "08:00", "departure": "17:30", "meal": true } ] } ],
              "gaps": [ { "monday": "2026-02-16", "weeks": 2 } ], "visibility": "shared", "ownership": [ { "person": "adult-1", "share_bp": 10000 } ], "closed": false }
            """;
        var read = JsonSerializer.Deserialize(arrangement, ChildcareFileJson.Records.ArrangementRecord)!;
        Assert.Equal("4.30", read.Terms[0].NetHourlyRate.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal("08:00", read.Terms[0].Schedule![0].Arrival);
        var again = JsonSerializer.Deserialize(JsonSerializer.Serialize(read, ChildcareFileJson.Records.ArrangementRecord), ChildcareFileJson.Records.ArrangementRecord)!;
        Assert.Equal(read.Terms[0].Schedule![0], again.Terms[0].Schedule![0]);
        Assert.Null(read.WeekOverrides);
        Assert.Contains("\"upkeep_per_9h\":\"3.80\"", JsonSerializer.Serialize(read, ChildcareFileJson.Records.ArrangementRecord));

        const string statement = """
            { "arrangement": "cc-1", "month": "2025-09", "mileage": "0", "cmg": "100",
              "days": [ { "date": "2025-09-01", "planned": true, "present": true, "arrival": "08:00", "departure": "17:30", "meal": true, "absence": null } ],
              "figures": { "normal_hours": 146, "activity_days": 16, "paid_leave_days": null, "complementary_hours": "0", "overtime_hours": "0", "net_pay": "626.37",
                "upkeep": "70.40", "meals": "57.60", "base_pay": "626.37", "deduction": "0", "extra_pay": "0", "paid_leave_pay": "0", "cost": "654.37",
                "tax_base": "596.77", "child_sick_deducted": 0 } }
            """;
        var s = JsonSerializer.Deserialize(statement, ChildcareFileJson.Records.StatementRecord)!;
        Assert.Equal(626.37m, s.Figures.NetPay);
        Assert.Equal("2025-09", s.Month);
    }

    [Fact]
    public void Week_types_and_overrides_use_snake_case_and_the_old_form_is_not_written()
    {
        const string arrangement = """
            { "id": "cc-1", "name": "G", "child": "d", "carer": { "name": "M" },
              "terms": [ { "from": "2025-09-01", "net_hourly_rate": "4.30", "overtime_pct": "10", "upkeep_per_9h": "3.80",
                "week_types": [ { "name": "A", "weeks": 20, "schedule": [ { "day": 1, "arrival": "08:00", "departure": "17:30" } ] } ] } ],
              "week_overrides": [ { "monday": "2025-09-08", "week_type": 0 } ], "ownership": [] }
            """;
        var read = JsonSerializer.Deserialize(arrangement, ChildcareFileJson.Records.ArrangementRecord)!;
        Assert.Equal("A", read.Terms[0].WeekTypes![0].Name);
        Assert.Null(read.Terms[0].ProgrammedWeeks);
        var written = JsonSerializer.Serialize(read, ChildcareFileJson.Records.ArrangementRecord);
        Assert.Contains("\"week_types\":[", written);
        Assert.Contains("\"week_overrides\":[{\"monday\":\"2025-09-08\",\"week_type\":0}]", written);
        Assert.DoesNotContain("programmed_weeks", written);
        Assert.DoesNotContain("\"schedule\":null", written);
    }
}
