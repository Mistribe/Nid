using Nid.Banking.Contracts;
using NodaTime;
using UnambitiousFx.Functional;

namespace Nid.Banking.Application;

/// <summary>The French banks Enable Banking lists, kept a day (spec §3.1): a singleton, the list barely changes.</summary>
public sealed class AspspCache(IEnableBankingClient client, IClock clock)
{
    private sealed record Cached(Instant At, IReadOnlyList<Aspsp> Banks);

    private Cached? cached;

    public async Task<Result<IReadOnlyList<Aspsp>>> FrenchAsync(string appId, CancellationToken ct)
    {
        var now = clock.GetCurrentInstant();
        if (cached is { } kept && now - kept.At < Duration.FromDays(1))
            return Result.Success(kept.Banks);

        var read = await client.AspspsAsync(appId, "FR", ct);
        if (read.TryGetValue(out var banks))
            cached = new Cached(now, banks!);
        return read;
    }
}
