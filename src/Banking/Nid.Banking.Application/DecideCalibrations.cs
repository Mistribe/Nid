using Nid.Ai.Contracts;
using Nid.Banking.Contracts;
using Nid.Banking.Domain;
using Nid.Platform.Contracts;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Banking.Application;

/// <summary>#450: what a calibration measures: an implementation and its version (prompt included), a model and its digest. A re-pulled
/// model has another digest, so needs its own calibration.</summary>
public sealed record DecideKey(string Implementation, string Version, string Model, string Digest);

/// <summary>
/// #450 (<c>docs/ai/README.md</c> §7.3): one calibration's result for a <see cref="DecideKey"/>, on the evaluation set of
/// <paramref name="EvalVersion"/>; <paramref name="Options"/> the most options a case had. <paramref name="Threshold"/> with history,
/// <paramref name="ColdThreshold"/> on the categories' names alone; null: that mode is not offered for this model.
/// </summary>
public sealed record DecideProfile(DecideKey Key, int EvalVersion, int Options, double? Threshold, double? ColdThreshold, double? Temperature, bool Scoreless,
    DecideCalibration.Results Results, Instant MeasuredAt);

/// <summary>A calibration asked for: <c>queued</c>, <c>running</c>, <c>done</c> or <c>failed</c> with the gateway's <paramref name="Code"/>.</summary>
public sealed record DecideCalibrationRun(Guid Id, DecideKey Key, string Status, string? Code, Instant QueuedAt, Instant? FinishedAt);

public static class CalibrationStatuses
{
    public const string Queued = "queued", Running = "running", Done = "done", Failed = "failed";
}

/// <summary>#450: <c>banking.decide_calibration</c> and <c>banking.decide_profile</c>.</summary>
public interface IDecideCalibrationStore
{
    /// <summary>The calibration of <paramref name="key"/> queued or running, else a new one, queued.</summary>
    Task<DecideCalibrationRun> QueueAsync(DecideKey key, Instant at, CancellationToken ct);

    /// <summary>The oldest calibration queued, or left running by a host that stopped.</summary>
    Task<DecideCalibrationRun?> NextAsync(CancellationToken ct);

    Task<DecideCalibrationRun?> LatestAsync(DecideKey key, CancellationToken ct);

    Task SetStatusAsync(Guid id, string status, string? code, Instant? finishedAt, CancellationToken ct);

    Task<DecideProfile?> ProfileAsync(DecideKey key, CancellationToken ct);

    /// <summary>Replaces the profile of the same key.</summary>
    Task SaveProfileAsync(DecideProfile profile, CancellationToken ct);
}

/// <summary>#450: what suggests by meaning now, and so what a calibration measures: nearest neighbours on the <c>embed</c> use's
/// model, at the digest the workers declare (or its cloud profile).</summary>
public sealed class DecideSetup(IEmbeddings embeddings, IModelDigests digests)
{
    public sealed record Current(string? Model, string? Digest)
    {
        public DecideKey? Key => Model is { } model && Digest is { } digest
            ? new DecideKey(DecideCalibration.NearestNeighbours, DecideCalibration.NearestNeighboursVersion, model, digest)
            : null;

        /// <summary>Why nothing can be calibrated: no model set, or none the workers agree on.</summary>
        public string? Unavailable => Model is null ? JobCodes.NoModel : Digest is null ? JobCodes.ModelUnavailable : null;
    }

    public async Task<Current> CurrentAsync(CancellationToken ct)
    {
        var model = await embeddings.ModelAsync(ct);
        return new Current(model, model is null ? null : await digests.DigestAsync(ModelUses.Capability(ModelUses.Embed), model, ct));
    }
}

/// <summary>
/// #450, in <c>ModelWorkLoop</c>: runs one queued calibration a pass. Nearest neighbours embed every text of the evaluation set (invented,
/// so <see cref="DataClasses.None"/>) with the model calibrated, then sweep its thresholds (<see cref="DecideCalibration.Neighbours"/>).
/// </summary>
public sealed class DecideCalibrator(IDecideCalibrationStore store, IEmbeddings embeddings, IModelDigests digests, IClock clock)
{
    public async Task RunOnceAsync(CancellationToken ct)
    {
        if (await store.NextAsync(ct) is not { } run)
            return;
        await store.SetStatusAsync(run.Id, CalibrationStatuses.Running, null, null, ct);
        string? code;
        try
        {
            code = await CalibrateAsync(run.Key, ct);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // Left running, it would be run again every pass; the loop logs the exception.
            await store.SetStatusAsync(run.Id, CalibrationStatuses.Failed, BankingCodes.CalibrationFailed, clock.GetCurrentInstant(), CancellationToken.None);
            throw;
        }
        await store.SetStatusAsync(run.Id, code is null ? CalibrationStatuses.Done : CalibrationStatuses.Failed, code, clock.GetCurrentInstant(), ct);
    }

