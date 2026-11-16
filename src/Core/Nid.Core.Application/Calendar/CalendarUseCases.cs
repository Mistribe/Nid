using System.Text.Json.Serialization;
using Nid.Core.Application.Dashboard;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Core.Application.Calendar;

/// <summary>
/// Issue #80: the person's calendar feed as Réglages › Calendrier shows it: its token row and its options, never its secret.
/// Issue #243: <c>Modules</c> lists what the feed lists, every module when none was chosen.
/// </summary>
public sealed record CalendarFeedView(TokenView Token, string Scope, bool Amounts, IReadOnlyList<string> Modules);

/// <summary>Null <c>Feed</c>: the person has no active feed.</summary>
public sealed record MyCalendarView(CalendarFeedView? Feed);

/// <summary>The only response that carries the feed's secret; <see cref="PersonId"/> is the security event's subject and stays off the wire.</summary>
public sealed record CreatedCalendarFeedView(CalendarFeedView Feed, string Secret, [property: JsonIgnore] Guid PersonId);

public sealed record GetMyCalendarQuery : IQuery<MyCalendarView>, IBrowserOnly;

/// <summary>Creates the person's feed, or regenerates it: the active one is revoked in the same transaction.</summary>
public sealed record CreateCalendarFeedCommand(string? Scope, bool Amounts, IReadOnlyList<string>? Modules = null)
    : ICommand<CreatedCalendarFeedView>, IBrowserOnly, IRecordsSecurityEvent
{
    public SecurityEvent EventFor(object response)
    {
        var created = (CreatedCalendarFeedView)response;
        return new SecurityEvent(SecurityEventNames.TokenCreated, new PersonId(created.PersonId), [],
            new Dictionary<string, string> { ["token"] = created.Feed.Token.Id.ToString(), ["preset"] = TokenPresets.Calendar });
    }
}

/// <summary>Changes what the active feed lists; its URL stays.</summary>
public sealed record UpdateCalendarFeedCommand(string? Scope, bool Amounts, IReadOnlyList<string>? Modules = null) : ICommand<CalendarFeedView>, IBrowserOnly;

/// <summary>What the feed route reads, as the feed's person (<see cref="Actor.Token"/> with <c>read</c>), in the feed's options.</summary>
public sealed record GetCalendarFeedQuery(CalendarFeedOptions Options) : IQuery<CalendarFeedEvents>;

public sealed record CalendarFeedEvents(IReadOnlyList<DashboardEventView> Events, LocalDate Today) : IExposesItems
{
    public IEnumerable<ItemId> ExposedItems() => Events.Select(e => e.Id).OfType<ItemId>();
}

public static class CalendarFeeds
{
    public const string Household = "household";
    public const string Me = "me";

    /// <summary>Null modules, or all of them, is stored as null: every module, one added later included.</summary>
    public static Result<CalendarFeedOptions> Options(string? scope, bool amounts, IReadOnlyList<string>? modules = null)
    {
        if (Modules(modules, out var chosen) is { } refused)
            return Result.Failure<CalendarFeedOptions>(refused);
        return scope switch
        {
            Household => Result.Success(new CalendarFeedOptions(VisibilityScope.Household, amounts, chosen)),
            Me => Result.Success(new CalendarFeedOptions(VisibilityScope.Me, amounts, chosen)),
            _ => Result.Failure<CalendarFeedOptions>(FieldErrors.At(
                new Failure(TokenCodes.CalendarScopeInvalid, "A calendar feed's scope is household or me.", null), "scope")),
        };
    }

    /// <summary>
    /// The chosen modules in display order, null for every one (none chosen, or all of them); an empty list or an unknown module is
    /// refused on <c>modules</c>. The feed (#243) and the calendar page (#239) check them alike. Issue #241: the page with its amounts
    /// checks them against <paramref name="known"/>, <see cref="CalendarModules.WithAmounts"/>.
    /// </summary>
    public static Failure? Modules(IReadOnlyList<string>? modules, out IReadOnlyList<string>? chosen, IReadOnlyList<string>? known = null)
    {
        known ??= CalendarModules.All;
        chosen = null;
        if (modules is not null && (modules.Count == 0 || modules.Any(m => !known.Contains(m))))
            return FieldErrors.At(new Failure(TokenCodes.CalendarModulesInvalid,
                $"A calendar lists one or more modules among: {string.Join(", ", known)}.", null), "modules");
        var listed = modules is null ? null : known.Where(modules.Contains).ToList();
        chosen = listed?.Count == known.Count ? null : listed;
        return null;
    }

