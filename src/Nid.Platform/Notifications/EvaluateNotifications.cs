using Microsoft.Extensions.Logging;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Platform.Notifications;

/// <summary>
/// Evaluates the sources for every adult, or for <paramref name="Only"/> (the refresh). Without <paramref name="Force"/> a person
/// is evaluated once a day, from 06:00 on the household's clock (notifications design §3.3).
/// </summary>
public sealed record EvaluateNotificationsCommand(PersonId? Only = null, bool Force = false) : ICommand<EvaluationSummary>, IAdminOnly;

/// <summary>How many adults the run evaluated (the others were not due).</summary>
public sealed record EvaluationSummary(int Evaluated);

[RequestHandler<EvaluateNotificationsCommand, EvaluationSummary>]
public sealed partial class EvaluateNotificationsHandler(
    IPersonRegistry persons, IEnumerable<INotificationSource> sources, NotificationCatalog catalog, INotificationStore store,
    IHouseholdCalendar calendar, IClock clock, IActorAccessor accessor, IDeliveryOutbox outbox, IUnitOfWork unitOfWork, ILogger<EvaluateNotificationsHandler> logger) : IRequestHandler<EvaluateNotificationsCommand, EvaluationSummary>
{
    private const int FirstHour = 6;
    private const int HistoryDays = 90;

    public async ValueTask<Result<EvaluationSummary>> HandleAsync(EvaluateNotificationsCommand command, CancellationToken ct = default)
    {
        _ = catalog.Kinds; // a kind declared twice fails here, before anything is written
        var now = await calendar.NowAsync(ct);
        var today = now.Date;

        var adults = (await persons.ListAsync(ct)).Where(p => p.Kind == PersonKind.Adult && !p.IsPending);
        if (command.Only is { } only)
            adults = adults.Where(p => p.Id == only);

        var evaluated = 0;
        foreach (var adult in adults)
        {
            if (!command.Force && !await IsDueAsync(adult.Id, now, today, ct))
                continue;
            var prefs = await PreferencesOrNullAsync(adult.Id, ct);
            if (prefs is null)
                continue; // not evaluated on defaults: it would retime their rows to the wrong dates
            evaluated++;
            foreach (var source in sources)
                await EvaluateIsolatedAsync(source, adult.Id, prefs, today, ct);
        }

        await store.PurgeAsync(clock.GetCurrentInstant() - NodaTime.Duration.FromDays(HistoryDays), ct);
        return Result.Success(new EvaluationSummary(evaluated));
    }

    /// <summary>The person's preferences, read once for all the sources; a store failure is logged and skips the person until the next run.</summary>
    private async Task<NotificationPreferences?> PreferencesOrNullAsync(PersonId person, CancellationToken ct)
    {
        try
        {
            return await store.PreferencesAsync(person, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            LogStoreFailed(e, "preferences", person.Value);
            return null;
        }
    }

    private async Task<bool> IsDueAsync(PersonId person, LocalDateTime now, LocalDate today, CancellationToken ct)
    {
        if (now.Hour < FirstHour)
            return false;
        var last = await store.LastEvaluatedAsync(person, ct);
        return last is null || last.Value < today;
    }

    /// <summary>A store failure for one person and source is logged and recorded; the next source and person go on.</summary>
    private async Task EvaluateIsolatedAsync(INotificationSource source, PersonId person, NotificationPreferences prefs, LocalDate today, CancellationToken ct)
    {
        try
        {
            await EvaluateAsync(source, person, prefs, today, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            LogStoreFailed(e, source.Name, person.Value);
            try
            {
                await store.RecordRunAsync(person, source.Name, today, false, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception recording)
            {
                LogStoreFailed(recording, source.Name, person.Value);
            }
        }
    }

    private async Task EvaluateAsync(INotificationSource source, PersonId person, NotificationPreferences prefs, LocalDate today, CancellationToken ct)
    {
        var applied = source.Kinds.ToDictionary(k => k.Kind, k => prefs.Apply(k)); // the person's own lead times
        var horizon = today.PlusDays(applied.Values.Select(k => k.LeadDays ?? 0).DefaultIfEmpty(0).Max());
        IReadOnlyList<Signal> signals;
        try
        {
            Result<IReadOnlyList<Signal>> result;
            var saved = accessor.Current;
            accessor.Current = Actor.Notifier(person); // the sources read as that adult, never more
            try
            {
                result = await source.SignalsAsync(person, today, horizon, ct);
            }
            finally
            {
                accessor.Current = saved;
            }
            if (result.TryGetFailure(out var failure))
            {
                LogSourceFailed(source.Name, person.Value, failure!.Code);
                await store.RecordRunAsync(person, source.Name, today, false, ct);
                return;
            }

            result.TryGetValue(out var value);
            signals = value!;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            LogSourceCrashed(e, source.Name, person.Value);
            await store.RecordRunAsync(person, source.Name, today, false, ct);
            return;
        }

        var kinds = applied;
        var known = new List<Signal>();
        foreach (var signal in signals)
        {
            if (kinds.ContainsKey(signal.Kind))
                known.Add(signal);
            else
                LogUnknownKind(source.Name, signal.Kind);
        }

        var existing = await store.ForSourceAsync(person, kinds.Keys, ct);

        // One transaction: a row is never open without its deliveries. A failure rolls the whole (person, source) back, the
        // caller records the run as failed and the next evaluation redoes it (idempotent).
        await using var scope = await unitOfWork.BeginAsync(ct);
        var opened = await store.ApplyAsync(person, NotificationLifecycle.Plan(existing, known, kinds, today), ct);
        if (opened.Count > 0)
            await outbox.EnqueueAsync(person, opened, prefs, ct);
        await store.RecordRunAsync(person, source.Name, today, true, ct);
        await scope.CommitAsync(ct);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Notification source {Source} failed for person {Person}: {Code}")]
    private partial void LogSourceFailed(string source, Guid person, string code);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Notification source {Source} crashed for person {Person}")]
    private partial void LogSourceCrashed(Exception exception, string source, Guid person);

    [LoggerMessage(Level = LogLevel.Error, Message = "Notification store failed for source {Source} and person {Person}")]
    private partial void LogStoreFailed(Exception exception, string source, Guid person);

    [LoggerMessage(Level = LogLevel.Error, Message = "Notification source {Source} signalled the undeclared kind {Kind}; dropped")]
    private partial void LogUnknownKind(string source, string kind);
}
