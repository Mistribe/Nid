using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Platform;

/// <summary>Why the guard withheld a response (#28): the journal and the log say the same.</summary>
public static class AccessViolationReasons
{
    public const string NoActor = "no_actor";
    public const string VisibilityLookupFailed = "visibility_lookup_failed";
    public const string OutsideVisibleSet = "outside_visible_set";
}

/// <param name="Reason">One of <see cref="AccessViolationReasons"/>.</param>
/// <param name="Error">What the visible-set lookup threw; logged, and only its type reaches the event.</param>
public sealed record AccessViolationCause(string Reason, Exception? Error = null);

/// <summary>Where an access violation is recorded: the log and the audit (1F3a); every adult is notified (spec U14, lifted).</summary>
public interface IAccessViolations
{
    ValueTask RecordAsync(string request, PersonId actor, IReadOnlyCollection<ItemId> leaked, AccessViolationCause cause, CancellationToken ct);
}

/// <summary>Logs the violation without ever logging the leaked item's contents.</summary>
public sealed partial class LoggingAccessViolations(ILogger<LoggingAccessViolations> logger) : IAccessViolations
{
    public ValueTask RecordAsync(string request, PersonId actor, IReadOnlyCollection<ItemId> leaked, AccessViolationCause cause, CancellationToken ct)
    {
        if (cause.Error is { } error)
            LogLookupFailed(error, request);
        LogViolation(request, leaked.Count, cause.Reason);
        return ValueTask.CompletedTask;
    }

    [LoggerMessage(Level = LogLevel.Critical, EventId = 4001,
        Message = "access_violation: {Request} withheld with {Count} item(s), reason {Reason}")]
    private partial void LogViolation(string request, int count, string reason);

    [LoggerMessage(Level = LogLevel.Error, EventId = 4002,
        Message = "access_violation: the visible set could not be read for {Request}")]
    private partial void LogLookupFailed(Exception exception, string request);
}

/// <summary>
/// 1F3a: the violation is logged (Critical, whatever happens), recorded as a security event and told to every adult (no item id: a
/// hidden row would hide the alert). One notice per request and household day: a looping client floods the journal, not the adults.
/// </summary>
public sealed partial class AuditedAccessViolations(
    LoggingAccessViolations log, ISecurityEvents events, INotifier notifier, IServiceScopeFactory scopes, IClock clock,
    ILogger<AuditedAccessViolations> logger) : IAccessViolations
{
    private static readonly TimeSpan CalendarBound = TimeSpan.FromSeconds(5);

    public async ValueTask RecordAsync(string request, PersonId actor, IReadOnlyCollection<ItemId> leaked, AccessViolationCause cause, CancellationToken ct)
    {
        await log.RecordAsync(request, actor, leaked, cause, ct);
        var data = new Dictionary<string, string>
        {
            ["request"] = request,
            ["reason"] = cause.Reason,
            ["count"] = leaked.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        // The type only: an exception's message may carry data.
        if (cause.Error is { } error)
            data["error"] = error.GetType().Name;
        await events.RecordAsync(new SecurityEvent(SecurityEventNames.AccessViolation, actor == default ? null : actor, leaked.ToList(), data, Guid.CreateVersion7()), ct);
        var day = (await HouseholdDayAsync()).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        await notifier.PublishAsync(new Notice("access_violation", new Recipients.AllAdults(), $"access_violation:{request}:{day}", [], new Dictionary<string, string> { ["request"] = request }), ct);
    }

    /// <summary>
    /// The household's day, read in a scope of its own and without the caller's token: the request's context or token may be what
    /// failed, and the notice must still go out. If the read fails, the UTC day.
    /// </summary>
    private async Task<LocalDate> HouseholdDayAsync()
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            using var bound = new CancellationTokenSource(CalendarBound);
            return await scope.ServiceProvider.GetRequiredService<IHouseholdCalendar>().TodayAsync(bound.Token);
        }
        catch (Exception e)
        {
            LogNoCalendar(e);
            return clock.GetCurrentInstant().InUtc().Date;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The household's day could not be read for an access violation; the UTC day keys it.")]
    private partial void LogNoCalendar(Exception exception);
}

/// <summary>
/// The last line of defence (<c>architecture.md</c> D15, <c>access-tests.md</c> §5). A response implementing
/// <see cref="IExposesItems"/> may only carry items inside <see cref="VisibilityScope.Me"/> — the widest set
/// the actor may legitimately see. Anything else is refused with no payload. It fails closed: if the visible
/// set cannot be determined, the response does not go out.
/// </summary>
public sealed class OutputGuardBehavior<TRequest, TResponse>(
    IActorAccessor accessor, IVisibleItems visible, IAccessViolations violations)
    : IRequestPipelineBehavior<TRequest, TResponse>, IOrderedPipelineBehavior
    where TRequest : IRequest<TResponse>
    where TResponse : notnull
{
    public uint Order => PipelineOrder.OutputGuard;

    public async ValueTask<Result<TResponse>> HandleAsync(
        TRequest request, RequestHandlerDelegate<TRequest, TResponse> next, CancellationToken cancellationToken = default)
    {
        var result = await next(request, cancellationToken);

        if (!result.TryGetValue(out var response) || response is not IExposesItems exposing)
            return result;

        var exposed = exposing.ExposedItems().ToList();
        if (exposed.Count == 0)
            return result;

        // A system actor has no PersonId, so it has no visible-item set to prove a response carrying items is
        // legitimate. Uncertainty is a refusal here too, not a pass — the same principle as the lookup throwing
        // below. (No system-actor request exposes items today: Actor.System is set only on the OIDC callback,
        // which returns PersonInfo. But the pass-through would silently bypass the guard the day one does.)
        if (accessor.Current?.PersonId is not { } actor)
        {
            await violations.RecordAsync(typeof(TRequest).Name, default, exposed, new AccessViolationCause(AccessViolationReasons.NoActor), cancellationToken);
            return Violation();
        }

        IReadOnlySet<ItemId> allowed;
        try
        {
            allowed = await visible.ForAsync(actor, VisibilityScope.Me, includeArchived: true, cancellationToken);
        }
        catch (Exception e)
        {
            await violations.RecordAsync(typeof(TRequest).Name, actor, exposed, new AccessViolationCause(AccessViolationReasons.VisibilityLookupFailed, e), cancellationToken);
            return Violation();
        }

        var leaked = exposed.Where(id => !allowed.Contains(id)).ToList();
        if (leaked.Count == 0)
            return result;

        await violations.RecordAsync(typeof(TRequest).Name, actor, leaked, new AccessViolationCause(AccessViolationReasons.OutsideVisibleSet), cancellationToken);
        return Violation();
    }

    private static Result<TResponse> Violation()
        => Result.Failure<TResponse>(new Failure(
            "core.access_violation", "The response was withheld.", null));
}
