using System.Text.Json;
using Nid.Core.Contracts;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;

namespace Nid.Subscriptions.IntegrationTests.Proposals;

/// <summary>
/// A kind for the queue's own tests: `{ subject, refuse?, applyFails? }`. Validation refuses when `refuse` is true;
/// application fails when `applyFails` is true, else returns one new id.
/// </summary>
public sealed class EchoProposalHandler : IProposalHandler
{
    public const string KindName = "test.echo";
    public string Module => "test";
    public string Kind => KindName;
    public int PayloadVersion => 1;

    public Task<Result<ProposalCheck>> ValidateAsync(ItemId target, JsonElement payload, CancellationToken ct)
        => Task.FromResult(payload.TryGetProperty("refuse", out var r) && r.ValueKind == JsonValueKind.True
            ? Result.Failure<ProposalCheck>(new Failure("test.refused", "Refused by the test handler.", null))
            : Result.Success(new ProposalCheck(payload.TryGetProperty("subject", out var s) ? s.GetString() : null,
                [new ProposalWarning("test.note", "info", "Echoed.")])));

    public Task<JsonElement> PreviewAsync(ItemId target, JsonElement payload, CancellationToken ct)
        => Task.FromResult(JsonSerializer.SerializeToElement(new { proposed = payload }));

    public Task<Result<IReadOnlyList<Guid>>> ApplyAsync(ItemId target, JsonElement payload, CancellationToken ct)
        => Task.FromResult(payload.TryGetProperty("applyFails", out var f) && f.ValueKind == JsonValueKind.True
            ? Result.Failure<IReadOnlyList<Guid>>(new Failure("test.apply_failed", "The test handler failed to apply.", null))
            : Result.Success<IReadOnlyList<Guid>>([Guid.CreateVersion7()]));
}
