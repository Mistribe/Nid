using Nid.Ai.Contracts;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Ai.Application;

/// <summary>A month of the household's budget for paid providers (#488, <c>docs/ingestion/llm-providers.md</c> §7): its
/// ceiling, what was spent, what open calls hold back. In euros.</summary>
public sealed record BudgetState(LocalDate Month, decimal Cap, decimal Spent, decimal Reserved);

/// <summary>What a settled call did to the month: the spend before and after it, against the ceiling.</summary>
public sealed record BudgetSettlement(LocalDate Month, decimal SpentBefore, decimal SpentAfter, decimal Cap);

/// <summary>
/// The budget's counters (§7): a reservation is one atomic statement, so concurrent calls never pass the ceiling together;
/// a call settles on what the provider reports, or on what it held back when the stream was cut.
/// </summary>
public interface IBudget
{
    Task<decimal> CapAsync(CancellationToken ct);

    Task SetCapAsync(decimal cap, CancellationToken ct);

    Task<BudgetState> StateAsync(LocalDate month, CancellationToken ct);

    /// <summary>The reservation, or null when it would pass the ceiling: nothing is sent.</summary>
    Task<Guid?> ReserveAsync(LocalDate month, decimal amount, Instant expires, CancellationToken ct);

    Task<BudgetSettlement?> SettleAsync(Guid reservation, decimal actual, CancellationToken ct);

    /// <summary>Reservations past their expiry, settled at what they held back.</summary>
    Task<IReadOnlyList<BudgetSettlement>> SettleExpiredAsync(Instant now, CancellationToken ct);
}

/// <summary>A paid call's price in euros, from its profile's prices and Nid's exchange rates; none without a rate.</summary>
public sealed class BudgetPrices(IFxRates fx, IClock clock)
{
    public async Task<decimal?> EurAsync(Pricing pricing, long tokensIn, long tokensOut, CancellationToken ct)
    {
        var amount = (tokensIn * pricing.InputPerMillion + tokensOut * pricing.OutputPerMillion) / 1_000_000m;
        if (!Currency.TryCreate(pricing.Currency, out var currency))
            return null;
        if (currency == Currency.Eur)
            return Math.Round(amount, 6);
        var today = clock.GetCurrentInstant().InUtc().Date;
        // USD is the pivot and is never loaded: EUR's own series is what converts a USD price (#621).
        var rates = await fx.LoadAsync([currency, Currency.Eur], today, today, ct);
        return rates.TryConvert(amount, currency, Currency.Eur, today, projected: false, out var eur, out _) ? Math.Round(eur, 6) : null;
    }

    /// <summary>The most a turn can cost (§7): every input byte counted as a token, the whole output allowance.</summary>
    public Task<decimal?> UpperBoundAsync(Pricing pricing, ChatRequestInputSize input, CancellationToken ct)
        => EurAsync(pricing, input.Bytes, input.MaxTokens, ct);
}

/// <summary>A turn's size for its upper bound: its UTF-8 bytes and its output allowance.</summary>
public readonly record struct ChatRequestInputSize(long Bytes, int MaxTokens);

public static class BudgetMonths
{
    /// <summary>The budget runs by calendar month, in UTC; a block lasts until the 1st of the next one.</summary>
    public static LocalDate Of(Instant at) => at.InUtc().Date.With(DateAdjusters.StartOfMonth);

    /// <summary>A reservation an answer never settled is settled at its amount after this.</summary>
    public static readonly NodaTime.Duration ReservationFor = NodaTime.Duration.FromMinutes(15);
}

/// <summary>Tells both adults when the month's spend crosses 80 % and when it reaches the ceiling: once each per month.</summary>
public sealed class BudgetNotices(INotifier notifier)
{
    public const string Alert = "ai.budget_alert";
    public const string Reached = "ai.budget_reached";
    public const string Changed = "ai.budget_changed";

