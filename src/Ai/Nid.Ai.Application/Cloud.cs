using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Nid.Ai.Contracts;
using Nid.Ai.Protocol.Contracts;
using NodaTime;

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
    Pricing? Pricing = null)
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
/// </summary>
public sealed class CloudProvider(
    ILlmProfiles profiles, ICloudSettings settings, IEnumerable<ICloudAdapter> adapters, CloudBreaker breaker, IClock clock) : ILlmProvider
{
    /// <summary>The classes that may reach a cloud provider today: nothing, or figures in the chat (§6.1).</summary>
    private static readonly HashSet<string> CloudClasses = [DataClasses.None, DataClasses.Figures];

    public int Order => 1;

    public bool Allows(string mode) => mode == UseModes.SelfHostedThenCloud;

    public async IAsyncEnumerable<ChatRequestOutput> ChatAsync(
        ChatRequestInput input, string dataClass, LlmCallTrace trace, [EnumeratorCancellation] CancellationToken ct)
    {
        if (!CloudClasses.Contains(dataClass))
            throw new ModelCallException(JobCodes.CloudNotAllowed);
        var region = await settings.RegionAsync(ct);
        // A profile is used under the terms it was enabled on: terms reviewed since must be accepted again.
        var enabled = (await settings.EnabledAsync(ct)).ToDictionary(e => e.ProfileId, e => e.AcceptedReviewedOn, StringComparer.Ordinal);
        var candidates = profiles.All
            .Where(p => enabled.TryGetValue(p.Id, out var accepted) && accepted == p.DataTerms.ReviewedOn && region is not null && p.EligibleIn(region) && p.Capabilities.Contains(ModelUses.Chat)
                        && !breaker.IsSuspended(p.Id))
            .ToList();

        foreach (var profile in candidates)
        {
            if (adapters.FirstOrDefault(a => a.Provider == profile.Provider) is not { } adapter
                || await settings.KeyAsync(profile.Provider, clock.GetCurrentInstant(), ct) is not { } key)
                continue;

            await using var outputs = adapter.ChatAsync(profile, key, input with { Model = profile.Model }, ct).GetAsyncEnumerator(ct);
            bool any;
            try
            {
                any = await outputs.MoveNextAsync();
            }
            catch (CloudCallException e) when (e.Kind == CloudFailure.Transient)
            {
                breaker.Failed(profile.Id);
                continue; // quota, 429, 5xx: the next profile (§5.3)
            }
            catch (CloudCallException e)
            {
                throw new ModelCallException(e.Code);
            }

            (trace.ProfileId, trace.Provider, trace.Tier) = (profile.Id, profile.Provider, profile.Tier);
            while (any)
            {
                yield return outputs.Current;
                any = await NextAsync(outputs);
            }

            breaker.Succeeded(profile.Id);
            yield break;
        }

        throw new ModelCallException(JobCodes.NoCloudProfile);
    }

    private static async Task<bool> NextAsync(IAsyncEnumerator<ChatRequestOutput> outputs)
    {
        try
        {
            return await outputs.MoveNextAsync();
        }
        catch (CloudCallException e)
        {
            throw new ModelCallException(e.Code); // after output: no other profile can take the rest
        }
    }
}
