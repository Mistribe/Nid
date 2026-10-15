using Microsoft.EntityFrameworkCore;
using Nid.Core.Contracts;
using Nid.RealEstate.Contracts;

namespace Nid.RealEstate.Infrastructure.Persistence;

public sealed class LoanStore(RealEstateDbContext db) : ILoanStore
{
    public async Task CreateAsync(ItemId item, string? lender, string? note, ItemId? property, LoanContract contract, CancellationToken ct)
    {
        var row = new LoanRow { ItemId = item.Value, Lender = lender, Note = note, PropertyItemId = property?.Value };
        Fill(row, contract);
        db.Loans.Add(row);
        AddParts(item, contract);
        await db.SaveChangesAsync(ct);
    }

    public async Task<StoredLoan?> FindAsync(ItemId item, CancellationToken ct)
        => (await FindManyAsync([item], ct)).SingleOrDefault();

    public async Task<IReadOnlyList<StoredLoan>> FindManyAsync(IReadOnlyCollection<ItemId> items, CancellationToken ct)
    {
        var ids = items.Select(i => i.Value).ToList();
        var loans = await db.Loans.AsNoTracking().Where(l => ids.Contains(l.ItemId)).ToListAsync(ct);
        if (loans.Count == 0)
            return [];

        var found = loans.Select(l => l.ItemId).ToList();
        var phases = (await db.Phases.AsNoTracking().Where(p => found.Contains(p.LoanItemId)).ToListAsync(ct)).ToLookup(p => p.LoanItemId);
        var insurances = (await db.Insurances.AsNoTracking().Where(i => found.Contains(i.LoanItemId)).ToListAsync(ct)).ToLookup(i => i.LoanItemId);
        var fees = (await db.Fees.AsNoTracking().Where(f => found.Contains(f.LoanItemId)).ToListAsync(ct)).ToLookup(f => f.LoanItemId);
        var events = (await db.Events.AsNoTracking().Where(e => found.Contains(e.LoanItemId)).ToListAsync(ct)).ToLookup(e => e.LoanItemId);

        // Ordered here, not by the database's collation or uuid order: the same order on every server (precision 12).
        return loans.Select(l => new StoredLoan(
                new ItemId(l.ItemId), l.Lender, l.Note,
                new LoanContract(l.Principal, l.DisbursedOn, l.FirstPaymentDate, l.TotalPayments,
                    Parse<InterestRounding>(l.RoundingMode), Parse<FirstPeriod>(l.FirstPeriodMode),
                    phases[l.ItemId].OrderBy(p => p.Seq).Select(ToPhase).ToList(),
                    insurances[l.ItemId].OrderBy(i => i.StartsOn).ThenBy(i => i.Id).Select(ToInsurance).ToList()),
                l.Version,
                fees[l.ItemId].OrderBy(f => f.OnDate).ThenBy(f => f.Id).Select(ToFee).ToList(),
                events[l.ItemId].OrderBy(e => e.OnDate).ThenBy(e => e.Id).Select(ToRepayment).ToList(),
                l.PropertyItemId is { } property ? new ItemId(property) : null))
            .ToList();
    }

    public async Task<IReadOnlyList<StoredLoan>> FindLinkedAsync(IReadOnlyCollection<ItemId> properties, CancellationToken ct)
    {
        var ids = properties.Select(p => (Guid?)p.Value).ToList();
        var linked = await db.Loans.AsNoTracking().Where(l => ids.Contains(l.PropertyItemId)).Select(l => l.ItemId).ToListAsync(ct);
        return await FindManyAsync(linked.Select(i => new ItemId(i)).ToList(), ct);
    }

    public Task LockAsync(ItemId item, CancellationToken ct)
        => db.Database.ExecuteSqlAsync($"select 1 from realestate.loan where item_id = {item.Value} for update", ct);

    public async Task UpdateDetailsAsync(ItemId item, LoanDetailsChange change, CancellationToken ct)
    {
        var row = await db.Loans.SingleAsync(l => l.ItemId == item.Value, ct);
        if (change.ClearLender) row.Lender = null; else if (change.Lender is { } lender) row.Lender = lender;
        if (change.ClearNote) row.Note = null; else if (change.Note is { } note) row.Note = note;
        if (change.ClearProperty) row.PropertyItemId = null; else if (change.PropertyId is { } property) row.PropertyItemId = property.Value;
        await db.SaveChangesAsync(ct);
    }

    public async Task ReplaceContractAsync(ItemId item, LoanContract contract, CancellationToken ct)
    {
        var row = await db.Loans.SingleAsync(l => l.ItemId == item.Value, ct);
        Fill(row, contract);
        db.Entry(row).State = EntityState.Modified; // the contract's version moves even when only its phases change
        db.Phases.RemoveRange(await db.Phases.Where(p => p.LoanItemId == item.Value).ToListAsync(ct));
        db.Insurances.RemoveRange(await db.Insurances.Where(i => i.LoanItemId == item.Value).ToListAsync(ct));
        await db.SaveChangesAsync(ct); // the old seqs leave before the new ones arrive: (loan_item_id, seq) is unique
        AddParts(item, contract);
        await db.SaveChangesAsync(ct);
    }

    public async Task AddFeeAsync(ItemId loan, Guid id, LoanFee fee, string? note, CancellationToken ct)
    {
        var row = new LoanFeeRow { Id = id, LoanItemId = loan.Value };
        Fill(row, fee, note);
        db.Fees.Add(row);
        await db.SaveChangesAsync(ct);
    }

