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
    DecideCalibration.Results Results, Instant MeasuredAt)
{
    /// <summary>A worker's guesses are offered at the probability kept, or, with no scores, when they are right often enough as they are.</summary>
    public bool Offered => Scoreless ? Results.Guess is not null : Threshold is not null;
}

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

public static class DecideProfiles
{
    /// <summary>#821: the profile in force for <paramref name="key"/>. One measured on another version of the evaluation set is neither
    /// applied nor shown: what it measured is not what the set asks now, so the model needs calibrating again.</summary>
    public static async Task<DecideProfile?> CurrentProfileAsync(this IDecideCalibrationStore store, DecideKey key, CancellationToken ct)
        => await store.ProfileAsync(key, ct) is { } profile && profile.EvalVersion == CategoryEvalSet.Current.Version ? profile : null;
}

/// <summary>#450: what suggests now, and so what a calibration measures: the <c>decide</c> use's model when it has one, a worker's
/// implementation as the workers declare it; else nearest neighbours on the <c>embed</c> use's model. Each at the digest the workers
/// declare (or the embed model's cloud profile).</summary>
public sealed class DecideSetup(IEmbeddings embeddings, IDecisions decisions, IModelDigests digests)
{
    /// <summary><paramref name="Implementation"/> and <paramref name="Version"/> null: no worker declares the <c>decide</c> model now.</summary>
    /// <remarks><paramref name="Off"/>: the <c>decide</c> use is off, so nothing is calibrated and no model is said to be missing.</remarks>
    public sealed record Current(string? Implementation, string? Version, string? Model, string? Digest, bool Off = false)
    {
        public DecideKey? Key => !Off && Implementation is { } implementation && Version is { } version && Model is { } model && Digest is { } digest
            ? new DecideKey(implementation, version, model, digest)
            : null;

        /// <summary>Why nothing can be calibrated: the use off, no model set, or none the workers agree on.</summary>
        public string? Unavailable => Off ? JobCodes.UseOff : Model is null ? JobCodes.NoModel : Key is null ? JobCodes.ModelUnavailable : null;
    }

    /// <summary>The set-up « Calibrer » measures: nothing while the <c>decide</c> use is off, else the worker's model or nearest neighbours.</summary>
    public async Task<Current> CurrentAsync(CancellationToken ct)
        => !await embeddings.UseOnAsync(ModelUses.Decide, ct)
            ? new Current(null, null, null, null, Off: true)
            : await WorkerAsync(ct) ?? await NeighboursAsync(ct);

    public async Task<Current> NeighboursAsync(CancellationToken ct)
    {
        var model = await embeddings.ModelAsync(ct);
        return new Current(DecideCalibration.NearestNeighbours, DecideCalibration.NearestNeighboursVersion, model,
            model is null ? null : await digests.DigestAsync(ModelUses.Capability(ModelUses.Embed), model, ct));
    }

    /// <summary>The worker's set-up; null while the <c>decide</c> use has no model.</summary>
    public async Task<Current?> WorkerAsync(CancellationToken ct)
    {
        if (await decisions.ModelAsync(ct) is not { } model)
            return null;
        if (await decisions.ImplementationAsync(model, ct) is not { } implementation)
            return new Current(null, null, model, null);
        var digest = await digests.DigestAsync(ModelUses.Capability(ModelUses.Decide), model, ct);
        return new Current(implementation.Name, CategoryQuestion.Version(implementation.Version, digest), model, digest);
    }

    /// <summary>A worker's profile in force: its guesses are shown (<see cref="DecideProfile.Offered"/>).</summary>
    public async Task<(DecideKey Key, DecideProfile Profile)?> WorkerProfileAsync(IDecideCalibrationStore calibrations, CancellationToken ct)
        => await WorkerAsync(ct) is { Key: { } key } && await calibrations.CurrentProfileAsync(key, ct) is { Offered: true } profile ? (key, profile) : null;
}

