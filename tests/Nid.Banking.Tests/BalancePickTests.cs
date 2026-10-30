using Nid.Banking.Contracts;
using Nid.Banking.Domain;
using NodaTime;

namespace Nid.Banking.Tests;

public class BalancePickTests
{
    private static ProviderBalance Of(string type, decimal amount) => new(type, amount, "EUR", new LocalDate(2026, 8, 31));

    [Fact]
    public void The_closing_booked_balance_wins_over_the_available_one()
    {
        var picked = BalancePick.Choose([Of("CLAV", 90m), Of("CLBD", 100m), Of("ITBD", 95m)]);

        Assert.Equal((100m, false), (picked!.Value.Balance.Amount, picked.Value.Fallback));
    }

    [Fact]
    public void An_interim_booked_balance_is_no_fallback()
        => Assert.Equal(("ITBD", false), (BalancePick.Choose([Of("XPCD", 1m), Of("ITBD", 2m)])!.Value.Balance.Type, BalancePick.Choose([Of("ITBD", 2m)])!.Value.Fallback));

    [Fact]
    public void An_available_balance_alone_is_a_fallback()
    {
        var picked = BalancePick.Choose([Of("CLAV", 90m)]);

        Assert.Equal((90m, true), (picked!.Value.Balance.Amount, picked.Value.Fallback));
    }

    [Fact]
    public void None_of_the_five_types_is_no_balance()
        => Assert.Null(BalancePick.Choose([Of("OPBD", 1m), Of("PRCD", 2m)]));
}
