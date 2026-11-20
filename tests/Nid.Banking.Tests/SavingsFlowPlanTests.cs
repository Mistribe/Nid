using Nid.Banking.Domain;
using Nid.Core.Contracts;
using NodaTime;

namespace Nid.Banking.Tests;

public class SavingsFlowPlanTests
{
    private static readonly LocalDate Associated = new(2026, 3, 1);

    private static SavingsLine L(LocalDate on, decimal amount, decimal interest = 0m, string currency = "EUR")
        => new(Guid.NewGuid(), on, amount, currency, "VIR Alice", interest);

    private static LocalDate D(int month, int day) => new(2026, month, day);

    private static IReadOnlyList<WantedFlow> Plan(IReadOnlyList<SavingsLine> lines, IReadOnlyList<TypedFlow>? typed = null, LocalDate? associatedOn = null)
        => SavingsFlowPlan.Plan(lines, typed ?? [], associatedOn ?? Associated, "EUR");

    [Fact]
    public void A_deposit_is_a_contribution()
    {
        var line = L(D(3, 10), 100m);
        Assert.Equal([new WantedFlow($"bank:{line.Id}", D(3, 10), FlowKind.Contribution, 100m, null, "VIR Alice")], Plan([line]));
    }

    [Fact]
    public void A_debit_is_a_withdrawal_with_a_positive_amount()
    {
        var line = L(D(3, 10), -40m);
        Assert.Equal([new WantedFlow($"bank:{line.Id}", D(3, 10), FlowKind.Withdrawal, 40m, null, "VIR Alice")], Plan([line]));
    }

    [Fact]
    public void An_interest_credit_is_income_only()
    {
        var line = L(D(3, 10), 12.5m, 12.5m);
        Assert.Equal([new WantedFlow($"bank:{line.Id}:interest", D(3, 10), FlowKind.Income, 12.5m, "interest", "VIR Alice")], Plan([line]));
    }

    [Fact]
    public void A_split_line_gives_a_movement_and_an_interest_flow()
    {
        var line = L(D(3, 10), 112.5m, 12.5m);
        Assert.Equal(
            [
                new WantedFlow($"bank:{line.Id}", D(3, 10), FlowKind.Contribution, 100m, null, "VIR Alice"),
                new WantedFlow($"bank:{line.Id}:interest", D(3, 10), FlowKind.Income, 12.5m, "interest", "VIR Alice"),
            ],
            Plan([line]));
    }

    [Fact]
    public void A_line_before_the_association_month_gives_nothing()
    {
        var associatedOn = D(3, 20);
        Assert.Empty(Plan([L(D(2, 28), 100m)], associatedOn: associatedOn));
        Assert.Single(Plan([L(D(3, 1), 100m)], associatedOn: associatedOn));
    }

    [Fact]
    public void A_line_in_another_currency_gives_nothing()
        => Assert.Empty(Plan([L(D(3, 10), 100m, currency: "USD")]));

    [Fact]
    public void A_long_label_is_trimmed_to_the_note_limit()
    {
        var line = L(D(3, 10), 100m) with { Label = new string('x', 2500) };
        Assert.Equal(2000, Plan([line])[0].Note.Length);
    }

    [Fact]
    public void A_typed_flow_absorbs_the_nearest_matching_line()
    {
        var typed = new TypedFlow(Guid.NewGuid(), D(3, 10), FlowKind.Contribution, 50m);
        var near = L(D(3, 9), 50m);
        var far = L(D(4, 9), 50m);
        var plan = Plan([near, far], [typed]);
        Assert.Equal([$"bank:{far.Id}"], plan.Select(f => f.Ref));
    }

    [Fact]
    public void A_typed_flow_absorbs_the_nearest_of_two_within_the_window()
    {
        var typed = new TypedFlow(Guid.NewGuid(), D(3, 10), FlowKind.Contribution, 50m);
        var far = L(D(3, 13), 50m);
        var near = L(D(3, 11), 50m);
        Assert.Equal([$"bank:{far.Id}"], Plan([far, near], [typed]).Select(f => f.Ref));
    }

    [Fact]
    public void A_typed_flow_absorbs_one_line_only()
    {
        var typed = new TypedFlow(Guid.NewGuid(), D(3, 10), FlowKind.Contribution, 50m);
        var first = L(D(3, 10), 50m);
        var second = L(D(3, 11), 50m);
        Assert.Equal([$"bank:{second.Id}"], Plan([first, second], [typed]).Select(f => f.Ref));
    }

    [Fact]
    public void Interest_is_never_absorbed()
    {
        var typed = new TypedFlow(Guid.NewGuid(), D(3, 10), FlowKind.Income, 12.5m);
        var line = L(D(3, 10), 12.5m, 12.5m);
        Assert.Equal([$"bank:{line.Id}:interest"], Plan([line], [typed]).Select(f => f.Ref));
    }

    [Fact]
    public void The_movement_of_a_split_line_is_absorbed_and_its_interest_stays()
    {
        var typed = new TypedFlow(Guid.NewGuid(), D(3, 10), FlowKind.Contribution, 100m);
        var line = L(D(3, 10), 112.5m, 12.5m);
        Assert.Equal([$"bank:{line.Id}:interest"], Plan([line], [typed]).Select(f => f.Ref));
    }

    [Fact]
    public void Absorption_ignores_other_kinds_and_amounts_and_days_beyond_three()
    {
        var line = L(D(3, 10), 50m);
        Assert.Single(Plan([line], [new TypedFlow(Guid.NewGuid(), D(3, 10), FlowKind.Withdrawal, 50m)]));
        Assert.Single(Plan([line], [new TypedFlow(Guid.NewGuid(), D(3, 10), FlowKind.Contribution, 60m)]));
        Assert.Single(Plan([line], [new TypedFlow(Guid.NewGuid(), D(3, 14), FlowKind.Contribution, 50m)]));
        Assert.Empty(Plan([line], [new TypedFlow(Guid.NewGuid(), D(3, 13), FlowKind.Contribution, 50m)]));
    }

    [Fact]
    public void Equal_gaps_give_the_same_outcome_in_any_input_order()
    {
        var a = new TypedFlow(Guid.NewGuid(), D(3, 9), FlowKind.Contribution, 50m);
        var b = new TypedFlow(Guid.NewGuid(), D(3, 11), FlowKind.Contribution, 50m);
        var early = L(D(3, 10), 50m);
        Assert.Empty(Plan([early], [a, b]));
        Assert.Empty(Plan([early], [b, a]));
        var x = L(D(3, 9), 50m);
        var y = L(D(3, 11), 50m);
        var typed = new TypedFlow(Guid.NewGuid(), D(3, 10), FlowKind.Contribution, 50m);
        var expected = Plan([x, y], [typed]).Select(f => f.Ref).ToList();
        Assert.Single(expected);
        Assert.Equal(expected, Plan([y, x], [typed]).Select(f => f.Ref));
    }

    [Fact]
    public void A_zero_movement_line_gives_only_its_interest()
    {
        var line = L(D(3, 10), 12.5m, 12.5m);
        Assert.Equal([FlowKind.Income], Plan([line]).Select(f => f.Kind));
    }

    [Fact]
    public void A_debit_is_absorbed_by_a_typed_withdrawal()
        => Assert.Empty(Plan([L(D(3, 10), -40m)], [new TypedFlow(Guid.NewGuid(), D(3, 11), FlowKind.Withdrawal, 40m)]));
}
