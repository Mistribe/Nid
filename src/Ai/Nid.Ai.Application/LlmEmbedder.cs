using Nid.Ai.Contracts;
using Nid.Ai.Protocol.Contracts;
using Nid.Platform.Contracts;
using NodaTime;
using UnambitiousFx.Functional;

namespace Nid.Ai.Application;

/// <summary>The gateway's <c>embed</c> task (#558, <c>docs/ingestion/llm-providers.md</c> §9): the use's model and mode, the
/// data policy and the consent of the adults concerned, every call recorded in <c>llm_call</c> without its content.</summary>
public sealed class LlmEmbedder(
    IAiSettings settings, IModelRequests requests, IAssistantPeople people, ILlmCalls calls, IActorAccessor actors, IClock clock) : IEmbeddings
{
    /// <summary>#558: the <c>embed</c> use's model on the household's workers, local ones only without <c>allow</c> from each
    /// adult concerned. A mode that would open the cloud stays on the workers until #556.</summary>
    public async Task<Result<LlmEmbeddings>> EmbedAsync(LlmEmbedRequest request, CancellationToken ct)
    {
        var started = clock.GetCurrentInstant();
        Guid? workerId = null;
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

            if (vectors.Any(v => v is null) || vectors.Select(v => v.Count).Distinct().Count() != 1)
                return Refuse(JobCodes.ResultInvalid);
            outcome.End(new ChatUsage(0, 0, ChatStop.End, use.Model));
            return Result.Success(new LlmEmbeddings(use.Model, vectors[0].Count, vectors));
        }
        finally
        {
            await calls.RecordAsync(new LlmCallRecord(Guid.CreateVersion7(), started, actors.Current?.PersonId?.Value, "embed", ModelUses.Embed,
                workerId, null, request.JobId, outcome.Model, 0, 0, outcome.Status, outcome.Code), CancellationToken.None);
        }

        Result<LlmEmbeddings> Refuse(string code)
        {
            outcome.Fail(code);
            return ModelFailures.Of(code);
        }
    }
}
