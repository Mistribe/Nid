using Microsoft.Extensions.DependencyInjection;
using Nid.Core.Domain.Inflation;
using NodaTime;

namespace Nid.TestSupport;

public static class CpiFixtures
{
    /// <summary>Seeds 13 INSEE months ending at <paramref name="last"/>: the first at 100, the others at 100 + <paramref name="percentOver12Months"/>.</summary>
    public static async Task SeedCpiAsync(this NidApp app, YearMonth last, decimal percentOver12Months)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var values = Enumerable.Range(0, 13).Select(i => new CpiValue(last.PlusMonths(i - 12), i == 0 ? 100m : 100m + percentOver12Months,
            new LocalDate(last.Year, last.Month, 1).PlusMonths(1).PlusDays(9), new LocalDate(last.Year, last.Month, 1).PlusMonths(1).PlusDays(10), "insee")).ToList();
        await scope.ServiceProvider.GetRequiredService<ICpiStore>().UpsertManyAsync(values, TestContext.Current.CancellationToken);
    }

    /// <summary>Seeds the INSEE months <paramref name="first"/>..<paramref name="last"/> at 100 × <paramref name="monthlyFactor"/>^n.</summary>
    public static async Task SeedCpiAsync(this NidApp app, YearMonth first, YearMonth last, decimal monthlyFactor)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var values = new List<CpiValue>();
        var index = 100m;
        for (var m = first; m <= last; m = m.PlusMonths(1), index *= monthlyFactor)
            values.Add(new CpiValue(m, index, new LocalDate(m.Year, m.Month, 1).PlusMonths(1).PlusDays(9), new LocalDate(m.Year, m.Month, 1).PlusMonths(1).PlusDays(10), "insee"));
        await scope.ServiceProvider.GetRequiredService<ICpiStore>().UpsertManyAsync(values, TestContext.Current.CancellationToken);
    }
}
