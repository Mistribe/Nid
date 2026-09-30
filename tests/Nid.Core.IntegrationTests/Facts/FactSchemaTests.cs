using Microsoft.EntityFrameworkCore;
using Nid.TestSupport;

namespace Nid.Core.IntegrationTests.Facts;

/// <summary>2A spec §3.4: the constraints the stores rely on exist in the database, not only in the model.</summary>
[Collection("postgres")]
public class FactSchemaTests(PostgresFixture postgres)
{
    private static async Task<List<string>> ScalarsAsync(Microsoft.EntityFrameworkCore.DbContext db, string sql)
    {
        var connection = db.Database.GetDbConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var values = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            values.Add(reader.GetString(0));
        return values;
    }

    [Fact]
    public async Task The_unique_indexes_are_partial_on_current_rows()
    {
        await using var db = await postgres.NewDatabaseAsync();

        var indexes = await ScalarsAsync(db,
            "select indexdef from pg_indexes where schemaname = 'core' and tablename in ('valuation','flow') and indexname not like 'pk_%' and indexdef like 'CREATE UNIQUE%' order by indexdef");

        Assert.Equal(5, indexes.Count);
        Assert.Contains(indexes, d => d.StartsWith("CREATE UNIQUE INDEX ux_valuation_current ON core.valuation")
            && d.Contains("(item_id, as_of)") && d.Contains("retired_at IS NULL"));
        Assert.Contains(indexes, d => d.StartsWith("CREATE UNIQUE INDEX ux_flow_opening_current ON core.flow")
            && d.Contains("(item_id)") && d.Contains("'opening'") && d.Contains("retired_at IS NULL"));
        Assert.Contains(indexes, d => d.StartsWith("CREATE UNIQUE INDEX ux_flow_external_ref_current ON core.flow")
            && d.Contains("(item_id, external_ref)") && d.Contains("external_ref IS NOT NULL") && d.Contains("retired_at IS NULL"));
        foreach (var table in new[] { "valuation", "flow" })
            Assert.Contains(indexes, d => d.StartsWith($"CREATE UNIQUE INDEX ux_{table}_import_ref ON core.{table}")
                && d.Contains("(imported_by, import_ref)") && d.Contains("import_ref IS NOT NULL") && d.Contains("retired_at IS NULL"));
    }

    [Fact]
    public async Task The_checks_refuse_a_non_positive_flow_and_an_unknown_kind()
    {
        await using var db = await postgres.NewDatabaseAsync();

        var checks = await ScalarsAsync(db,
            "select conname from pg_constraint where conrelid in ('core.flow'::regclass, 'core.valuation'::regclass) and contype = 'c' order by conname");

        Assert.Equal(
            ["ck_flow_amount_positive", "ck_flow_created_via", "ck_flow_import", "ck_flow_kind", "ck_flow_source",
                "ck_valuation_created_via", "ck_valuation_import", "ck_valuation_source"],
            checks);
    }
}
