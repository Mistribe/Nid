using Nid.Core.Contracts;
using Nid.RealEstate.Contracts;
using Nid.RealEstate.Infrastructure.Persistence;
using Nid.TestSupport;
using NodaTime;

namespace Nid.Subscriptions.IntegrationTests.RealEstate;

[Collection("postgres")]
public class LoanStoreTests(PostgresFixture fixture)
{
    private static readonly LocalDate Disbursed = new(2020, 10, 15);

    /// <summary>Phases given out of seq order, a six-decimal rate, a cent-precise override: what the columns must keep.</summary>
    private static LoanContract Contract(decimal principal = 200_000m) => new(
        principal, Disbursed, new LocalDate(2020, 11, 15), 240, InterestRounding.Truncate, FirstPeriod.ProrataActual365,
        [
            new LoanPhaseEntry(new LoanPhase(2, PhaseMode.Annuity, 0.035m, RateType.Variable), "après le différé"),
            new LoanPhaseEntry(new LoanPhase(1, PhaseMode.DeferredPartial, 0.012345m, RateType.Fixed, 12, null, 150_000.5m), null),
        ],
        [new LoanInsuranceEntry(new LoanInsurance(InsuranceMode.RateOnRemaining, null, 0.0036m, new LocalDate(2020, 11, 15)), "ADI")]);

    private static async Task<(LoanStore Store, ItemId Id)> NewAsync(PostgresFixture f)
    {
        var store = new LoanStore(PostgresFixture.NewRealEstateContext(await f.NewConnectionStringAsync()));
        var id = ItemId.New();
        await store.CreateAsync(id, "Banque", "note", Contract(), default);
        return (store, id);
    }

    [Fact]
    public async Task A_loan_reads_back_whole_its_phases_by_seq_and_its_rates_to_six_decimals()
    {
        var (store, id) = await NewAsync(fixture);
        var loan = (await store.FindAsync(id, default))!;

        Assert.Equal(("Banque", "note", 200_000m), (loan.Lender, loan.Note, loan.Contract.Principal));
        Assert.Equal((InterestRounding.Truncate, FirstPeriod.ProrataActual365, 240), (loan.Contract.Rounding, loan.Contract.FirstPeriod, loan.Contract.TotalPayments));
        Assert.Equal([1, 2], loan.Contract.Phases.Select(p => p.Phase.Seq));
        var first = loan.Contract.Phases[0].Phase;
        Assert.Equal((0.012345m, 150_000.5m, 12), (first.AnnualRate, first.OpeningBalanceOverride!.Value, first.Payments!.Value));
        Assert.Equal(("après le différé", RateType.Variable), (loan.Contract.Phases[1].Note, loan.Contract.Phases[1].Phase.RateType));
        var insurance = Assert.Single(loan.Contract.Insurances);
        Assert.Equal((InsuranceMode.RateOnRemaining, 0.0036m, "ADI"), (insurance.Insurance.Mode, insurance.Insurance.AnnualRate!.Value, insurance.Note));
        Assert.NotEqual(0u, loan.Version);
        Assert.Null(await store.FindAsync(ItemId.New(), default));
    }

    [Fact]
    public async Task Replacing_the_contract_rewrites_its_parts_and_moves_the_version_even_when_only_phases_change()
    {
        var (store, id) = await NewAsync(fixture);
        var before = (await store.FindAsync(id, default))!;

        await store.ReplaceContractAsync(id, Contract() with
        {
            Phases = [new LoanPhaseEntry(new LoanPhase(1, PhaseMode.Annuity, 0.03m, RateType.Fixed), null)],
            Insurances = [],
        }, default);

        var after = (await store.FindAsync(id, default))!;
        Assert.Equal(0.03m, Assert.Single(after.Contract.Phases).Phase.AnnualRate);
        Assert.Empty(after.Contract.Insurances);
        Assert.NotEqual(before.Version, after.Version);

        await store.UpdateDetailsAsync(id, new LoanDetailsChange(ClearLender: true, Note: "renégocié"), default);
        var edited = (await store.FindAsync(id, default))!;
        Assert.Equal(((string?)null, "renégocié"), (edited.Lender, edited.Note));
    }

    [Fact]
    public async Task Fees_and_repayments_read_back_by_date_then_id_with_their_link_and_versions()
    {
        var (store, id) = await NewAsync(fixture);
        var (late, early, penalty, repayment) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
        await store.AddFeeAsync(id, late, new LoanFee(FeeKind.Guarantee, 2_400m, new LocalDate(2021, 1, 1)), null, default);
        await store.AddFeeAsync(id, early, new LoanFee(FeeKind.File, 1_000m, Disbursed), "dossier", default);
        await store.AddFeeAsync(id, penalty, new LoanFee(FeeKind.EarlyRepaymentPenalty, 300m, new LocalDate(2022, 3, 15)), null, default);
        await store.AddRepaymentAsync(id, repayment, new EarlyRepayment(new LocalDate(2022, 3, 15), 10_000m, RepaymentEffect.ReducePayment), penalty, "prime", default);

        var loan = (await store.FindAsync(id, default))!;
        Assert.Equal([early, late, penalty], loan.Fees.Select(f => f.Id));
        var stored = Assert.Single(loan.Repayments);
        Assert.Equal((repayment, penalty, RepaymentEffect.ReducePayment, "prime"), (stored.Id, stored.FeeId!.Value, stored.Repayment.Effect, stored.Note));

        await store.UpdateRepaymentAsync(id, repayment, stored.Repayment with { Amount = 12_000m }, null, null, default);
        await store.DeleteFeeAsync(id, penalty, default);
        loan = (await store.FindAsync(id, default))!;
        Assert.Equal((12_000m, (Guid?)null), (loan.Repayments[0].Repayment.Amount, loan.Repayments[0].FeeId));
        Assert.NotEqual(stored.Version, loan.Repayments[0].Version);
        Assert.Equal([early, late], loan.Fees.Select(f => f.Id));

        await store.LockAsync(id, default); // outside a transaction the lock is released at once: it must simply not fail
        await store.DeleteRepaymentAsync(id, repayment, default);
        Assert.Empty((await store.FindAsync(id, default))!.Repayments);
    }
}
