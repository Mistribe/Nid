using Nid.Ai.Contracts;
using Nid.Ai.Protocol.Contracts;
using Nid.Platform.Contracts;
using NodaTime;
using UnambitiousFx.Functional;

namespace Nid.Ai.Application;

/// <summary>
/// #450, the gateway's <c>decide</c> task (<c>docs/ai/README.md</c> §7) as <see cref="LlmEmbedder"/>: the use's model and mode, the data
/// policy and the consent of the adults concerned, every call recorded in <c>llm_call</c> without its content. #812: with
/// <c>self_hosted_then_cloud</c>, a cloud profile's <c>decide</c> model is that provider's alone (Jev), as <see cref="LlmEmbedder"/> routes one.
/// </summary>
public sealed class LlmDecider(
    IAiSettings settings, IModelRequests requests, IAssistantPeople people, ILlmCalls calls, IActorAccessor actors, IClock clock, IWorkerStore workers,
    ILlmProfiles profiles, CloudProvider cloud)
    : IDecisions
{
    public async Task<string?> ModelAsync(CancellationToken ct)
        => await settings.UseAsync(ModelUses.Decide, ct) is { Mode: not UseModes.Off, Model: { } model } ? model : null;

    public async Task<LlmDecideImplementation?> ImplementationAsync(string model, CancellationToken ct)
    {
        if (await CloudAsync(model, ct))
            return cloud.DecideImplementation(model);
        var declared = (await workers.ListAsync(ct)).Where(w => w.Status == WorkerStatus.Active)
            .SelectMany(w => WorkerRouter.DeclarationOf(w)?.Models ?? [])
            .Where(m => m.Capability == DeclaredCapability.Decide && m.Name == model && m.Decide is not null)
            .Select(m => m.Decide!).Distinct().ToList();
        return declared is [var one] ? new LlmDecideImplementation(one.Name, one.Version, one.Scores == DecideScores.None) : null;
    }

    public async Task<Result<LlmDecisions>> DecideAsync(LlmDecideRequest request, CancellationToken ct)
    {
        var started = clock.GetCurrentInstant();
        ServingWorker? worker = null;
        var trace = new LlmCallTrace();
        var outcome = new LlmGateway.Outcome();
        try
        {
            var use = await settings.UseAsync(ModelUses.Decide, ct);
            outcome.Model = use.Model;
            if (use.Mode == UseModes.Off || use.Model is null)
                return Refuse(use.Mode == UseModes.Off ? JobCodes.UseOff : JobCodes.NoModel);
            if (request.Options.Count > DecideRequestInput.MaxOptions)
                return Refuse(JobCodes.InvalidInput);
            var input = new DecideRequestInput(use.Model, [.. request.Options.Select((name, i) => new DecideOption(DecideRequestInput.Ids[i], name))],
                request.Texts);
            if (!input.IsValid())
                return Refuse(JobCodes.InvalidInput);

            // Without everyone's allow, a remote worker is skipped; noted, so the refusal says why rather than « no model ».
            var offsite = await OffsiteConsent.AllowedAsync(people, request.Concerned ?? [], ct);
            if (use.Mode == UseModes.SelfHostedThenCloud && CloudModels.Of(profiles, ModelUses.Decide, use.Model) is not null)
                return await CloudDecideAsync(use.Model, offsite);
            var heldBack = false;
            Func<ServingWorker, bool>? onsite = offsite ? null : serving =>
            {
                if (serving.Placement == WorkerPlacement.Local)
                    return true;
                heldBack = true;
                return false;
            };
            var answers = new DecideRequestOutput?[request.Texts.Count];
            await foreach (var frame in requests.StreamAsync("decide", 1, ContractJson.Write(input), request.DataClass, null, ct, model: use.Model,
                               served: serving => worker = serving, only: onsite))
            {
                if (!frame.TryGet(out var data, out var failed))
                    return Refuse(!offsite && heldBack && failed!.Code == JobCodes.ModelUnavailable ? JobCodes.OffsiteNotAllowed : failed!.Code);
                var output = ContractJson.Read<DecideRequestOutput>(data.Span);
                if (output.Index >= answers.Length)
                    return Refuse(JobCodes.ResultInvalid);
                answers[output.Index] = output;
            }

            // Every text answered, by one build of one implementation, with only the options asked.
            var place = input.Options.Select((o, i) => (o.Id, i)).ToDictionary(o => o.Id, o => o.i, StringComparer.Ordinal);
            if (answers.Any(a => a is null || (a.Choice is not null && !place.ContainsKey(a.Choice)) || a.Scores.Any(s => !place.ContainsKey(s.Id)))
                || answers.Select(a => (a!.Digest, a.Implementation, a.ImplementationVersion)).Distinct().Count() != 1 || worker is null)
                return Refuse(JobCodes.ResultInvalid);
            var first = answers[0]!;
            outcome.End(new ChatUsage(0, 0, ChatStop.End, use.Model));
            return Result.Success(new LlmDecisions(use.Model, first.Digest, first.Implementation, first.ImplementationVersion,
                worker.Placement == WorkerPlacement.Local ? "local" : "remote",
                [.. answers.Select(a => new LlmDecision(a!.Choice is { } c ? place[c] : null, a.Scores.ToDictionary(s => place[s.Id], s => s.Score)))]));
        }
        finally
        {
            await calls.RecordAsync(new LlmCallRecord(Guid.CreateVersion7(), started, actors.Current?.PersonId?.Value, "decide", ModelUses.Decide,
                worker?.Id, null, null, outcome.Model, outcome.TokensIn, 0, outcome.Status, outcome.Code, trace.ProfileId, trace.Provider, trace.Tier,
                trace.ReservedEur, trace.CostEur), CancellationToken.None);
        }

        // #812 (docs/ai/README.md §6.1): local-only data never; bank labels only once an admin put them in allowed_kinds; and only with
        // allow from each adult concerned. A guess stays a suggestion, whatever the provider's confidence.
        async Task<Result<LlmDecisions>> CloudDecideAsync(string model, bool offsite)
        {
            if (request.DataClass == DataClasses.LocalOnly
                || (request.DataClass == DataClasses.BankLabels && !(await settings.AllowedKindsAsync(ct)).Contains(DataClasses.BankLabels)))
                return Refuse(JobCodes.CloudNotAllowed);
            if (!offsite)
                return Refuse(JobCodes.OffsiteNotAllowed);
            if (cloud.DecideImplementation(model) is not { } implementation)
                return Refuse(JobCodes.NoCloudProfile);
            var decided = await cloud.DecideAsync(model, request.Options, request.Texts, request.DataClass, trace, ct);
            if (!decided.TryGetValue(out var result))
                return Refuse(decided.TryGetFailure(out var failure) ? failure!.Code : JobCodes.CloudFailed);
            var count = request.Options.Count;
            if (result.Decisions.Count != request.Texts.Count
                || result.Decisions.Any(d => d.Choice is < 0 || d.Choice >= count || d.Scores.Keys.Any(k => k < 0 || k >= count)))
                return Refuse(JobCodes.ResultInvalid);
            outcome.End(new ChatUsage(result.TokensIn ?? 0, 0, ChatStop.End, model));
            return Result.Success(new LlmDecisions(model, "cloud:" + trace.ProfileId, implementation.Name, implementation.Version, "cloud", result.Decisions));
        }

        Result<LlmDecisions> Refuse(string code)
        {
            outcome.Fail(code);
            return ModelFailures.Of(code);
        }
    }

    /// <summary>Whether <paramref name="model"/> is served by its cloud profile: <c>decide</c> set to <c>self_hosted_then_cloud</c>, as
    /// <see cref="ModelDigests"/> reads it.</summary>
    private async Task<bool> CloudAsync(string model, CancellationToken ct)
        => CloudModels.Of(profiles, ModelUses.Decide, model) is not null && (await settings.UseAsync(ModelUses.Decide, ct)).Mode == UseModes.SelfHostedThenCloud;
}
