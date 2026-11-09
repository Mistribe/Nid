using System.Globalization;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using NodaTime;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.RealEstate.Application;

/// <summary>A loan's remaining balance (<c>loan:&lt;id&gt;:remaining</c>) and payment (<c>:payment</c>), one point per schedule row, as its schedule shows them.</summary>
public sealed class LoanSeries(IVisibleItems visible, IItemRegistry items, IInvoker invoker) : ISeriesContribution
{
    private const string Remaining = "remaining";
    private const string Payment = "payment";

    public string Module => "realestate";

    public async Task<IReadOnlyList<SeriesInfo>> ListAsync(PersonId reader, VisibilityScope scope, string? q, IReadOnlyCollection<string> kinds, CancellationToken ct)
    {
        var ids = await visible.ForAsync(reader, scope, ct);
        var list = new List<SeriesInfo>();
        foreach (var (kind, suffix) in new[] { (SeriesKinds.LoanRemaining, Remaining), (SeriesKinds.LoanPayment, Payment) })
            list.AddRange((await items.SearchAsync(ids, Module, "loan", SeriesSearch.NameFilter(q, kinds, kind, "loan", suffix), ct)).Select(l => Info(l, suffix)));
        return list;
    }

    public async Task<SeriesPoints?> PointsAsync(PersonId reader, VisibilityScope scope, string key, LocalDate? from, LocalDate? to, CancellationToken ct)
    {
        if (key.Split(':') is not ["loan", var id, (Remaining or Payment) and var suffix] || !Guid.TryParse(id, out var guid)) return null;
        var itemId = new ItemId(guid);
        var schedule = await invoker.InvokeAsync(new GetLoanScheduleQuery(itemId), ct); // item access, denials journaled
        if (!schedule.TryGetValue(out var view) || await items.FindAsync(itemId, ct) is not { } item) return null;
        var points = SeriesWindow.Within(view.Rows.Select(r => new SeriesPoint(r.DueDate, decimal.Parse(suffix == Remaining ? r.Balance : r.Payment, CultureInfo.InvariantCulture))), from, to);
        return new SeriesPoints(Info(item, suffix), points);
    }

    private static SeriesInfo Info(ItemAccess loan, string suffix)
        => new($"loan:{loan.Id.Value}:{suffix}", suffix == Remaining ? SeriesKinds.LoanRemaining : SeriesKinds.LoanPayment, loan.DisplayName,
            $"currency:{loan.Currency}", SeriesFrequencies.Monthly, null, null, loan.Id);
}
