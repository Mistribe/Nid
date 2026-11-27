using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Nid.Ai.Application;
using Nid.Ai.Contracts;
using Nid.Ai.Protocol.Contracts;

namespace Nid.Ai.Infrastructure.Cloud;

/// <summary>
/// #812: Jev, TypeSafe AI's System One API (<c>POST /v1/systemone</c>, <c>Authorization: Bearer</c>), hand-written from its public
/// documentation (<c>docs.typesafe.ai/primitives/choice.md</c>, read on 2026-11-27) and never yet called with a real key. It generates no
/// text: its <c>choice</c> primitive gives the probability of each option. One request per text, the text as the <c>state</c> and one
/// question whose criteria are the option names: a question reads the whole state, so texts sharing one request would read each other.
/// The probabilities go back as log-probabilities, which Nid's calibration reads as any worker's scores. No chat, no embeddings.
/// </summary>
public sealed class JevAdapter(CloudHttp cloud) : ICloudAdapter
{
    /// <summary>The question asked of every text; changing it is a new version: the household calibrates again.</summary>
    public const string Instructions = "Dans laquelle des catégories du foyer classer cette opération bancaire ?";

    private const string Question = "category";

    // ponytail: a probability of 0 has no log; this floor stands for it, far below any option the model gave a chance.
    private const double Floor = 1e-9;

    public string Provider => "typesafe";

    public LlmDecideImplementation DecideImplementation { get; } = new("jev-choice", "1", Scoreless: false);

    public IAsyncEnumerable<ChatRequestOutput> ChatAsync(LlmProfile profile, string key, ChatRequestInput input, CancellationToken ct)
        => throw new CloudCallException(CloudFailure.Refused, JobCodes.CloudRefused);

    public async Task<CloudDecisions> DecideAsync(LlmProfile profile, string key, IReadOnlyList<string> options, IReadOnlyList<string> texts, CancellationToken ct)
    {
        // The options are the criteria's keys: two of the same name could not be told apart.
        if (options.Distinct(StringComparer.Ordinal).Count() != options.Count)
            throw new CloudCallException(CloudFailure.Refused, JobCodes.InvalidInput);
        var criteria = options.ToDictionary(o => o, _ => (string?)null, StringComparer.Ordinal);
        var place = options.Select((o, i) => (o, i)).ToDictionary(o => o.o, o => o.i, StringComparer.Ordinal);

        var decisions = new List<LlmDecision>(texts.Count);
        var tokens = 0;
        foreach (var text in texts)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri($"https://{profile.Host}/v1/systemone"))
            {
                Content = JsonContent.Create(new JevRequest(profile.Model, text,
                    new Dictionary<string, JevQuestion> { [Question] = new("choice", Instructions, criteria) }), JevJson.Default.JevRequest),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            var answer = await CloudJson.SendAsync(cloud, request, JevJson.Default.JevResponse, ct);
            if (answer.Answers?.GetValueOrDefault(Question) is not { Type: "choice", Choice: { } choice, Probabilities: { Count: > 0 } probabilities }
                || !place.TryGetValue(choice, out var chosen) || probabilities.Keys.Any(k => !place.ContainsKey(k))
                || probabilities.Values.Any(p => !double.IsFinite(p) || p is < 0 or > 1))
                throw new CloudCallException(CloudFailure.Refused, JobCodes.CloudFailed);
            decisions.Add(new LlmDecision(chosen, probabilities.ToDictionary(p => place[p.Key], p => Math.Log(Math.Max(p.Value, Floor)))));
            tokens += answer.Usage?.InputTokens ?? 0;
        }

        return new CloudDecisions(decisions, tokens);
    }
}

internal sealed record JevRequest(string Model, string State, IReadOnlyDictionary<string, JevQuestion> Questions);

internal sealed record JevQuestion(string Type, string Instructions, IReadOnlyDictionary<string, string?> Criteria);

internal sealed record JevResponse(string? Model, IReadOnlyDictionary<string, JevAnswer>? Answers, JevUsage? Usage);

internal sealed record JevAnswer(string? Type, string? Choice, double? Confidence, IReadOnlyDictionary<string, double>? Probabilities);

internal sealed record JevUsage(int? InputTokens, int? OutputTokens);

/// <summary>The System One wire format: snake_case, unknown fields ignored. A criterion's description is null on purpose (the option's
/// name says it all), so nulls are written.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(JevRequest))]
[JsonSerializable(typeof(JevResponse))]
internal sealed partial class JevJson : JsonSerializerContext;
