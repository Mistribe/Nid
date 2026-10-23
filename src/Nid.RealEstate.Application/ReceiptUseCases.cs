using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.RealEstate.Contracts;
using Nid.RealEstate.Domain;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Synapse.Abstractions;
using static Nid.RealEstate.Application.LoanReading;

namespace Nid.RealEstate.Application;

/// <summary>Rental receipts spec §4: the receipt of <paramref name="Month"/> (any day of it); null is the month of the latest cash in, else the current one.</summary>
public sealed record GetRentReceiptQuery(ItemId ItemId, LocalDate? Month) : IQuery<RentReceiptView>, IItemScoped
{
    public AccessKind Access => AccessKind.Read;
}

/// <summary>
/// <c>Kind</c> is <c>quittance</c>, <c>receipt</c> (partial payment) or <c>unpaid</c>; <c>Month</c> is <c>yyyy-mm</c>; <c>Missing</c> lists the
/// identity fields the receipt cannot name; <c>Months</c> are the months a receipt can be asked for, newest first.
/// </summary>
public sealed record RentReceiptView(
    ItemId Property, string PropertyName, string Currency, string Month, string Kind, string Rent, string Charges, string Total, string Paid,
    LocalDate? PaidOn, string? PostalAddress, string? TenantNames, string? LandlordName, string? LandlordAddress, IReadOnlyList<string> Missing,
    IReadOnlyList<string> Months) : IExposesItems
{
    public IEnumerable<ItemId> ExposedItems() => [Property];
}

[RequestHandler<GetRentReceiptQuery, RentReceiptView>]
public sealed class GetRentReceiptHandler(IPropertyStore properties, IItemRegistry items, IFlows flows, IHouseholdCalendar calendar)
    : IRequestHandler<GetRentReceiptQuery, RentReceiptView>
{
    private static LocalDate First(LocalDate d) => new(d.Year, d.Month, 1);

    public async ValueTask<Result<RentReceiptView>> HandleAsync(GetRentReceiptQuery query, CancellationToken ct = default)
    {
        var loaded = await PropertyCheck.LoadAsync(properties, items, query.ItemId, ct);
        if (loaded.TryGetFailure(out var failure))
            return Result.Failure<RentReceiptView>(failure!);
        loaded.TryGetValue(out var found);
        var listed = await flows.ListAsync(found.Property.ItemId, PropertyReading.Everything, null, ct);
        if (listed.TryGetFailure(out failure))
            return Result.Failure<RentReceiptView>(failure!);
        listed.TryGetValue(out var all);
        var today = await calendar.TodayAsync(ct);
        var stored = found.Property.Tenancies;
        var tenancies = stored.Select(t => t.Tenancy).ToList();

        var months = ReceiptMonths.Of(stored, all!, today);

        if (query.Month is null && months.Count == 0)
            return Result.Failure<RentReceiptView>(RentRevisions.Refusal(PropertyCodes.ReceiptNoTenancy, "No tenancy covers a month a receipt can be asked for.", "month"));
        var cashMonths = all!.Where(f => f.Kind == FlowKind.Income && PropertyCategories.CashIn.Contains(f.Category!)).Select(f => First(f.OccurredOn)).ToHashSet();
        // Default: the newest allowed month with cash in, else the current month, else the newest allowed one.
        var month = query.Month is { } asked ? First(asked)
            : months.Where(cashMonths.Contains).Cast<LocalDate?>().FirstOrDefault() ?? (months.Contains(First(today)) ? First(today) : months[0]);

        if (RentReceipt.For(tenancies, all!, month, today) is not { } receipt)
        {
            // Null is a month after today's, one no tenancy covers, or one before the rents tracked.
            return Result.Failure<RentReceiptView>(month <= today && tenancies.All(t => PropertyMath.RentIn(t, month) is null)
                ? RentRevisions.Refusal(PropertyCodes.ReceiptNoTenancy, "No tenancy covers this month.", "month")
                : RentRevisions.Refusal(PropertyCodes.ReceiptMonth, "A receipt can be asked for a month of the rent ledger, up to the current one.", "month"));
        }
        if (ReceiptMonths.Covering(stored, month) is not { } covering)
            return Result.Failure<RentReceiptView>(RentRevisions.Refusal(PropertyCodes.ReceiptTwoLeases,
                "Two leases that are not one revision chain cover this month: a receipt is for one lease.", "month"));

        // A revision never changes the parties: the identity is the chain's last link's, the one the screen edits.
        var named = covering[^1];
        while (stored.FirstOrDefault(t => t.RevisedFrom == named.Id) is { } next)
            named = next;
        var lease = named.Tenancy;
        var postal = found.Property.Details.PostalAddress;
        var missing = new List<string>();
        if (postal is null) missing.Add("postal_address");
        if (lease.TenantNames is null) missing.Add("tenant_names");
        if (lease.LandlordName is null) missing.Add("landlord_name");
        if (lease.LandlordAddress is null) missing.Add("landlord_address");

        return Result.Success(new RentReceiptView(
            found.Property.ItemId, found.Item.DisplayName, LoanViews.CurrencyOf(found.Item).Code, ReceiptMonths.Wire(month), receipt.Kind, Amount(receipt.Rent),
            Amount(receipt.Charges), Amount(receipt.Total), Amount(receipt.Paid), receipt.PaidOn, postal, lease.TenantNames, lease.LandlordName,
            lease.LandlordAddress, missing, months.Select(ReceiptMonths.Wire).ToList()));
    }
}

/// <summary>Rental receipts spec §4: the months a receipt can be given for, newest first — the receipt's selector and the detail's links.</summary>
internal static class ReceiptMonths
{
    /// <summary>The selector shows 36 months at most; the route answers any month of the ledger.</summary>
    public const int Shown = 36;

    public static string Wire(LocalDate m) => $"{m.Year:0000}-{m.Month:00}";

    /// <summary>Down from the current month to the ledger's start: a month some tenancy owes, of one lease or one revision chain.</summary>
    public static List<LocalDate> Of(IReadOnlyList<StoredTenancy> stored, IReadOnlyList<Flow> flows, LocalDate today)
    {
        var tenancies = stored.Select(t => t.Tenancy).ToList();
        var start = RentLedger.Start(tenancies, flows, today);
        var months = new List<LocalDate>();
        for (var m = new LocalDate(today.Year, today.Month, 1); months.Count < Shown && m >= new LocalDate(start.Year, start.Month, 1); m = m.PlusMonths(-1))
            if (RentReceipt.InRange(tenancies, flows, m, today) && Covering(stored, m) is not null)
                months.Add(m);
        return months;
    }

    /// <summary>The links covering a day of the month, oldest first; null when two of them are not one revision chain (a change of tenant).</summary>
    public static List<StoredTenancy>? Covering(IReadOnlyList<StoredTenancy> stored, LocalDate month)
    {
        var covering = stored.Where(t => PropertyMath.RentIn(t.Tenancy, month, false) is not null).OrderBy(t => t.Tenancy.StartsOn).ToList();
        return covering.Skip(1).Zip(covering, (next, previous) => next.RevisedFrom == previous.Id).All(same => same) ? covering : null;
    }
}