    public static CalendarFeedView? View(PersonalAccessToken token, Instant now)
        => token.Calendar is { } options
            ? new CalendarFeedView(TokenView.From(token, now), options.Scope == VisibilityScope.Me ? Me : Household, options.Amounts,
                options.Modules ?? CalendarModules.All)
            : null;
}

[RequestHandler<GetMyCalendarQuery, MyCalendarView>]
public sealed class GetMyCalendarHandler(IPersonalAccessTokens tokens, IActorAccessor accessor, IClock clock)
    : IRequestHandler<GetMyCalendarQuery, MyCalendarView>
{
    public async ValueTask<Result<MyCalendarView>> HandleAsync(GetMyCalendarQuery query, CancellationToken ct = default)
    {
        if (accessor.Current?.PersonId is not { } me)
            return Result.Failure<MyCalendarView>(new UnauthorizedFailure("Only a person has a calendar feed.", null));
        var now = clock.GetCurrentInstant();
        var active = (await tokens.ListAsync(me, ct)).FirstOrDefault(t => t.Calendar is not null && t.RevokedAt is null && t.ExpiresAt > now);
        return Result.Success(new MyCalendarView(active is null ? null : CalendarFeeds.View(active, now)));
    }
}

[RequestHandler<CreateCalendarFeedCommand, CreatedCalendarFeedView>]
public sealed class CreateCalendarFeedHandler(IPersonalAccessTokens tokens, IActorAccessor accessor, IClock clock)
    : IRequestHandler<CreateCalendarFeedCommand, CreatedCalendarFeedView>
{
    public async ValueTask<Result<CreatedCalendarFeedView>> HandleAsync(CreateCalendarFeedCommand command, CancellationToken ct = default)
    {
        if (accessor.Current?.PersonId is not { } me)
            return Result.Failure<CreatedCalendarFeedView>(new UnauthorizedFailure("Only a person has a calendar feed.", null));
        var options = CalendarFeeds.Options(command.Scope, command.Amounts, command.Modules);
        if (options.TryGetFailure(out var failure))
            return Result.Failure<CreatedCalendarFeedView>(failure!);
        options.TryGetValue(out var o);
        return (await tokens.CreateCalendarAsync(me, o!, ct))
            .Map(created => new CreatedCalendarFeedView(CalendarFeeds.View(created.Token, clock.GetCurrentInstant())!, created.Secret, me.Value));
    }
}

[RequestHandler<UpdateCalendarFeedCommand, CalendarFeedView>]
public sealed class UpdateCalendarFeedHandler(IPersonalAccessTokens tokens, IActorAccessor accessor, IClock clock)
    : IRequestHandler<UpdateCalendarFeedCommand, CalendarFeedView>
{
    public async ValueTask<Result<CalendarFeedView>> HandleAsync(UpdateCalendarFeedCommand command, CancellationToken ct = default)
    {
        if (accessor.Current?.PersonId is not { } me)
            return Result.Failure<CalendarFeedView>(new UnauthorizedFailure("Only a person has a calendar feed.", null));
        var options = CalendarFeeds.Options(command.Scope, command.Amounts, command.Modules);
        if (options.TryGetFailure(out var failure))
            return Result.Failure<CalendarFeedView>(failure!);
        options.TryGetValue(out var o);
        return (await tokens.SetCalendarOptionsAsync(me, o!, ct)).Map(token => CalendarFeeds.View(token, clock.GetCurrentInstant())!);
    }
}

[RequestHandler<GetCalendarFeedQuery, CalendarFeedEvents>]
public sealed class GetCalendarFeedHandler(DashboardReader dashboard, IActorAccessor accessor, IHouseholdCalendar calendar)
    : IRequestHandler<GetCalendarFeedQuery, CalendarFeedEvents>
{
    public async ValueTask<Result<CalendarFeedEvents>> HandleAsync(GetCalendarFeedQuery query, CancellationToken ct = default)
    {
        if (accessor.Current?.PersonId is not { } reader)
            return Result.Failure<CalendarFeedEvents>(new UnauthorizedFailure("Only a person has a calendar feed.", null));
        var today = await calendar.TodayAsync(ct);
        return (await dashboard.UpcomingAsync(reader, query.Options.Scope, today, today.PlusMonths(CalendarFeedLimits.Months), query.Options.Modules, ct))
            .Map(events => new CalendarFeedEvents(events, today));
    }
}

/// <summary>
/// Issue #239: the calendar page's events dated in [<c>From</c>, <c>To</c>] (« À venir » as the feed lists it, in the server's order),
/// of <c>Modules</c> (null: every module). Issue #241: <c>Amounts</c> adds the money expected in and out over the window, and accepts
/// <see cref="CalendarModules.Childcare"/> among the modules.
/// </summary>
public sealed record GetCalendarQuery(VisibilityScope Scope, LocalDate From, LocalDate To, IReadOnlyList<string>? Modules, bool Amounts = false)
    : IQuery<CalendarView>;

