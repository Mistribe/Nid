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
    /// refused on <c>modules</c>. The feed (#243) and the calendar page (#239) check them alike.
    /// </summary>
    public static Failure? Modules(IReadOnlyList<string>? modules, out IReadOnlyList<string>? chosen)
    {
        chosen = null;
        if (modules is not null && (modules.Count == 0 || modules.Any(m => !CalendarModules.All.Contains(m))))
            return FieldErrors.At(new Failure(TokenCodes.CalendarModulesInvalid,
                $"A calendar lists one or more modules among: {string.Join(", ", CalendarModules.All)}.", null), "modules");
        var listed = modules is null ? null : CalendarModules.All.Where(modules.Contains).ToList();
        chosen = listed?.Count == CalendarModules.All.Count ? null : listed;
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
/// of <c>Modules</c> (null: every module).
/// </summary>
public sealed record GetCalendarQuery(VisibilityScope Scope, LocalDate From, LocalDate To, IReadOnlyList<string>? Modules) : IQuery<CalendarView>;

public sealed record CalendarView(LocalDate From, LocalDate To, IReadOnlyList<DashboardEventView> Events) : IExposesItems
{
    public IEnumerable<ItemId> ExposedItems() => Events.Select(e => e.Id).OfType<ItemId>();
}

[RequestHandler<GetCalendarQuery, CalendarView>]
public sealed class GetCalendarHandler(DashboardReader dashboard, IActorAccessor accessor, IHouseholdCalendar calendar)
    : IRequestHandler<GetCalendarQuery, CalendarView>
{
    /// <summary>Two months at most: a month shown, and its neighbour's days on the grid, stay cheap to compute.</summary>
    public const int MaxDays = 62;

    public async ValueTask<Result<CalendarView>> HandleAsync(GetCalendarQuery query, CancellationToken ct = default)
    {
        var reader = accessor.Current!.PersonId!.Value;
        if (CalendarFeeds.Modules(query.Modules, out var chosen) is { } refused)
            return Result.Failure<CalendarView>(refused);
        // The readers are not built for past dates: the calendar starts at the current month.
        var today = await calendar.TodayAsync(ct);
        if (query.From < today.With(DateAdjusters.StartOfMonth))
            return Refused("from", "The calendar starts on the first day of the current month.");
        if (query.To < query.From || Period.DaysBetween(query.From, query.To) > MaxDays)
            return Refused("to", $"The calendar covers at most {MaxDays} days from its first day.");
        // Read from the first day asked, not from today: the current month's days already past show too.
        return (await dashboard.UpcomingAsync(reader, query.Scope, query.From, query.To, chosen, ct))
            .Map(events => new CalendarView(query.From, query.To, events));
    }

    private static Result<CalendarView> Refused(string field, string message)
        => Result.Failure<CalendarView>(FieldErrors.At(new Failure(TokenCodes.CalendarRangeInvalid, message, null), field));
}
