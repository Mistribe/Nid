using Nid.Platform.Contracts;
using Quartz;

namespace Nid.Host.Jobs;

/// <summary>
/// One <c>job_failed:{job}</c> notice per job: a crash publishes it to the admins (a repeat while it is open changes nothing), the next
/// run that does not crash resolves it. A refusal (« already running ») is neither. The notifier never throws.
/// </summary>
internal static class JobIncidents
{
    public static string Key(IJobExecutionContext context) => $"job_failed:{context.JobDetail.Key.Name}";

    public static async Task CrashedAsync(IServiceScopeFactory scopes, IJobExecutionContext context, Exception exception, CancellationToken ct)
    {
        JobRun.Failed(context, exception);
        await using var scope = scopes.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<INotifier>().PublishAsync(
            new Notice("job_failed", new Recipients.Admins(), Key(context), [], new Dictionary<string, string> { ["job"] = context.JobDetail.Key.Name }), ct);
    }

    public static async Task SucceededAsync(IServiceScopeFactory scopes, IJobExecutionContext context, CancellationToken ct)
    {
        JobRun.Succeeded(context);
        await using var scope = scopes.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<INotifier>().ResolveAsync(Key(context), ct);
    }
}
