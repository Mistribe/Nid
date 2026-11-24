using Nid.Ai.Contracts;
using Nid.Ai.Protocol.Contracts;
using Nid.Platform.Contracts;
using NodaTime;
using UnambitiousFx.Functional;

namespace Nid.Ai.Application;

/// <summary>The gateway's <c>embed</c> task (#558, <c>docs/ingestion/llm-providers.md</c> §9): the use's model and mode, the
/// data policy and the consent of the adults concerned, every call recorded in <c>llm_call</c> without its content.</summary>
public sealed class LlmEmbedder(
    IAiSettings settings, IModelRequests requests, IAssistantPeople people, ILlmCalls calls, IActorAccessor actors, IClock clock,
    ILlmProfiles profiles, CloudProvider cloud) : IEmbeddings
{
    public async Task<string?> ModelAsync(CancellationToken ct)
        => await settings.UseAsync(ModelUses.Embed, ct) is { Mode: not UseModes.Off, Model: { } model } ? model : null;

    /// <summary>#558: the <c>embed</c> use's model on the household's workers, local ones only without <c>allow</c> from each
    /// adult concerned. #556: a cloud profile's model, with the mode <c>self_hosted_then_cloud</c>, is that provider's alone (the
    /// index has one model, nothing falls back to a worker), within <c>allowed_kinds</c> and everyone's <c>allow</c>.</summary>
    public async Task<Result<LlmEmbeddings>> EmbedAsync(LlmEmbedRequest request, CancellationToken ct)
    {
        var started = clock.GetCurrentInstant();
        Guid? workerId = null;
        var trace = new LlmCallTrace();
        var outcome = new LlmGateway.Outcome();
        try
        {
            var use = await settings.UseAsync(ModelUses.Embed, ct);
            outcome.Model = use.Model;
            if (use.Mode == UseModes.Off || use.Model is null)
                return Refuse(use.Mode == UseModes.Off ? JobCodes.UseOff : JobCodes.NoModel);
            var input = new EmbedRequestInput(use.Model, request.Texts);
            if (!input.IsValid())
                return Refuse(JobCodes.InvalidInput);

            var offsite = await OffsiteConsent.AllowedAsync(people, request.Concerned ?? [], ct);
            if (use.Mode == UseModes.SelfHostedThenCloud && CloudEmbedModels.IsCloud(profiles, use.Model))
                return await CloudAsync(use.Model, offsite);

            // Without everyone's allow, a remote worker is skipped; noted, so the refusal says why rather than « no model ».
            var heldBack = false;
            Func<ServingWorker, bool>? onsite = offsite ? null : worker =>
            {
                if (worker.Placement == WorkerPlacement.Local)
                    return true;
                heldBack = true;
                return false;
            };
            var vectors = new IReadOnlyList<float>[request.Texts.Count];
            await foreach (var frame in requests.StreamAsync("embed", 1, ContractJson.Write(input), request.DataClass, null, ct, model: use.Model,
                               served: worker => workerId = worker.Id, only: onsite))
            {
                if (!frame.TryGet(out var data, out var failed))
                    return Refuse(!offsite && heldBack && failed!.Code == JobCodes.ModelUnavailable ? JobCodes.OffsiteNotAllowed : failed!.Code);
                var output = ContractJson.Read<EmbedRequestOutput>(data.Span);
                if (output.Index >= vectors.Length)
                    return Refuse(JobCodes.ResultInvalid);
                vectors[output.Index] = output.Vector;
            }

            return Embedded(use.Model, vectors, 0);
        }
        finally
        {
            await calls.RecordAsync(new LlmCallRecord(Guid.CreateVersion7(), started, actors.Current?.PersonId?.Value, "embed", ModelUses.Embed,
                workerId, null, request.JobId, outcome.Model, outcome.TokensIn, 0, outcome.Status, outcome.Code, trace.ProfileId, trace.Provider,
                trace.Tier, trace.ReservedEur, trace.CostEur), CancellationToken.None);
        }

        // #556 (docs/ai/README.md §6.1): local-only data never; a document only when each of its types is allowed (an unfiled one
        // has none); background work only with allow from each adult concerned, a search only with its reader's.
        async Task<Result<LlmEmbeddings>> CloudAsync(string model, bool offsite)
        {
            if (request.DataClass == DataClasses.LocalOnly)
                return Refuse(JobCodes.CloudNotAllowed);
            if (request.Kinds is { } kinds && (kinds.Count == 0 || kinds.Except(await settings.AllowedKindsAsync(ct)).Any()))
                return Refuse(JobCodes.CloudNotAllowed);
            if (!offsite)
                return Refuse(JobCodes.OffsiteNotAllowed);
            var embedded = await cloud.EmbedAsync(model, request.Texts, request.DataClass, trace, ct);
            return embedded.TryGetValue(out var result) ? Embedded(model, result.Vectors, result.TokensIn ?? 0) : Refuse(Code(embedded));
        }

        Result<LlmEmbeddings> Embedded(string model, IReadOnlyList<IReadOnlyList<float>?> vectors, int tokensIn)
        {
            if (vectors.Count != request.Texts.Count || vectors.Any(v => v is null) || vectors.Select(v => v!.Count).Distinct().Count() != 1
                || vectors[0]!.Count == 0)
                return Refuse(JobCodes.ResultInvalid);
            outcome.End(new ChatUsage(tokensIn, 0, ChatStop.End, model));
            return Result.Success(new LlmEmbeddings(model, vectors[0]!.Count, [.. vectors.Select(v => v!)]));
        }

        Result<LlmEmbeddings> Refuse(string code)
        {
            outcome.Fail(code);
            return ModelFailures.Of(code);
        }
    }

    private static string Code<T>(Result<T> failed) where T : notnull
        => failed.TryGetFailure(out var failure) ? failure!.Code : JobCodes.CloudFailed;
}
