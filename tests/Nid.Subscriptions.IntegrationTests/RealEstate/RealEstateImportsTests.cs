using Microsoft.EntityFrameworkCore;
using Nid.Core.Contracts;
using Nid.RealEstate.Contracts;
using Nid.RealEstate.Infrastructure.Persistence;
using Nid.TestSupport;
using NodaTime;
using Duration = Nid.Core.Contracts.Duration;

namespace Nid.Subscriptions.IntegrationTests.RealEstate;

/// <summary>Spec 3F1 §2: the import marks of the module's tables, through <see cref="IRealEstateImports"/> and the stores.</summary>
[Collection("postgres")]
public class RealEstateImportsTests(PostgresFixture fixture)
{
    private static readonly PersonId Alice = PersonId.New();
    private static readonly PersonId Bob = PersonId.New();
    private static readonly LocalDate First = new(2020, 11, 15);

    private static string Hash(char c) => new(c, 64);

    private static PropertyDetails Home() => new(PropertyType.PrimaryResidence, new LocalDate(2020, 6, 15), 250_000m, 0m, 0m, 0m,
        null, null, PropertyCadence.Yearly, null, null, null);

    private static RecurringCost Tax(decimal amount = 1_200m)
        => new("property_tax", amount,
            Duration.Create(1, DurationUnit.Year).TryGetValue(out var year) ? year : throw new InvalidOperationException("A year is a period."),
            new LocalDate(2020, 10, 15), null, null);

    private static async Task<(PropertyStore Properties, LoanStore Loans, RealEstateImports Imports)> NewAsync(PostgresFixture f)
    {
        var db = PostgresFixture.NewRealEstateContext(await f.NewConnectionStringAsync());
        return (new PropertyStore(db), new LoanStore(db), new RealEstateImports(db));
    }

    [Fact]
    public async Task A_marked_row_is_found_by_its_file_id_and_keeps_its_mark_through_an_edit_in_the_app()
    {
        var (properties, _, imports) = await NewAsync(fixture);
        var home = ItemId.New();
        var cost = Guid.CreateVersion7();
        await properties.CreateAsync(home, Home(), default);
        await properties.AddCostAsync(home, cost, Tax(), default);
        await imports.MarkAsync(EstateTable.Property, home.Value, new ImportMark("prop-home", Hash('a'), Alice), default);
        await imports.MarkAsync(EstateTable.RecurringCost, cost, new ImportMark("cost-tax", Hash('b'), Alice), default);

        var found = await imports.FindAsync(EstateTable.RecurringCost, ["cost-tax", "cost-none"], default);

        var row = Assert.Single(Assert.Single(found).Value);
        Assert.Equal((cost, home, Hash('b'), Alice), (row.Id, row.Item, row.Hash, row.ImportedBy));
        Assert.Equal(new ImportMark("prop-home", Hash('a'), Alice), (await imports.MarksAsync(EstateTable.Property, [home], default))[home.Value]);

        await properties.UpdateCostAsync(home, cost, Tax(1_300m), default);
        Assert.Equal(new ImportMark("cost-tax", Hash('b'), Alice), (await imports.MarksAsync(EstateTable.RecurringCost, [home], default))[cost]);
    }

    [Fact]
    public async Task A_loans_phases_and_insurances_keep_their_file_ids_through_a_contract_rewrite()
    {
        var (_, loans, imports) = await NewAsync(fixture);
        var loan = ItemId.New();
        var phase = new ImportMark("lp-1", Hash('c'), Alice);
        await loans.CreateAsync(loan, null, null, null, new LoanContract(
            200_000m, new LocalDate(2020, 10, 15), First, 240, InterestRounding.HalfUp, FirstPeriod.Full,
            [new LoanPhaseEntry(new LoanPhase(1, PhaseMode.Annuity, 0.035m, RateType.Fixed), null) { Import = phase }],
            [new LoanInsuranceEntry(new LoanInsurance(InsuranceMode.RateOnInitial, null, 0.003m, First), null) { Import = new ImportMark("li-1", Hash('d'), Alice) }]),
            default);

        var stored = (await loans.FindAsync(loan, default))!;
        var row = Assert.Single((await imports.FindAsync(EstateTable.Phase, ["lp-1"], default))["lp-1"]);
        Assert.Equal(phase, stored.Contract.Phases[0].Import);
        Assert.Equal((stored.Contract.Phases[0].Id, loan), (row.Id, row.Item));

        await loans.ReplaceContractAsync(loan, stored.Contract, default);

        var rewritten = (await loans.FindAsync(loan, default))!;
        Assert.Equal(phase, rewritten.Contract.Phases[0].Import);
        Assert.Equal("li-1", rewritten.Contract.Insurances[0].Import?.Ref);
        Assert.NotEqual(stored.Contract.Phases[0].Id, rewritten.Contract.Phases[0].Id);
    }

    [Fact]
    public async Task A_file_id_is_unique_per_importer_not_per_instance()
    {
        var (properties, _, imports) = await NewAsync(fixture);
        var home = ItemId.New();
        var (mine, theirs, again) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
        await properties.CreateAsync(home, Home(), default);
        foreach (var cost in new[] { mine, theirs, again })
            await properties.AddCostAsync(home, cost, Tax(), default);
        await imports.MarkAsync(EstateTable.RecurringCost, mine, new ImportMark("cost-tax", Hash('a'), Alice), default);
        await imports.MarkAsync(EstateTable.RecurringCost, theirs, new ImportMark("cost-tax", Hash('a'), Bob), default);

        Assert.Equal(2, (await imports.FindAsync(EstateTable.RecurringCost, ["cost-tax"], default))["cost-tax"].Count);
        await Assert.ThrowsAsync<DbUpdateException>(
            () => imports.MarkAsync(EstateTable.RecurringCost, again, new ImportMark("cost-tax", Hash('b'), Alice), default));
    }

    [Theory]
    [InlineData("property")]
    [InlineData("property_recurring_cost")]
    [InlineData("property_tenancy")]
    [InlineData("loan")]
    [InlineData("loan_phase")]
    [InlineData("loan_insurance")]
    [InlineData("loan_fee")]
    [InlineData("loan_event")]
    public async Task Every_table_an_import_writes_keeps_a_file_id_unique_per_importer(string table)
    {
        await using var db = PostgresFixture.NewRealEstateContext(await fixture.NewConnectionStringAsync());

        var index = await db.Database
            .SqlQuery<string>($"select indexdef as \"Value\" from pg_indexes where schemaname = 'realestate' and indexname = {$"ix_{table}_imported_by_import_ref"}")
            .SingleAsync();
        var check = await db.Database
            .SqlQuery<string>($"select conname::text as \"Value\" from pg_constraint where conname = {$"ck_{table}_import"}")
            .ToListAsync();

        Assert.StartsWith("CREATE UNIQUE INDEX", index);
        Assert.EndsWith("(imported_by, import_ref) WHERE (import_ref IS NOT NULL)", index);
        Assert.Single(check);
    }
}