    public async Task AfterAsync(BudgetSettlement settled, CancellationToken ct)
    {
        if (settled.Cap <= 0)
            return;
        var month = settled.Month.ToString("yyyy-MM", null);
        var threshold = settled.Cap * 0.8m;
        if (settled.SpentBefore < threshold && settled.SpentAfter >= threshold && settled.SpentAfter < settled.Cap)
            await notifier.PublishAsync(new Notice(Alert, new Recipients.AllAdults(), $"{Alert}:{month}", [], new Dictionary<string, string>()), ct);
        if (settled.SpentBefore < settled.Cap && settled.SpentAfter >= settled.Cap)
            await notifier.PublishAsync(new Notice(Reached, new Recipients.AllAdults(), $"{Reached}:{month}", [], new Dictionary<string, string>()), ct);
    }
}

/// <summary>The month's budget for paid providers, for any adult (§7, decision F).</summary>
public sealed record GetBudgetQuery : IQuery<AiBudgetView>;

/// <summary>The household's monthly ceiling for paid providers, 0 € by default: an admin sets it (#585); every adult is told.</summary>
public sealed record SetMonthlyBudgetCommand(decimal AmountEur) : ICommand<AiBudgetView>, IAdminOnly, IBrowserOnly;

internal static class BudgetViews
{
    /// <summary>Euros to the cent, the way the page shows them (a fraction of a cent spent reads as 0.00).</summary>
    private static string Eur(decimal amount) => Math.Round(amount, 2).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);

    public static async Task<AiBudgetView> ReadAsync(IBudget budget, IClock clock, CancellationToken ct)
    {
        var state = await budget.StateAsync(BudgetMonths.Of(clock.GetCurrentInstant()), ct);
        var status = state.Cap <= 0 ? BudgetStatus.Off
            : state.Spent >= state.Cap ? BudgetStatus.Reached
            : state.Spent >= state.Cap * 0.8m ? BudgetStatus.Alert
            : BudgetStatus.Ok;
        return new AiBudgetView(Eur(state.Cap), state.Month, Eur(state.Spent), Eur(state.Reserved), Eur(Math.Max(0, state.Cap - state.Spent - state.Reserved)), status);
    }
}

[RequestHandler<GetBudgetQuery, AiBudgetView>]
public sealed class GetBudgetHandler(IBudget budget, IClock clock) : IRequestHandler<GetBudgetQuery, AiBudgetView>
{
    public async ValueTask<Result<AiBudgetView>> HandleAsync(GetBudgetQuery query, CancellationToken ct = default)
        => Result.Success(await BudgetViews.ReadAsync(budget, clock, ct));
}

[RequestHandler<SetMonthlyBudgetCommand, AiBudgetView>]
public sealed class SetMonthlyBudgetHandler(IBudget budget, INotifier notifier, IClock clock) : IRequestHandler<SetMonthlyBudgetCommand, AiBudgetView>
{
    public const decimal Max = 100_000m;

    public async ValueTask<Result<AiBudgetView>> HandleAsync(SetMonthlyBudgetCommand command, CancellationToken ct = default)
    {
        if (command.AmountEur is < 0 or > Max || decimal.Round(command.AmountEur, 2) != command.AmountEur)
            return Result.Failure<AiBudgetView>(new Failure(JobCodes.InvalidBudget, "0 to 100 000 €, in cents.", null));
        if (await budget.CapAsync(ct) != command.AmountEur)
        {
            await budget.SetCapAsync(command.AmountEur, ct);
            var now = clock.GetCurrentInstant();
            await notifier.PublishAsync(new Notice(BudgetNotices.Changed, new Recipients.AllAdults(), $"{BudgetNotices.Changed}:{now.ToUnixTimeTicks()}", [],
                new Dictionary<string, string>()), ct);
        }

        return Result.Success(await BudgetViews.ReadAsync(budget, clock, ct));
    }
}
