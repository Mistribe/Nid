using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Nid.Core.Domain;

/// <summary>
/// tokens-and-credentials.md §1.2: <c>nid_pat_&lt;prefix&gt;_&lt;secret&gt;</c>. The prefix finds the row without exposing
/// the secret; the whole token is hashed with SHA-256 — a 256-bit secret needs no slow hash.
/// </summary>
public static partial class TokenFormat
{
    private const string Lead = "nid_pat_";
    private const string PrefixAlphabet = "abcdefghijklmnopqrstuvwxyz0123456789";

    public static (string Token, string Prefix, string Hash) Generate()
    {
        var prefix = RandomNumberGenerator.GetString(PrefixAlphabet, 8);
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var token = $"{Lead}{prefix}_{secret}";
        return (token, prefix, Hash(token));
    }

    /// <summary>Exact shape only: no trimming, so a token never matches loosely.</summary>
    public static bool TryReadPrefix(string? token, out string prefix)
    {
        prefix = "";
        if (token is null || !Shape().IsMatch(token))
            return false;
        prefix = token.Substring(Lead.Length, 8);
        return true;
    }

    public static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public static bool Matches(string token, string hash)
        => CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Hash(token)), Encoding.ASCII.GetBytes(hash));

    [GeneratedRegex("^nid_pat_[a-z0-9]{8}_[A-Za-z0-9_-]{43}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex Shape();
}
