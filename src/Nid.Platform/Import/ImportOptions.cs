namespace Nid.Platform.Import;

/// <summary>The bounds of <c>import-format.md</c> §11.5. Defaults are the spec's; tests replace the instance.</summary>
public sealed record ImportOptions
{
    public int MaxEntitiesPerBatch { get; init; } = 500;
    public int MaxBatchBytes { get; init; } = 1_048_576;
    public int MaxEntitiesPerSession { get; init; } = 50_000;
    public int MaxDepth { get; init; } = 32;
    public int MaxStringLength { get; init; } = 10_000;
    public TimeSpan OperationTimeout { get; init; } = TimeSpan.FromSeconds(60);
    public TimeSpan SessionLifetime { get; init; } = TimeSpan.FromHours(24);
    public int ListLimit { get; init; } = 20;
}