/// <summary>Issue #241: <c>Amounts</c> only when asked.</summary>
public sealed record CalendarView(LocalDate From, LocalDate To, IReadOnlyList<DashboardEventView> Events, CalendarAmountsView? Amounts = null) : IExposesItems
{
    public IEnumerable<ItemId> ExposedItems()
        => Events.Select(e => e.Id).OfType<ItemId>().Concat(Amounts?.Charges.Select(c => c.Id) ?? []);
}

/// <summary>
/// Issue #241: a debit (a credit: <c>Credit</c>, a rent) expected on an item, in its own currency and in the household's (<c>Converted</c>,
/// null with <c>MissingCurrency</c> when a rate is missing). <c>Kind</c> is one of <see cref="ChargeKinds.Wire"/>.
/// </summary>
public sealed record CalendarChargeView(
    LocalDate On, string Module, ItemId Id, string Name, string Kind, DashboardMoneyView Amount, DashboardMoneyView? Converted,
    string? MissingCurrency, bool Credit);

/// <summary>Issue #241: a day with a charge; <c>Out</c> or <c>In</c> null when nothing goes that way. <c>Heavy</c>: one of the window's three largest days out.</summary>
public sealed record CalendarDayView(LocalDate On, DashboardFigure? Out, DashboardFigure? In, bool Heavy);

/// <summary>Issue #241: the window's expected money, by day and in all, in the household currency; <c>Balance</c> is in minus out.</summary>
public sealed record CalendarAmountsView(
    string Currency, DashboardFigure Out, DashboardFigure In, DashboardFigure Balance, IReadOnlyList<CalendarDayView> Days,
    IReadOnlyList<CalendarChargeView> Charges);

public static class CalendarAmounts
{
    /// <summary>The days that stand out: the largest days out, never one whose total is unknown.</summary>
    public const int HeavyDays = 3;

    private static readonly StringComparer French = StringComparer.Create(System.Globalization.CultureInfo.GetCultureInfo("fr-FR"), true);

    /// <summary>Each figure at <paramref name="today"/>'s rate, as the dashboard's; a total with nothing in it is 0, not unknown.</summary>
    public static CalendarAmountsView Compose(IReadOnlyList<ExpectedCharge> charges, Currency reference, RateTable rates, LocalDate today)
    {
        DashboardMath.Sum Sum(IEnumerable<ExpectedCharge> some, bool signed = false)
            => DashboardMath.Sum.Of(some.GroupBy(c => c.Amount.Currency)
                .ToDictionary(g => g.Key, g => g.Sum(c => signed && !c.Credit ? -c.Amount.Amount : c.Amount.Amount)), reference, rates, today);
        DashboardFigure Figure(DashboardMath.Sum sum)
            => sum.PerCurrency.Count == 0 ? new DashboardFigure([], DashboardMath.Money(0m, reference), null) : sum.View(reference);

        var days = charges.GroupBy(c => c.DueOn).OrderBy(g => g.Key)
            .Select(g => (On: g.Key, Out: g.Any(c => !c.Credit) ? Sum(g.Where(c => !c.Credit)) : null, In: g.Any(c => c.Credit) ? Sum(g.Where(c => c.Credit)) : null))
            .ToList();
        var heavy = days.Where(d => d.Out?.Total is not null).OrderByDescending(d => d.Out!.Total).ThenBy(d => d.On)
            .Take(HeavyDays).Select(d => d.On).ToHashSet();
        return new CalendarAmountsView(
            reference.Code, Figure(Sum(charges.Where(c => !c.Credit))), Figure(Sum(charges.Where(c => c.Credit))), Figure(Sum(charges, signed: true)),
            days.Select(d => new CalendarDayView(d.On, d.Out?.View(reference), d.In?.View(reference), heavy.Contains(d.On))).ToList(),
            charges.OrderBy(c => c.DueOn).ThenBy(c => c.ItemName, French).ThenBy(c => c.Kind)
                .Select(c =>
                {
                    var (converted, missing) = DashboardMath.Convert(c.Amount, reference, rates, today);
                    return new CalendarChargeView(c.DueOn, ChargeKinds.ModuleOf(c.Kind), c.Item, c.ItemName, ChargeKinds.Wire(c.Kind),
                        DashboardMath.Money(c.Amount.Amount, c.Amount.Currency), converted is { } v ? DashboardMath.Money(v, reference) : null, missing, c.Credit);
                })
                .ToList());
    }
}

