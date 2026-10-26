using Microsoft.Extensions.Logging;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using NodaTime;
using NodaTime.Calendars;
using UnambitiousFx.Functional;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Platform.Notifications;

/// <summary>
/// Groups the notifications that wait for the digest into one message per person whose day it is (notifications design §5.2).
/// Run every hour by the host as the system actor: without <paramref name="Force"/> a digest goes out at the first run at or after
/// 08:00 on the household's clock.
/// </summary>
public sealed record SendDigestsCommand(bool Force = false) : ICommand<DigestSummary>, IAdminOnly;

/// <summary>How many digests the run queued, one per person.</summary>
public sealed record DigestSummary(int Queued);

/// <summary>Persistence of digests (notifications design §2.3, §5.2).</summary>
public interface IDigestStore
{
    /// <summary>
    /// Records the digest of (person, period). False when it exists: another run decided it, this one must stop. Meant to be inside
    /// the transaction that attaches the rows and queues the messages, so that nothing of it lasts unless all of it does.
    /// </summary>
    Task<bool> TryCreateAsync(Guid id, PersonId person, string periodKey, Instant now, CancellationToken ct);

    /// <summary>The person's open rows that no digest carried yet and that no message sent on its own for their current round, the oldest opened first.</summary>
    Task<IReadOnlyList<NotificationRecord>> CandidatesAsync(PersonId person, CancellationToken ct);

    /// <summary>Puts the rows in the digest (<c>digested_at</c>, <c>digest_id</c>); a row another digest took meanwhile is left. Returns how many were taken.</summary>
    Task<int> AttachAsync(Guid digestId, PersonId person, IReadOnlyCollection<Guid> rows, Instant now, CancellationToken ct);

    /// <summary>Queues one message per channel, due at <paramref name="now"/>; one that exists for (digest, channel) is ignored.</summary>
    Task EnqueueAsync(IReadOnlyList<NewDigestDelivery> deliveries, Instant now, CancellationToken ct);

    /// <summary>The rows the digest carries that are still open, the oldest opened first.</summary>
    Task<IReadOnlyList<NotificationRecord>> OpenRowsAsync(Guid digestId, CancellationToken ct);
}

[RequestHandler<SendDigestsCommand, DigestSummary>]
public sealed partial class SendDigestsHandler(
    IPersonRegistry persons, INotificationStore store, IDigestStore digests, NotificationCatalog catalog,
    IEnumerable<INotificationChannel> channels, IVisibleItems visible, IHouseholdCalendar calendar, IClock clock, IUnitOfWork unitOfWork,
    ILogger<SendDigestsHandler> logger) : IRequestHandler<SendDigestsCommand, DigestSummary>
{
    private const int FirstHour = 8;

    public async ValueTask<Result<DigestSummary>> HandleAsync(SendDigestsCommand command, CancellationToken ct = default)
    {
        _ = catalog.Kinds; // a kind declared twice fails here, before anything is written (as in the evaluation)
        var now = await calendar.NowAsync(ct);
        if (!command.Force && now.Hour < FirstHour)
            return Result.Success(new DigestSummary(0));

        var queued = 0;
        foreach (var adult in (await persons.ListAsync(ct)).Where(p => p.Kind == PersonKind.Adult && !p.IsPending))
        {
            try
            {
                if (await DigestOneAsync(adult, now.Date, ct))
                    queued++;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception e)
            {
                // One person's failure rolled their transaction back; the next hourly run tries again.
                LogFailed(e, adult.Id.Value);
            }
        }

        return Result.Success(new DigestSummary(queued));
    }

    /// <summary>The key of the period the digest covers: the day, or the ISO week.</summary>
    public static string PeriodKey(DigestFrequency frequency, LocalDate today)
        => frequency == DigestFrequency.Daily
            ? today.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)
            : $"{WeekYearRules.Iso.GetWeekYear(today):0000}-W{WeekYearRules.Iso.GetWeekOfWeekYear(today):00}";

    private async Task<bool> DigestOneAsync(PersonInfo adult, LocalDate today, CancellationToken ct)
    {
        var prefs = await store.PreferencesAsync(adult.Id, ct);
        if (prefs.Frequency == DigestFrequency.Weekly && prefs.Weekday != today.DayOfWeek)
            return false;

        var reaching = channels.Where(c => ChannelAccess.AddressFor(c, adult, prefs) is not null).ToList();
        if (reaching.Count == 0)
            return false; // nothing to send it on: the rows stay for the day a channel works

        // One transaction: the digest, its rows and its messages last together or not at all. Leaving the scope without a commit
        // (another run took the period, or nothing to say) rolls the digest row back: none is kept.
        await using var scope = await unitOfWork.BeginAsync(ct);
        var id = Guid.CreateVersion7();
        var now = clock.GetCurrentInstant();
        if (!await digests.TryCreateAsync(id, adult.Id, PeriodKey(prefs.Frequency, today), now, ct))
            return false;

        var readable = await visible.ForAsync(adult.Id, VisibilityScope.Me, ct);
        var rows = (await digests.CandidatesAsync(adult.Id, ct))
            .Where(r => OutboxRules.GoesToDigest(r.Level, prefs) && r.ItemIds.All(readable.Contains))
            .Select(r => r.Id)
            .ToList();
        if (rows.Count == 0 || await digests.AttachAsync(id, adult.Id, rows, now, ct) == 0)
            return false;

        await digests.EnqueueAsync(reaching.Select(c => new NewDigestDelivery(id, adult.Id, c.Name)).ToList(), now, ct);
        await scope.CommitAsync(ct);
        return true;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "The digest of person {Person} failed")]
    private partial void LogFailed(Exception exception, Guid person);
}
