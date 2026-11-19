using Nid.Banking.Contracts;
using Nid.Banking.Domain;
using NodaTime;

namespace Nid.Banking.Tests;

/// <summary>Issue #81: the shortfall ahead, a checking account's balance less the charges due before the next income (today: 2026-03-10).</summary>
public class ShortfallsTests
{
    private static readonly LocalDate Today = new(2026, 3, 10);
    private static readonly Guid Courant = Guid.NewGuid();
    private static readonly Guid Joint = Guid.NewGuid();

    private static BankBalance Balance(Guid account, decimal amount, int daysOld = 1)
        => new(account, Today.PlusDays(-daysOld), amount, "CLBD", Instant.FromUtc(2026, 3, 9, 6, 0));

    private static Shortfalls.Due Due(int inDays, decimal amount, string name, Guid? account = null) => new(Today.PlusDays(inDays), amount, name, account);

    private static List<Shortfalls.Shortfall> Find(IReadOnlyDictionary<Guid, BankBalance?> accounts, decimal margin, params Shortfalls.Due[] dues)
        => [.. Shortfalls.Find(Today, Shortfalls.End(Today, null), margin, accounts, dues)];

    [Fact]
    public void The_first_day_below_the_margin_is_raised_with_the_charges_so_far_and_that_days_names()
    {
        var found = Assert.Single(Find(new Dictionary<Guid, BankBalance?> { [Courant] = Balance(Courant, 1_000m) }, 0m,
            Due(2, 300m, "Électricité", Courant), Due(5, 650m, "Loyer", Courant), Due(5, 100m, "Assurance", Courant), Due(9, 500m, "Crèche", Courant)));

        Assert.Equal((Courant, 1_000m, Today.PlusDays(5), 1_050m), (found.Account, found.Balance, found.On, found.Charges));
        Assert.Equal(["Loyer", "Assurance"], found.Names);
    }

    [Fact]
    public void Exactly_the_margin_left_is_not_a_shortfall()
    {
        var accounts = new Dictionary<Guid, BankBalance?> { [Courant] = Balance(Courant, 1_000m) };

        Assert.Empty(Find(accounts, 200m, Due(3, 800m, "Loyer", Courant)));
        Assert.Single(Find(accounts, 200.01m, Due(3, 800m, "Loyer", Courant)));
    }

    [Fact]
    public void An_unmatched_charge_counts_on_the_only_checking_account_and_on_none_of_two()
    {
        var one = new Dictionary<Guid, BankBalance?> { [Courant] = Balance(Courant, 100m) };
        var two = new Dictionary<Guid, BankBalance?> { [Courant] = Balance(Courant, 100m), [Joint] = Balance(Joint, 100m) };

        Assert.Equal(Courant, Assert.Single(Find(one, 0m, Due(3, 150m, "Netflix"))).Account);
        Assert.Empty(Find(two, 0m, Due(3, 150m, "Netflix")));
        Assert.Equal(Joint, Assert.Single(Find(two, 0m, Due(3, 150m, "Netflix", Joint))).Account);
    }

    [Fact]
    public void A_stale_or_missing_balance_says_nothing()
    {
        Assert.Empty(Find(new Dictionary<Guid, BankBalance?> { [Courant] = Balance(Courant, 100m, daysOld: 8) }, 0m, Due(3, 150m, "Loyer", Courant)));
        Assert.Empty(Find(new Dictionary<Guid, BankBalance?> { [Courant] = null }, 0m, Due(3, 150m, "Loyer", Courant)));
        Assert.Single(Find(new Dictionary<Guid, BankBalance?> { [Courant] = Balance(Courant, 100m, daysOld: 7) }, 0m, Due(3, 150m, "Loyer", Courant)));
    }

    [Fact]
    public void The_window_ends_the_day_before_the_next_income_else_after_31_days()
    {
        var accounts = new Dictionary<Guid, BankBalance?> { [Courant] = Balance(Courant, 100m) };
        Shortfalls.Due[] dues = [Due(-1, 500m, "Hier", Courant), Due(20, 150m, "Loyer", Courant)];

        Assert.Empty(Shortfalls.Find(Today, Shortfalls.End(Today, Today.PlusDays(20)), 0m, accounts, dues)); // paid on the 20th: the rent falls after it
        Assert.Single(Shortfalls.Find(Today, Shortfalls.End(Today, Today.PlusDays(21)), 0m, accounts, dues));
        Assert.Equal(Today.PlusDays(31), Shortfalls.End(Today, null));
        Assert.Single(Find(accounts, 0m, Due(30, 150m, "Loyer", Courant)));
        Assert.Empty(Find(accounts, 0m, Due(31, 150m, "Loyer", Courant)));
    }
}