[RequestHandler<GetCalendarQuery, CalendarView>]
public sealed class GetCalendarHandler(
    DashboardReader dashboard, IActorAccessor accessor, IHouseholdCalendar calendar, IEnumerable<IExpectedCharges> expected,
    IHouseholdSettings household, IFxRates fx)
    : IRequestHandler<GetCalendarQuery, CalendarView>
{
    /// <summary>Two months at most: a month shown, and its neighbour's days on the grid, stay cheap to compute.</summary>
    public const int MaxDays = 62;

    public async ValueTask<Result<CalendarView>> HandleAsync(GetCalendarQuery query, CancellationToken ct = default)
    {
        var reader = accessor.Current!.PersonId!.Value;
        // Childcare has amounts only: asked without them, it is an unknown module.
        if (CalendarFeeds.Modules(query.Modules, out var chosen, query.Amounts ? CalendarModules.WithAmounts : CalendarModules.All) is { } refused)
            return Result.Failure<CalendarView>(refused);
        // The readers are not built for past dates: the calendar starts at the current month.
        var today = await calendar.TodayAsync(ct);
        if (query.From < today.With(DateAdjusters.StartOfMonth))
            return Refused("from", "The calendar starts on the first day of the current month.");
        if (query.To < query.From || Period.DaysBetween(query.From, query.To) > MaxDays)
            return Refused("to", $"The calendar covers at most {MaxDays} days from its first day.");
        // Read from the first day asked, not from today: the current month's days already past show too.
        var events = await dashboard.UpcomingAsync(reader, query.Scope, query.From, query.To, chosen, ct);
        if (!events.TryGetValue(out var listed) || !query.Amounts)
            return events.Map(e => new CalendarView(query.From, query.To, e));

        // Issue #241: the month's plan, its days already past included.
        var charges = new List<ExpectedCharge>();
        foreach (var source in expected)
        {
            var found = await source.ChargesAsync(reader, query.Scope, query.From, query.To, ct);
            if (found.TryGetFailure(out var failure))
                return Result.Failure<CalendarView>(failure!);
            found.TryGetValue(out var some);
            charges.AddRange(some!.Where(c => chosen is null || chosen.Contains(ChargeKinds.ModuleOf(c.Kind))));
        }
        var reference = await household.ReferenceCurrencyAsync(ct);
        var foreign = charges.Select(c => c.Amount.Currency).Where(c => c != reference).Distinct().ToList();
        // The reference's own quote is needed too: rates are per USD (RateTable converts through it).
        var rates = foreign.Count == 0 ? RateTable.Empty(today, 0) : await fx.LoadAsync([.. foreign, reference], today, today, ct);
        return Result.Success(new CalendarView(query.From, query.To, listed!, CalendarAmounts.Compose(charges, reference, rates, today)));
    }

    private static Result<CalendarView> Refused(string field, string message)
        => Result.Failure<CalendarView>(FieldErrors.At(new Failure(TokenCodes.CalendarRangeInvalid, message, null), field));
}

/// <summary>
/// Issue #240: the calendar's timeline, the milestones (<see cref="UpcomingKinds.IsMilestone"/>) of <c>Modules</c> (null: every module)
/// from today over <c>HorizonYears</c> (one of <see cref="GetCalendarTimelineHandler.Horizons"/>).
/// </summary>
public sealed record GetCalendarTimelineQuery(VisibilityScope Scope, int HorizonYears, IReadOnlyList<string>? Modules) : IQuery<CalendarView>;

[RequestHandler<GetCalendarTimelineQuery, CalendarView>]
public sealed class GetCalendarTimelineHandler(DashboardReader dashboard, IActorAccessor accessor, IHouseholdCalendar calendar)
    : IRequestHandler<GetCalendarTimelineQuery, CalendarView>
{
    public static readonly IReadOnlyList<int> Horizons = [1, 3, 5, 10];

    public async ValueTask<Result<CalendarView>> HandleAsync(GetCalendarTimelineQuery query, CancellationToken ct = default)
    {
        var reader = accessor.Current!.PersonId!.Value;
        if (CalendarFeeds.Modules(query.Modules, out var chosen) is { } refused)
            return Result.Failure<CalendarView>(refused);
        if (!Horizons.Contains(query.HorizonYears))
            return Result.Failure<CalendarView>(FieldErrors.At(new Failure(TokenCodes.CalendarRangeInvalid,
                $"The timeline looks {string.Join(", ", Horizons)} years ahead.", null), "horizon"));
        var today = await calendar.TodayAsync(ct);
        var until = today.PlusYears(query.HorizonYears);
        return (await dashboard.UpcomingAsync(reader, query.Scope, today, until, chosen, ct, milestonesOnly: true))
            .Map(events => new CalendarView(today, until, events));
    }
}
