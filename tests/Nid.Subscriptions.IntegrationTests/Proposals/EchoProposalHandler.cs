using System.Text.Json;
using Nid.Core.Contracts;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;

namespace Nid.Subscriptions.IntegrationTests.Proposals;

/// <summary>
/// A kind for the queue's own tests: `{ subject, note?, refuse?, applyFails?, applyThrows?, conforme? }`. Validation refuses when `refuse` is true;
/// application fails when `applyFails` is true, throws when `applyThrows` is true, else returns one new id.
/// </summary>
public sealed class EchoProposalHandler : IProposalHandler
{
    public const string KindName = "test.echo";
    public string Module => "test";
    public string Kind => KindName;
    public int PayloadVersion => 1;

    public Task<Result<ProposalCheck>> ValidateAsync(ItemId target, JsonElement payload, bool edited, CancellationToken ct)
        => Task.FromResult(payload.TryGetProperty("refuse", out var r) && r.ValueKind == JsonValueKind.True
            ? Result.Failure<ProposalCheck>(new Failure("test.refused", "Refused by the test handler.", null))
            : Result.Success(new ProposalCheck(payload.TryGetProperty("subject", out var s) ? s.GetString() : null,
                [new ProposalWarning("test.note", "info", "Echoed.")], Known(payload),
                payload.TryGetProperty("conforme", out var c) && c.ValueKind == JsonValueKind.True
                    ? JsonSerializer.SerializeToElement(new { expected = Known(payload), given = Known(payload) })
                    : null)));

    private static readonly string[] Fields = ["subject", "note", "refuse", "applyFails", "applyThrows", "conforme"];

    /// <summary>Its known fields only, as a real handler normalizes.</summary>
    private static JsonElement Known(JsonElement payload)
    {
        var known = new System.Text.Json.Nodes.JsonObject();
        foreach (var field in Fields)
            if (payload.TryGetProperty(field, out var value))
                known[field] = System.Text.Json.Nodes.JsonNode.Parse(value.GetRawText());
        return JsonSerializer.SerializeToElement(known);
    }

    public Task<JsonElement> PreviewAsync(ItemId target, JsonElement payload, CancellationToken ct)
        => Task.FromResult(JsonSerializer.SerializeToElement(new { proposed = payload }));

    public Task<Result<IReadOnlyList<Guid>>> ApplyAsync(ItemId target, JsonElement payload, CancellationToken ct)
        => payload.TryGetProperty("applyThrows", out var t) && t.ValueKind == JsonValueKind.True
            ? throw new InvalidOperationException("The test handler threw.")
            : Task.FromResult(payload.TryGetProperty("applyFails", out var f) && f.ValueKind == JsonValueKind.True
            ? Result.Failure<IReadOnlyList<Guid>>(new Failure("test.apply_failed", "The test handler failed to apply.", null))
            : Result.Success<IReadOnlyList<Guid>>([Guid.CreateVersion7()]));
}
