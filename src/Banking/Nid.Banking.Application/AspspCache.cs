using Nid.Banking.Contracts;
using NodaTime;
using UnambitiousFx.Functional;

namespace Nid.Banking.Application;

/// <summary>The French banks Enable Banking lists, kept a day per application (spec §3.1): a singleton, the list barely changes.</summary>
public sealed class AspspCache(IEnableBankingClient client, IClock clock)
{
    private sealed record Cached(Instant At, IReadOnlyList<Aspsp> Banks);

    private readonly Dictionary<string, Cached> cached = [];

    public async Task<Result<IReadOnlyList<Aspsp>>> FrenchAsync(EnableBankingApp app, CancellationToken ct)
    {
        var now = clock.GetCurrentInstant();
        lock (cached)
            if (cached.TryGetValue(app.Id, out var kept) && now - kept.At < Duration.FromDays(1))
                return Result.Success(kept.Banks);

        var read = await client.AspspsAsync(app, "FR", ct);
        if (read.TryGetValue(out var banks))
            lock (cached)
                cached[app.Id] = new Cached(now, banks!);
        return read;
    }
}
