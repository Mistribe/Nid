using Nid.Ai.Contracts;
using Nid.Ai.Protocol.Contracts;
using Nid.Platform.Contracts;
using NodaTime;
using UnambitiousFx.Functional;

namespace Nid.Ai.Application;

/// <summary>
/// #450, the gateway's <c>decide</c> task (<c>docs/ai/README.md</c> §7) as <see cref="LlmEmbedder"/>: the use's model and mode, the data
/// policy and the consent of the adults concerned, every call recorded in <c>llm_call</c> without its content. No cloud adapter serves it:
/// <c>self_hosted_then_cloud</c> goes no further than the workers.
/// </summary>
public sealed class LlmDecider(
    IAiSettings settings, IModelRequests requests, IAssistantPeople people, ILlmCalls calls, IActorAccessor actors, IClock clock, IWorkerStore workers)
    : IDecisions
{
    public async Task<string?> ModelAsync(CancellationToken ct)
        => await settings.UseAsync(ModelUses.Decide, ct) is { Mode: not UseModes.Off, Model: { } model } ? model : null;

    public async Task<LlmDecideImplementation?> ImplementationAsync(string model, CancellationToken ct)
    {
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
                worker?.Id, null, null, outcome.Model, 0, 0, outcome.Status, outcome.Code, null, null, null, null, null), CancellationToken.None);
        }

        Result<LlmDecisions> Refuse(string code)
        {
            outcome.Fail(code);
            return ModelFailures.Of(code);
        }
    }
}
