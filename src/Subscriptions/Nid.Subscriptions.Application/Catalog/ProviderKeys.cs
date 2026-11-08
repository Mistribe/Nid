using System.Security.Cryptography;
using Nid.Subscriptions.Contracts;
using Nid.Subscriptions.Domain.Catalog;

namespace Nid.Subscriptions.Application.Catalog;

/// <summary>The six random characters of a custom key. An interface so a test can force a collision.</summary>
public interface IProviderKeySuffix
{
    string Next();
}

public sealed class RandomProviderKeySuffix : IProviderKeySuffix
{
    public string Next() => RandomNumberGenerator.GetString("abcdefghijklmnopqrstuvwxyz0123456789", ProviderRules.SuffixLength);
}

/// <summary>
/// Design §3.3: <c>&lt;slug&gt;-&lt;6 random [a-z0-9]&gt;</c>. A catalog key carries no such suffix, a custom key is
/// never chosen: a collision is still checked, against every origin, and another suffix drawn.
/// </summary>
public sealed class ProviderKeys(IProviderStore providers, IProviderKeySuffix suffix)
{
    public const int Attempts = 5;

    public async Task<string> NewAsync(string name, CancellationToken ct)
    {
        var slug = ProviderRules.Slug(name);
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            var key = $"{slug}-{suffix.Next()}";
            if (!await providers.ExistsAsync(key, ct))
                return key;
        }

        throw new InvalidOperationException($"No free key for '{slug}' after {Attempts} attempts.");
    }
}
