using Microsoft.Extensions.Logging;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Platform;

/// <summary>Where an access violation is recorded. 1F routes this to the audit log and an alert.</summary>
public interface IAccessViolations
{
    ValueTask RecordAsync(string request, PersonId actor, IReadOnlyCollection<ItemId> leaked, CancellationToken ct);
}

/// <summary>Logs the violation without ever logging the leaked item's contents.</summary>
public sealed partial class LoggingAccessViolations(ILogger<LoggingAccessViolations> logger) : IAccessViolations
{
    public ValueTask RecordAsync(string request, PersonId actor, IReadOnlyCollection<ItemId> leaked, CancellationToken ct)
    {
        LogViolation(request, leaked.Count);
        return ValueTask.CompletedTask;
    }

    [LoggerMessage(Level = LogLevel.Critical, EventId = 4001,
        Message = "access_violation: {Request} exposed {Count} item(s) outside the actor's visible set")]
    private partial void LogViolation(string request, int count);
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
            await violations.RecordAsync(typeof(TRequest).Name, default, exposed, cancellationToken);
            return Violation();
        }

        IReadOnlySet<ItemId> allowed;
        try
        {
            allowed = await visible.ForAsync(actor, VisibilityScope.Me, cancellationToken);
        }
        catch (Exception)
        {
            await violations.RecordAsync(typeof(TRequest).Name, actor, exposed, cancellationToken);
            return Violation();
        }

        var leaked = exposed.Where(id => !allowed.Contains(id)).ToList();
        if (leaked.Count == 0)
            return result;

        await violations.RecordAsync(typeof(TRequest).Name, actor, leaked, cancellationToken);
        return Violation();
    }

    private static Result<TResponse> Violation()
        => Result.Failure<TResponse>(new Failure(
            "core.access_violation", "The response was withheld.", null));
}