    /// <summary>Null once the profile is saved, else the gateway's refusal.</summary>
    private async Task<string?> CalibrateAsync(DecideKey key, CancellationToken ct)
    {
        var set = CategoryEvalSet.Current;
        var cases = set.CalibrationCases();
        var history = set.Evidence();
        var options = CategoryEvalSet.Options();
        var vectors = new Dictionary<string, IReadOnlyList<float>>(StringComparer.Ordinal);
        foreach (var batch in cases.Select(c => c.Pattern).Concat(history.Select(h => h.Pattern)).Concat(options.Select(o => o.Text)).Distinct().Chunk(PatternIndexer.Batch))
        {
            var embedded = await embeddings.EmbedAsync(new LlmEmbedRequest(batch, DataClasses.None), ct);
            if (!embedded.TryGetValue(out var result))
                return embedded.TryGetFailure(out var failure) ? failure!.Code : JobCodes.ModelUnavailable;
            if (result.Model != key.Model)
                return JobCodes.ModelUnavailable; // the use's model changed since it was queued
            foreach (var (text, vector) in batch.Zip(result.Vectors))
                vectors[text] = vector;
        }
        // Re-pulled meanwhile: what was measured is not that build.
        if (await digests.DigestAsync(ModelUses.Capability(ModelUses.Embed), key.Model, ct) != key.Digest)
            return JobCodes.ModelUnavailable;

        var results = DecideCalibration.Neighbours(cases, history, options, text => vectors[text]);
        await store.SaveProfileAsync(new DecideProfile(key, set.Version, options.GroupBy(o => o.Debit).Max(g => g.Count()), results.WithHistory?.Threshold,
            results.Cold?.Threshold, null, false, results, clock.GetCurrentInstant()), ct);
        return null;
    }
}

/// <summary>#450: « Calibrer » (an admin): queues a calibration of the current set-up; one already queued or running is that one. A model and
/// invented labels, no account: it runs with banking off too, as the administrator's sync journal does.</summary>
public sealed record CalibrateCategorySuggestionsCommand : ICommand<CalibrationRunView>, IAdminOnly;

/// <summary>#450 (any adult): the current set-up, its latest calibration and the profile in force, if any.</summary>
public sealed record GetCategorySuggestionCalibrationQuery : IQuery<CategorySuggestionCalibrationView>;

/// <summary>The set-up's <paramref name="Implementation"/> on <paramref name="Model"/> at <paramref name="Digest"/>; <paramref name="Unavailable"/>
/// says why it cannot be calibrated (<c>ai.no_model</c>, <c>ai.model_unavailable</c>). No <paramref name="Profile"/>: suggestions by meaning are not
/// offered.</summary>
public sealed record CategorySuggestionCalibrationView(string Implementation, string Version, string? Model, string? Digest, string? Unavailable,
    CalibrationRunView? Latest, CalibrationProfileView? Profile);

public sealed record CalibrationRunView(Guid Id, string Status, string? Code, Instant QueuedAt, Instant? FinishedAt);

/// <summary>The profile in force: its thresholds (null: that mode not offered) and what was measured at each.</summary>
public sealed record CalibrationProfileView(int EvalVersion, int Options, double? Threshold, double? ColdThreshold, int Cases, int? Neighbours,
    CalibrationFiguresView? WithHistory, CalibrationFiguresView? Cold, Instant MeasuredAt);

/// <summary>At <paramref name="Threshold"/>: suggestions shown, right, wrong, « don't know »; the wrong rate's 95 % interval and the coverage.</summary>
public sealed record CalibrationFiguresView(double Threshold, int Shown, int Correct, int Wrong, int DontKnow, double WrongLow, double WrongHigh, double Coverage);

[RequestHandler<CalibrateCategorySuggestionsCommand, CalibrationRunView>]
public sealed class CalibrateCategorySuggestionsHandler(DecideSetup setup, IDecideCalibrationStore store, IClock clock)
    : IRequestHandler<CalibrateCategorySuggestionsCommand, CalibrationRunView>
{
    public async ValueTask<Result<CalibrationRunView>> HandleAsync(CalibrateCategorySuggestionsCommand command, CancellationToken ct = default)
    {
        var current = await setup.CurrentAsync(ct);
        return current.Key is { } key
            ? Result.Success(GetCategorySuggestionCalibrationHandler.View(await store.QueueAsync(key, clock.GetCurrentInstant(), ct)))
            : Result.Failure<CalibrationRunView>(new Failure(current.Unavailable!, "No model to calibrate.", null));
    }
}

[RequestHandler<GetCategorySuggestionCalibrationQuery, CategorySuggestionCalibrationView>]
public sealed class GetCategorySuggestionCalibrationHandler(DecideSetup setup, IDecideCalibrationStore store)
    : IRequestHandler<GetCategorySuggestionCalibrationQuery, CategorySuggestionCalibrationView>
{
    public async ValueTask<Result<CategorySuggestionCalibrationView>> HandleAsync(GetCategorySuggestionCalibrationQuery query, CancellationToken ct = default)
    {
        var current = await setup.CurrentAsync(ct);
        var latest = current.Key is { } key ? await store.LatestAsync(key, ct) : null;
        var profile = current.Key is { } k ? await store.ProfileAsync(k, ct) : null;
        return Result.Success(new CategorySuggestionCalibrationView(DecideCalibration.NearestNeighbours, DecideCalibration.NearestNeighboursVersion,
            current.Model, current.Digest, current.Unavailable, latest is null ? null : View(latest), profile is null ? null : View(profile)));
    }

    internal static CalibrationRunView View(DecideCalibrationRun run) => new(run.Id, run.Status, run.Code, run.QueuedAt, run.FinishedAt);

    private static CalibrationProfileView View(DecideProfile p)
        => new(p.EvalVersion, p.Options, p.Threshold, p.ColdThreshold, p.Results.Cases, p.Results.Neighbours, Figures(p.Results.WithHistory),
            Figures(p.Results.Cold), p.MeasuredAt);

    private static CalibrationFiguresView? Figures(DecideCalibration.Measure? m)
        => m is null ? null : new(m.Threshold, m.Shown, m.Correct, m.Wrong, m.DontKnow, m.WrongLow, m.WrongHigh, m.Coverage);
}
