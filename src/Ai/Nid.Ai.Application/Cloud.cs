using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Nid.Ai.Contracts;
using Nid.Ai.Protocol.Contracts;
using NodaTime;
using UnambitiousFx.Functional;

namespace Nid.Ai.Application;

/// <summary>
/// A cloud profile (#487, <c>docs/ingestion/llm-providers.md</c> §4): a reviewed provider offer, versioned with Nid in
/// <c>deploy/llm-profiles/</c>, never in the database. The model is the profile's, whatever the use asked of a worker.
/// </summary>
public sealed record LlmProfile(
    string Id,
    string Provider,
    string Tier,
    string Host,
    string Model,
    IReadOnlyList<string> Capabilities,
    DataTerms DataTerms,
    Quotas? Quotas = null,
    Pricing? Pricing = null,
    int ContextTokens = 0)
{
    /// <summary>Its terms never let the provider train on the data in <paramref name="region"/>.</summary>
    public bool EligibleIn(string region) => DataTerms.TrainsOnData.TryGetValue(region, out var trains) && !trains;
}

public sealed record DataTerms(
    IReadOnlyDictionary<string, bool> TrainsOnData,
    bool HumanReview,
    int? RetentionDays,
    IReadOnlyList<string> RetentionSwitches,
    string TermsUrl,
    LocalDate ReviewedOn);

public sealed record Quotas(int? Rpm = null, int? Rpd = null, int? TokensPerDay = null);

public sealed record Pricing(decimal InputPerMillion, decimal OutputPerMillion, string Currency);

/// <summary>The profiles Nid ships, read and checked at start; a profile that fails its checks is not loaded.</summary>
public interface ILlmProfiles
{
    IReadOnlyList<LlmProfile> All { get; }
}

/// <summary>A profile the household enabled: the terms it accepted (their review date).</summary>
public sealed record EnabledProfile(string ProfileId, LocalDate AcceptedReviewedOn);

/// <summary>The household's cloud settings and provider keys (§10): a key is stored encrypted and read only to call.</summary>
public interface ICloudSettings
{
    Task<string?> RegionAsync(CancellationToken ct);

    Task SetRegionAsync(string region, CancellationToken ct);

    Task<IReadOnlyList<EnabledProfile>> EnabledAsync(CancellationToken ct);

    Task EnableAsync(string profileId, LocalDate acceptedReviewedOn, Guid person, Instant at, CancellationToken ct);

    Task<bool> DisableAsync(string profileId, CancellationToken ct);

    /// <summary>The last four characters of each provider's active key.</summary>
    Task<IReadOnlyDictionary<string, string>> KeyHintsAsync(CancellationToken ct);

    /// <summary>Replaces the provider's active key: the previous one is revoked.</summary>
    Task SetKeyAsync(string provider, string key, Guid person, Instant at, CancellationToken ct);

    Task<bool> RevokeKeyAsync(string provider, Instant at, CancellationToken ct);

    /// <summary>The active key in clear, for a call; never logged, never sent elsewhere.</summary>
    Task<string?> KeyAsync(string provider, Instant usedAt, CancellationToken ct);
}

/// <summary>How a provider failed a call: worth another profile (quota, 429, 5xx, network) or not (a request it refuses).</summary>
public enum CloudFailure
{
    Transient,
    Refused,
}

public sealed class CloudCallException(CloudFailure kind, string code) : Exception($"The provider failed the call ({code}).")
{
    public CloudFailure Kind { get; } = kind;

    public string Code { get; } = code;
}

/// <summary>One provider's API, hand-written (decided 2026-11-22): the neutral chat contract in, its stream out.</summary>
public interface ICloudAdapter
{
    string Provider { get; }

    IAsyncEnumerable<ChatRequestOutput> ChatAsync(LlmProfile profile, string key, ChatRequestInput input, CancellationToken ct);
}

/// <summary>§5.3: three failures in a row suspend a profile for 15 minutes; a success clears its count.</summary>
public sealed class CloudBreaker(IClock clock)
{
    public const int Failures = 3;
    public static readonly Duration SuspendFor = Duration.FromMinutes(15);

