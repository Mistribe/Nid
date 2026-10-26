using System.Threading.Channels;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.Platform.Notifications;
using NodaTime;
using Quartz;
using UnambitiousFx.Functional;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Host.Jobs;

public static class NotificationJobs
{
    public static readonly JobKey EvaluateKey = new("notifications-evaluate");
    public static readonly JobKey DispatchKey = new("notifications-dispatch");
    public static readonly JobKey DigestKey = new("notifications-digest");
}

/// <summary>
/// The minute-by-minute outbox: sends the messages that are due (notifications design §5.1). Runs as the system actor like
/// <see cref="NotificationsEvaluateJob"/>. Never throws.
/// </summary>
[DisallowConcurrentExecution]
public sealed partial class NotificationsDispatchJob(IServiceScopeFactory scopes, ILogger<NotificationsDispatchJob> logger) : IJob
{
    public async ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
            var result = await scope.ServiceProvider.GetRequiredService<IInvoker>().InvokeAsync(new DispatchNotificationsCommand(), cancellationToken);
            if (result.TryGetFailure(out var failure))
                LogRefused(failure!.Code);
            else
                await JobIncidents.SucceededAsync(scopes, context, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            LogInterrupted();
        }
        catch (Exception e)
        {
            LogCrashed(e);
            await JobIncidents.CrashedAsync(scopes, context, cancellationToken);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "notifications-dispatch was interrupted")]
    private partial void LogInterrupted();

    [LoggerMessage(Level = LogLevel.Information, Message = "notifications-dispatch did not run: {Code}")]
    private partial void LogRefused(string code);

    [LoggerMessage(Level = LogLevel.Error, Message = "notifications-dispatch crashed")]
    private partial void LogCrashed(Exception exception);
}

/// <summary>
/// The hourly trigger: every adult not yet evaluated today, from 06:00 on the household's clock. Sends the command as the system
/// actor like <c>irl-weekly</c>. Never throws.
/// </summary>
[DisallowConcurrentExecution]
public sealed partial class NotificationsEvaluateJob(IServiceScopeFactory scopes, ILogger<NotificationsEvaluateJob> logger) : IJob
{
    public async ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
            var result = await scope.ServiceProvider.GetRequiredService<IInvoker>().InvokeAsync(new EvaluateNotificationsCommand(), cancellationToken);
            if (result.TryGetFailure(out var failure))
                LogRefused(failure!.Code);
            else
                await JobIncidents.SucceededAsync(scopes, context, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            LogInterrupted();
        }
        catch (Exception e)
        {
            LogCrashed(e);
            await JobIncidents.CrashedAsync(scopes, context, cancellationToken);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "notifications-evaluate was interrupted")]
    private partial void LogInterrupted();

    [LoggerMessage(Level = LogLevel.Information, Message = "notifications-evaluate did not run: {Code}")]
    private partial void LogRefused(string code);

    [LoggerMessage(Level = LogLevel.Error, Message = "notifications-evaluate crashed")]
    private partial void LogCrashed(Exception exception);
}

/// <summary>
/// A bounded queue of people to refresh, drained by <see cref="NotificationRefreshWorker"/>. A full queue drops the request: the
/// hourly job catches up. The throttle (a person evaluated less than <c>Notifications:RefreshThrottleSeconds</c> ago is not
/// evaluated again) is applied by the worker, which has the scope the store needs.
/// </summary>
public sealed class NotificationRefreshQueue : INotificationRefresh
{
    private readonly Channel<PersonId> channel = Channel.CreateBounded<PersonId>(
        new BoundedChannelOptions(64) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });

    public ChannelReader<PersonId> Reader => channel.Reader;

    public void Request(PersonId person) => channel.Writer.TryWrite(person);
}

public sealed partial class NotificationRefreshWorker(
    NotificationRefreshQueue queue, IServiceScopeFactory scopes, IClock clock, IConfiguration configuration,
    ILogger<NotificationRefreshWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var throttle = NodaTime.Duration.FromSeconds(configuration.GetValue("Notifications:RefreshThrottleSeconds", 900));
        try
        {
            await foreach (var person in queue.Reader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    await RefreshAsync(person, throttle, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception e)
                {
                    LogCrashed(e, person.Value);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task RefreshAsync(PersonId person, NodaTime.Duration throttle, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var last = await scope.ServiceProvider.GetRequiredService<INotificationStore>().LastRunAtAsync(person, ct);
        if (last is { } at && clock.GetCurrentInstant() - at < throttle)
            return;

        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
        var result = await scope.ServiceProvider.GetRequiredService<IInvoker>().InvokeAsync(new EvaluateNotificationsCommand(person, Force: true), ct);
        if (result.TryGetFailure(out var failure))
            LogRefused(person.Value, failure!.Code);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Notification refresh of {Person} did not run: {Code}")]
    private partial void LogRefused(Guid person, string code);

    [LoggerMessage(Level = LogLevel.Error, Message = "Notification refresh of {Person} crashed")]
    private partial void LogCrashed(Exception exception, Guid person);
}

/// <summary>
/// The hourly digest: groups what waits for each person whose day it is, from 08:00 on the household's clock (notifications design
/// §5.2). Runs as the system actor like <see cref="NotificationsEvaluateJob"/>. Never throws.
/// </summary>
[DisallowConcurrentExecution]
public sealed partial class NotificationsDigestJob(IServiceScopeFactory scopes, ILogger<NotificationsDigestJob> logger) : IJob
{
    public async ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
            var result = await scope.ServiceProvider.GetRequiredService<IInvoker>().InvokeAsync(new SendDigestsCommand(), cancellationToken);
            if (result.TryGetFailure(out var failure))
                LogRefused(failure!.Code);
            else
                await JobIncidents.SucceededAsync(scopes, context, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            LogInterrupted();
        }
        catch (Exception e)
        {
            LogCrashed(e);
            await JobIncidents.CrashedAsync(scopes, context, cancellationToken);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "notifications-digest was interrupted")]
    private partial void LogInterrupted();

    [LoggerMessage(Level = LogLevel.Information, Message = "notifications-digest did not run: {Code}")]
    private partial void LogRefused(string code);

    [LoggerMessage(Level = LogLevel.Error, Message = "notifications-digest crashed")]
    private partial void LogCrashed(Exception exception);
}