/// <summary>
/// #450, in <c>ModelWorkLoop</c>: runs one queued calibration a pass. Nearest neighbours embed every text of the evaluation set with the model calibrated, then sweep its thresholds (<see cref="DecideCalibration.Neighbours"/>); a
/// worker's <c>decide</c> is asked each case as the guess job would ask it, then calibrated (<see cref="DecideCalibration.Worker"/>).
/// Sent as <see cref="DataClasses.BankLabels"/>, like the labels it stands for: the gateway takes the class <c>none</c> for its echo test
/// alone, and a calibration should reach only where the guesses would. The labels are invented, so no adult is concerned.
/// </summary>
public sealed class DecideCalibrator(IDecideCalibrationStore store, IEmbeddings embeddings, IDecisions decisions, IModelDigests digests, IClock clock,
    CategoryGuessSignal signal)
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
        // A new profile may offer a worker's guesses, or show others: asked now rather than at the next 5 minutes.
        if (code is null)
            signal.Request();
    }

    /// <summary>Null once the profile is saved, else the gateway's refusal.</summary>
    private async Task<string?> CalibrateAsync(DecideKey key, CancellationToken ct)
        => key.Implementation == DecideCalibration.NearestNeighbours ? await NeighboursAsync(key, ct) : await WorkerAsync(key, ct);

    private async Task<string?> WorkerAsync(DecideKey key, CancellationToken ct)
    {
        if (await decisions.ImplementationAsync(key.Model, ct) is not { } implementation
            || (implementation.Name, CategoryQuestion.Version(implementation.Version, key.Digest)) != (key.Implementation, key.Version))
            return JobCodes.ModelUnavailable;
        var set = CategoryEvalSet.Current;
        var guesses = new List<DecideCalibration.Guess>();
        var options = 0;
        var oneStep = CategoryQuestion.OneStep(key.Digest);
        foreach (var side in set.Cases.Zip(set.CalibrationCases()).GroupBy(c => c.First.Debit))
        {
            var tops = CategoryEvalSet.Tree(side.Key);
            options = Math.Max(options, oneStep ? CategoryQuestion.Leaves(tops)!.Count + 1 : Math.Max(tops.Count + 1, tops.Max(t => t.Children.Count)));
            foreach (var batch in side.Chunk(CategoryQuestion.Batch))
            {
                var asked = await CategoryQuestion.AskAsync(decisions,
                    [.. batch.Select(c => CategoryGuesser.Text(c.Second.Pattern, c.First.Label, c.First.Counterparty, c.First.Debit, []))], side.Key, tops,
                    DataClasses.BankLabels, [], oneStep, ct);
                if (!asked.TryGetValue(out var result))
                    return asked.TryGetFailure(out var failure) ? failure!.Code : JobCodes.ModelUnavailable;
                if (result.Key != key)
                    return JobCodes.ModelUnavailable; // another model, or the same re-pulled, since it was queued
                guesses.AddRange(batch.Zip(result.Guesses, (c, g) => CategoryEvalSet.Expect(g, c.Second.Expected, oneStep)));
            }
        }

        var results = DecideCalibration.Worker(guesses, implementation.Scoreless);
        await store.SaveProfileAsync(new DecideProfile(key, set.Version, options,
            implementation.Scoreless ? null : results.Guess?.Threshold, null, results.Temperature, implementation.Scoreless, results, clock.GetCurrentInstant()), ct);
        return null;
    }

    private async Task<string?> NeighboursAsync(DecideKey key, CancellationToken ct)
    {
        var set = CategoryEvalSet.Current;
        var cases = set.CalibrationCases();
        var history = set.Evidence();
        var options = CategoryEvalSet.Options();
        var vectors = new Dictionary<string, IReadOnlyList<float>>(StringComparer.Ordinal);
        foreach (var batch in cases.Select(c => c.Pattern).Concat(history.Select(h => h.Pattern)).Concat(options.Select(o => o.Text)).Distinct().Chunk(PatternIndexer.Batch))
        {
            var embedded = await embeddings.EmbedAsync(new LlmEmbedRequest(batch, DataClasses.BankLabels, Concerned: [], Kinds: [DataClasses.BankLabels]), ct);
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

/// <summary>The set-up's <paramref name="Implementation"/> (<c>nearest-neighbours</c>, or the <c>decide</c> model's on the workers; null
/// when none declares it now) on <paramref name="Model"/> at <paramref name="Digest"/>; <paramref name="Unavailable"/> says why it cannot be
/// calibrated (<c>ai.no_model</c>, <c>ai.model_unavailable</c>). No <paramref name="Profile"/>: nothing is suggested by that set-up.</summary>
public sealed record CategorySuggestionCalibrationView(string? Implementation, string? Version, string? Model, string? Digest, string? Unavailable,
    CalibrationRunView? Latest, CalibrationProfileView? Profile);

public sealed record CalibrationRunView(Guid Id, string Status, string? Code, Instant QueuedAt, Instant? FinishedAt);

/// <summary>The profile in force: its thresholds (null: that mode not offered) and what was measured at each. A worker's: the probability
/// kept (<paramref name="Threshold"/>), the <paramref name="Temperature"/> learnt, <paramref name="Scoreless"/> when it gives no scores (its
/// guesses then shown as they are, if <paramref name="Guess"/> was right often enough), and the reliability of its probabilities.</summary>
public sealed record CalibrationProfileView(int EvalVersion, int Options, double? Threshold, double? ColdThreshold, int Cases, int? Neighbours,
    CalibrationFiguresView? WithHistory, CalibrationFiguresView? Cold, Instant MeasuredAt, double? Temperature, bool Scoreless, CalibrationFiguresView? Guess,
    IReadOnlyList<ReliabilityBinView> Reliability);

/// <summary>One fifth of a worker's probabilities: their mean, how often they were right, how many.</summary>
public sealed record ReliabilityBinView(double Confidence, double Accuracy, int Count);

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
        var profile = current.Key is { } k ? await store.CurrentProfileAsync(k, ct) : null;
        return Result.Success(new CategorySuggestionCalibrationView(current.Implementation, current.Version,
            current.Model, current.Digest, current.Unavailable, latest is null ? null : View(latest), profile is null ? null : View(profile)));
    }

    internal static CalibrationRunView View(DecideCalibrationRun run) => new(run.Id, run.Status, run.Code, run.QueuedAt, run.FinishedAt);

    private static CalibrationProfileView View(DecideProfile p)
        => new(p.EvalVersion, p.Options, p.Threshold, p.ColdThreshold, p.Results.Cases, p.Results.Neighbours, Figures(p.Results.WithHistory),
            Figures(p.Results.Cold), p.MeasuredAt, p.Temperature, p.Scoreless, Figures(p.Results.Guess),
            [.. (p.Results.Reliability ?? []).Select(b => new ReliabilityBinView(b.Confidence, b.Accuracy, b.Count))]);

    private static CalibrationFiguresView? Figures(DecideCalibration.Measure? m)
        => m is null ? null : new(m.Threshold, m.Shown, m.Correct, m.Wrong, m.DontKnow, m.WrongLow, m.WrongHigh, m.Coverage);
}