    private readonly ConcurrentDictionary<string, (int Failures, Instant? Until)> _state = new();

    public bool IsSuspended(string profileId)
        => _state.TryGetValue(profileId, out var s) && s.Until is { } until && clock.GetCurrentInstant() < until;

    public void Succeeded(string profileId) => _state.TryRemove(profileId, out _);

    public void Failed(string profileId)
        => _state.AddOrUpdate(profileId, _ => (1, null), (_, s) =>
        {
            var failures = s.Until is { } until && clock.GetCurrentInstant() >= until ? 1 : s.Failures + 1;
            return failures >= Failures ? (0, clock.GetCurrentInstant() + SuspendFor) : (failures, s.Until);
        });
}

/// <summary>
/// The enabled cloud profiles, after the workers (§5.3), for a use set to <c>self_hosted_then_cloud</c>: eligible in the
/// household's region, with a key, not suspended. A transient failure before any output moves to the next profile; a
/// refusal does not. Local-only data never goes there; documents and bank labels wait for <c>allowed_kinds</c> (§6.1).
/// A paid profile first reserves the turn's upper bound on the month's budget (#488): over the ceiling, or without an
/// exchange rate, it is skipped, and with nothing else left the call is blocked.
/// </summary>
public sealed class CloudProvider(
    ILlmProfiles profiles, ICloudSettings settings, IEnumerable<ICloudAdapter> adapters, CloudBreaker breaker, IClock clock,
    IBudget budget, BudgetPrices prices, BudgetNotices notices) : ILlmProvider
{
    /// <summary>The classes that may reach a cloud provider today: nothing, or figures in the chat (§6.1).</summary>
    private static readonly HashSet<string> CloudClasses = [DataClasses.None, DataClasses.Figures];

    public int Order => 1;

    public bool Allows(string mode) => mode == UseModes.SelfHostedThenCloud;

    public async IAsyncEnumerable<Result<ChatRequestOutput>> ChatAsync(
        ChatRequestInput input, string dataClass, LlmCallTrace trace, [EnumeratorCancellation] CancellationToken ct)
    {
        if (!CloudClasses.Contains(dataClass))
        {
            yield return ModelFailures.Of(JobCodes.CloudNotAllowed);
            yield break;
        }
        if (trace.Pin is { } pinned && !LlmClasses.IsCloud(pinned))
        {
            yield return ModelFailures.Of(JobCodes.NoCloudProfile); // pinned to a worker class: no provider may see this turn
            yield break;
        }
        var region = await settings.RegionAsync(ct);
        // A profile is used under the terms it was enabled on: terms reviewed since must be accepted again.
        var enabled = (await settings.EnabledAsync(ct)).ToDictionary(e => e.ProfileId, e => e.AcceptedReviewedOn, StringComparer.Ordinal);
        var candidates = profiles.All
            .Where(p => enabled.TryGetValue(p.Id, out var accepted) && accepted == p.DataTerms.ReviewedOn && region is not null && p.EligibleIn(region)
                        && p.Capabilities.Contains(ModelUses.Chat) && !breaker.IsSuspended(p.Id)
                        && (trace.Pin is null || LlmClasses.Cloud + p.Id == trace.Pin))
            .ToList();

        var blocked = false;
        foreach (var profile in candidates)
        {
            if (adapters.FirstOrDefault(a => a.Provider == profile.Provider) is not { } adapter
                || await settings.KeyAsync(profile.Provider, clock.GetCurrentInstant(), ct) is not { } key)
                continue;

            var turn = input with { Model = profile.Model };
            var hold = await ReserveAsync(profile, turn, ct);
            if (hold is { Blocked: true })
            {
                blocked = true; // over the month's ceiling, or no rate to price it: a free profile may still take it
                continue;
            }

            var spent = (decimal?)null; // what the turn is settled at, once known
            try
            {
                await using var outputs = adapter.ChatAsync(profile, key, turn, ct).GetAsyncEnumerator(ct);
                bool any;
                string? refused = null;
                try
                {
                    any = await outputs.MoveNextAsync();
                }
                catch (CloudCallException e) when (e.Kind == CloudFailure.Transient)
                {
                    spent = 0; // refused before answering: nothing billed
                    breaker.Failed(profile.Id);
                    continue; // quota, 429, 5xx: the next profile (§5.3)
                }
                catch (CloudCallException e)
                {
                    (spent, refused, any) = (0, e.Code, false);
                }

                if (refused is not null)
                {
                    yield return ModelFailures.Of(refused);
                    yield break;
                }

                (trace.ProfileId, trace.Provider, trace.Tier, trace.ReservedEur) = (profile.Id, profile.Provider, profile.Tier, hold?.Amount);
                trace.Destination = new LlmDestination(LlmPlacement.Cloud, profile.Provider, ProfileId: profile.Id, ContextTokens: profile.ContextTokens);
                ChatUsage? usage = null;
                while (any)
                {
                    usage = outputs.Current.Usage ?? usage;
                    yield return outputs.Current;
                    (any, var cut) = await NextAsync(outputs);
                    if (cut is not null)
                    {
                        yield return ModelFailures.Of(cut); // after output: no other profile can take the rest
                        yield break;
                    }
                }

                breaker.Succeeded(profile.Id);
                if (usage is not null && profile.Pricing is { } pricing)
                    spent = await prices.EurAsync(pricing, usage.TokensIn, usage.TokensOut, ct);
                trace.CostEur = spent ?? hold?.Amount;
                yield break;
            }
            finally
            {
                // A turn cut without its usage is settled at what it held back (§7).
                if (hold is { Reservation: { } reservation, Amount: var amount }
                    && await budget.SettleAsync(reservation, spent ?? amount, CancellationToken.None) is { } settled)
                    await notices.AfterAsync(settled, CancellationToken.None);
            }
        }

        yield return ModelFailures.Of(blocked ? JobCodes.BudgetBlocked : JobCodes.NoCloudProfile);
    }

    /// <summary>A paid profile's hold on the month's budget; null for a free one.</summary>
    private async Task<Hold?> ReserveAsync(LlmProfile profile, ChatRequestInput turn, CancellationToken ct)
    {
        if (profile.Tier != "paid" || profile.Pricing is not { } pricing)
            return null;
        var bound = await prices.UpperBoundAsync(pricing, new ChatRequestInputSize(ContractJson.Write(turn).Length, turn.MaxTokens), ct);
        if (bound is not { } amount)
            return new Hold(null, 0, Blocked: true); // with no rate, a paid call is blocked (§7)
        var now = clock.GetCurrentInstant();
        var reservation = await budget.ReserveAsync(BudgetMonths.Of(now), amount, now + BudgetMonths.ReservationFor, ct);
        return reservation is null ? new Hold(null, amount, Blocked: true) : new Hold(reservation, amount, Blocked: false);
    }

    private sealed record Hold(Guid? Reservation, decimal Amount, bool Blocked);

    /// <summary>The next output, or the provider's failure code.</summary>
    private static async Task<(bool More, string? Failure)> NextAsync(IAsyncEnumerator<ChatRequestOutput> outputs)
    {
        try
        {
            return (await outputs.MoveNextAsync(), null);
        }
        catch (CloudCallException e)
        {
            return (false, e.Code);
        }
    }
}

/// <summary>Settles the reservations no answer settled (a crash, a lost stream), at what they held back.</summary>
public interface IBudgetSweep
{
    Task RunOnceAsync(CancellationToken ct);
}

public sealed class BudgetSweep(IBudget budget, BudgetNotices notices, IClock clock) : IBudgetSweep
{
    public async Task RunOnceAsync(CancellationToken ct)
    {
        foreach (var settled in await budget.SettleExpiredAsync(clock.GetCurrentInstant(), ct))
            await notices.AfterAsync(settled, ct);
    }
}
