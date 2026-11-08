using System.Text;
using Nid.Core.Contracts;
using Nid.Investments.Contracts;
using Nid.Investments.Domain.Prices;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;
using Duration = NodaTime.Duration;

namespace Nid.Investments.Application.Prices;

/// <summary>
/// One collection run (holding prices spec §3–§4), journaled as <c>FxCollector</c> journals its runs: whatever goes wrong ends
/// in the journal as a failed run, and a cancellation of the caller's token closes it as <see cref="PriceCodes.RunInterrupted"/>.
/// An ISIN that yields no price is a count, not a failure; the journal holds counts only, never an ISIN (D12).
/// </summary>
public sealed class PriceCollector(
    IPriceSourceClient sources, ISecurityPriceStore prices, IPriceRunLog runs, IHoldingStore holdings, IItemRegistry items,
    IFxRates rates, IHouseholdSettings settings, PriceOptions options, IClock clock)
{
    public const int RetentionDays = 400;

    private enum Outcome { Accepted, Rejected, NotFound, SourceError }

    private sealed class Progress
    {
        public int Queried, Accepted, Rejected, NotFound, SourceErrors;
        public bool Requested;
    }

    /// <summary>What the run knows of every ISIN before it asks the sources.</summary>
    private sealed record Known(
        IReadOnlyDictionary<string, SecurityListing> Listings, IReadOnlyDictionary<string, SecurityPrice> Accepted,
        IReadOnlyDictionary<string, SnapshotPrice> Snapshots, IReadOnlyDictionary<ItemId, ItemAccess> Envelopes);

    private LocalDate Today => clock.GetCurrentInstant().InUtc().Date;

    public async Task<Result<PriceRun>> CollectAsync(CancellationToken ct)
    {
        if (!(await settings.ReadAsync(ct)).PricesEnabled)
            return Result.Failure<PriceRun>(new Failure(PriceCodes.Disabled, "Price collection is turned off for this household.", null));
        var run = await runs.TryStartAsync(ct);
        if (run is null)
            return Result.Failure<PriceRun>(new Failure(PriceCodes.SyncRunning, "A price collection is already running.", null));

        var progress = new Progress();
        Failure? failure = null;
        var interrupted = false;
        try
        {
            await CollectAllAsync(progress, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            interrupted = true;
            failure = new Failure(PriceCodes.RunInterrupted, "The run was interrupted before it finished (host shutting down).", null);
        }
        catch (Exception e)
        {
            failure = new Failure(PriceCodes.SourceUnavailable, $"The run stopped on {e.GetType().Name}.", null);
        }

        var finished = run with
        {
            FinishedAt = clock.GetCurrentInstant(),
            Status = failure is null ? PriceRunStatus.Succeeded : PriceRunStatus.Failed,
            Queried = progress.Queried,
            Accepted = progress.Accepted,
            Rejected = progress.Rejected,
            NotFound = progress.NotFound,
            SourceErrors = progress.SourceErrors,
            FailureCode = failure?.Code,
            FailureMessage = failure?.Message,
        };
        await runs.FinishAsync(finished, CancellationToken.None); // a cancelled run is still closed
        if (interrupted)
            ct.ThrowIfCancellationRequested();
        await runs.PurgeBeforeAsync(clock.GetCurrentInstant() - Duration.FromDays(RetentionDays), CancellationToken.None);
        await prices.PurgeBeforeAsync(Today.PlusDays(-RetentionDays), CancellationToken.None);

        return Result.Success(finished);
    }

    private async Task CollectAllAsync(Progress progress, CancellationToken ct)
    {
        var isins = await holdings.PricedIsinsAsync(ct);
        var snapshots = (await holdings.SnapshotUnitPricesAsync(isins, ct)).ToDictionary(s => s.Isin);
        // A registry read without an access check: the run sees every envelope's currency, and only counts leave it.
        var envelopes = await items.FindManyAsync(snapshots.Values.Select(s => s.Envelope).Distinct().ToList(), ct);
        var known = new Known(await prices.ListingsAsync(isins, ct), await prices.LatestAcceptedAsync(isins, ct), snapshots, envelopes);

        foreach (var isin in isins)
        {
            progress.Queried++;
            switch (await CollectAsync(isin, known, progress, ct))
            {
                case Outcome.Accepted: progress.Accepted++; break;
                case Outcome.Rejected: progress.Rejected++; break;
                case Outcome.NotFound: progress.NotFound++; break;
                default: progress.SourceErrors++; break;
            }
        }
    }

    /// <summary>Spec §3.3: the Boursorama path (cached, else searched), then Yahoo when Boursorama has no readable page.</summary>
    private async Task<Outcome> CollectAsync(string isin, Known known, Progress progress, CancellationToken ct)
    {
        var now = clock.GetCurrentInstant();
        var listing = known.Listings.GetValueOrDefault(isin);
        if (listing?.NotFoundAt is { } notFound && notFound > now - Duration.FromDays(options.NotFoundRetryDays))
            return Outcome.NotFound;

        var (path, symbol) = (listing?.BoursoramaPath, listing?.YahooSymbol);
        bool searchedBoursorama = false, searchedYahoo = false, answered = false, failed = false;
        PriceReading? reading = null;
        var source = PriceSources.Boursorama;

        if (path is null)
        {
            searchedBoursorama = true;
            var found = await FetchAsync(() => sources.BoursoramaSearchAsync(isin, ct), progress, ct);
            failed |= found is null;
            path = found?.Body is { } html ? BoursoramaReader.SearchPath(Encoding.UTF8.GetString(html)) : null;
        }
        if (path is not null)
        {
            var page = await FetchAsync(() => sources.BoursoramaPageAsync(path, ct), progress, ct);
            failed |= page is null;
            if (page is { Body: null })
                path = null; // the page is gone: searched again at the next run
            else if (page?.Body is { } html)
            {
                answered = true;
                var text = Encoding.UTF8.GetString(html);
                // A page that does not name the ISIN is another security's: unreadable.
                reading = text.Contains(isin, StringComparison.Ordinal) ? BoursoramaReader.Quote(text, path) : null;
            }
        }

        if (reading is null)
        {
            source = PriceSources.Yahoo;
            if (symbol is null)
            {
                searchedYahoo = true;
                var found = await FetchAsync(() => sources.YahooSearchAsync(isin, ct), progress, ct);
                failed |= found is null;
                symbol = found?.Body is { } json ? YahooReader.Symbol(Encoding.UTF8.GetString(json)) : null;
            }
            if (symbol is not null)
            {
                var chart = await FetchAsync(() => sources.YahooChartAsync(symbol, ct), progress, ct);
                failed |= chart is null;
                if (chart is { Body: null })
                    symbol = null;
                else if (chart?.Body is { } json)
                {
                    answered = true;
                    reading = YahooReader.Quote(Encoding.UTF8.GetString(json));
                }
            }
        }

        // Not found only when both sources were searched tonight and neither knew the ISIN; a cleared path is searched tomorrow.
        var nobodyKnows = path is null && symbol is null && searchedBoursorama && searchedYahoo && !failed;
        var updated = new SecurityListing(
            isin, path, symbol, searchedBoursorama || searchedYahoo ? now : listing!.ResolvedAt, nobodyKnows ? now : null);
        if (updated != listing)
            await prices.SaveListingAsync(updated, ct);

        if (reading is null)
            return answered || failed ? Outcome.SourceError : Outcome.NotFound;
        // Case-sensitive: Yahoo's GBp is pence, and stored as GBP the price would be a hundred times too high.
        if (!Currency.TryCreate(reading.Currency, out var currency))
            return Outcome.SourceError;

        var reference = await ReferenceAsync(known.Accepted.GetValueOrDefault(isin), known.Snapshots.GetValueOrDefault(isin), known.Envelopes, currency, ct);
        var verdict = PricePlausibility.Check(reading, reference, Today, options.MaxChange, options.MaxAgeDays);
        if (verdict == PriceVerdict.Invalid)
            return Outcome.SourceError;

        var rejected = verdict == PriceVerdict.Rejected;
        await prices.WriteAsync(new SecurityPrice(
            isin, reading.OnDate, reading.Price, currency, source, rejected ? PriceStatus.Rejected : PriceStatus.Accepted,
            rejected ? reference : null, now, null), ct);
        return rejected ? Outcome.Rejected : Outcome.Accepted;
    }

    /// <summary>
    /// Spec §4: the ISIN's latest accepted price, else the unit price of the latest snapshot that holds it, in the reading's
    /// currency at the reference's date; no rate, no reference.
    /// </summary>
    private async Task<decimal?> ReferenceAsync(
        SecurityPrice? accepted, SnapshotPrice? snapshot, IReadOnlyDictionary<ItemId, ItemAccess> envelopes, Currency to, CancellationToken ct)
    {
        (decimal Amount, Currency From, LocalDate On)? reference = accepted is not null
            ? (accepted.Price, accepted.Currency, accepted.OnDate)
            : snapshot is not null && envelopes.TryGetValue(snapshot.Envelope, out var envelope) && Currency.TryCreate(envelope.Currency, out var own)
                ? (snapshot.UnitPrice, own, snapshot.AsOf)
                : null;
        if (reference is not { } r)
            return null;

        var amount = r.Amount;
        if (r.From != to && !(await rates.LoadAsync([r.From, to], r.On, r.On, ct)).TryConvert(r.Amount, r.From, to, r.On, false, out amount, out _))
            return null;
        return amount > 0m ? amount : null; // the check divides by it
    }

    private async Task<PriceFetch?> FetchAsync(Func<Task<Result<PriceFetch>>> request, Progress progress, CancellationToken ct)
    {
        if (progress.Requested)
            await Task.Delay(options.RequestSpacing, ct);
        progress.Requested = true;
        return (await request()).TryGetValue(out var fetch) ? fetch : null;
    }
}