    public async Task UpdateFeeAsync(ItemId loan, Guid id, LoanFee fee, string? note, CancellationToken ct)
    {
        var row = await db.Fees.SingleAsync(f => f.Id == id && f.LoanItemId == loan.Value, ct);
        Fill(row, fee, note);
        db.Entry(row).State = EntityState.Modified;
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteFeeAsync(ItemId loan, Guid id, CancellationToken ct)
    {
        db.Fees.Remove(await db.Fees.SingleAsync(f => f.Id == id && f.LoanItemId == loan.Value, ct));
        await db.SaveChangesAsync(ct);
    }

    public async Task AddRepaymentAsync(ItemId loan, Guid id, EarlyRepayment repayment, Guid? feeId, string? note, CancellationToken ct)
    {
        var row = new LoanEventRow { Id = id, LoanItemId = loan.Value };
        Fill(row, repayment, feeId, note);
        db.Events.Add(row);
        await db.SaveChangesAsync(ct);
    }

    public async Task UpdateRepaymentAsync(ItemId loan, Guid id, EarlyRepayment repayment, Guid? feeId, string? note, CancellationToken ct)
    {
        var row = await db.Events.SingleAsync(e => e.Id == id && e.LoanItemId == loan.Value, ct);
        Fill(row, repayment, feeId, note);
        db.Entry(row).State = EntityState.Modified; // a penalty changed alone still moves the repayment's version
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteRepaymentAsync(ItemId loan, Guid id, CancellationToken ct)
    {
        db.Events.Remove(await db.Events.SingleAsync(e => e.Id == id && e.LoanItemId == loan.Value, ct));
        await db.SaveChangesAsync(ct);
    }

    private void AddParts(ItemId item, LoanContract contract)
    {
        // Spec 3F1 §2: a rewritten phase or insurance keeps the file id its entry carries.
        foreach (var entry in contract.Phases)
        {
            var phase = entry.Phase;
            var row = new LoanPhaseRow
            {
                Id = Guid.CreateVersion7(), LoanItemId = item.Value, Seq = phase.Seq, Mode = LoanWire.Of(phase.Mode),
                AnnualRate = phase.AnnualRate, RateType = LoanWire.Of(phase.RateType), Payments = phase.Payments,
                PaymentOverride = phase.PaymentOverride, OpeningBalanceOverride = phase.OpeningBalanceOverride, Note = entry.Note,
            };
            RealEstateImports.Mark(row, entry.Import);
            db.Phases.Add(row);
        }
        foreach (var entry in contract.Insurances)
        {
            var insurance = entry.Insurance;
            var row = new LoanInsuranceRow
            {
                Id = Guid.CreateVersion7(), LoanItemId = item.Value, Mode = LoanWire.Of(insurance.Mode),
                MonthlyAmount = insurance.MonthlyAmount, AnnualRate = insurance.AnnualRate, StartsOn = insurance.StartsOn,
                EndsOn = insurance.EndsOn, Note = entry.Note,
            };
            RealEstateImports.Mark(row, entry.Import);
            db.Insurances.Add(row);
        }
    }

    private static void Fill(LoanRow row, LoanContract contract)
    {
        row.Principal = contract.Principal;
        row.DisbursedOn = contract.DisbursedOn;
        row.FirstPaymentDate = contract.FirstPaymentDate;
        row.TotalPayments = contract.TotalPayments;
        row.RoundingMode = LoanWire.Of(contract.Rounding);
        row.FirstPeriodMode = LoanWire.Of(contract.FirstPeriod);
    }

    private static void Fill(LoanFeeRow row, LoanFee fee, string? note)
    {
        row.Kind = LoanWire.Of(fee.Kind);
        row.Amount = fee.Amount;
        row.OnDate = fee.OnDate;
        row.Note = note;
    }

    private static void Fill(LoanEventRow row, EarlyRepayment repayment, Guid? feeId, string? note)
    {
        row.OnDate = repayment.OnDate;
        row.Amount = repayment.Amount;
        row.Effect = LoanWire.Of(repayment.Effect);
        row.FeeId = feeId;
        row.Note = note;
    }

    private static LoanPhaseEntry ToPhase(LoanPhaseRow r)
        => new(new LoanPhase(r.Seq, Parse<PhaseMode>(r.Mode), r.AnnualRate, Parse<RateType>(r.RateType), r.Payments,
            r.PaymentOverride, r.OpeningBalanceOverride), r.Note) { Import = RealEstateImports.MarkOf(r), Id = r.Id };

    private static LoanInsuranceEntry ToInsurance(LoanInsuranceRow r)
        => new(new LoanInsurance(Parse<InsuranceMode>(r.Mode), r.MonthlyAmount, r.AnnualRate, r.StartsOn, r.EndsOn), r.Note)
            { Import = RealEstateImports.MarkOf(r), Id = r.Id };

    private static StoredFee ToFee(LoanFeeRow r) => new(r.Id, new LoanFee(Parse<FeeKind>(r.Kind), r.Amount, r.OnDate), r.Note, r.Version);

    private static StoredRepayment ToRepayment(LoanEventRow r)
        => new(r.Id, new EarlyRepayment(r.OnDate, r.Amount, Parse<RepaymentEffect>(r.Effect)), r.FeeId, r.Note, r.Version);

    /// <summary>The check constraints admit only these texts: anything else is a broken invariant.</summary>
    private static T Parse<T>(string text) where T : struct, Enum
        => LoanWire.TryParse<T>(text, out var value) ? value : throw new InvalidOperationException($"'{text}' is not a stored {typeof(T).Name}.");
}
