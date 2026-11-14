using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace Nid.Host.Auth;

public enum PasswordCheck
{
    Failed,
    Success,

    /// <summary>The password is right but was hashed with weaker parameters: the caller stores <see cref="LocalPasswordHasher.Hash"/> again.</summary>
    SuccessRehashNeeded,
}

/// <summary>
/// local-accounts.md §3: ASP.NET Core Identity's <see cref="PasswordHasher{TUser}"/> (format V3, PBKDF2-HMAC-SHA512, a
/// random 128-bit salt) at <see cref="Iterations"/>. The format carries its own parameters, so raising the constant makes
/// every older hash answer <see cref="PasswordCheck.SuccessRehashNeeded"/> at its next sign-in. At most
/// <c>Auth:Local:MaxConcurrentHashes</c> hashes run at once; past 5 seconds of waiting the caller is told to answer 429.
/// </summary>
public sealed class LocalPasswordHasher : IDisposable
{
    /// <summary>OWASP's figure for PBKDF2-HMAC-SHA512. Raise it in a release; never lower it.</summary>
    public const int Iterations = 210_000;

    private static readonly object User = new();
    private readonly PasswordHasher<object> hasher = new(Options.Create(new PasswordHasherOptions
    {
        CompatibilityMode = PasswordHasherCompatibilityMode.IdentityV3,
        IterationCount = Iterations,
    }));

    private readonly SemaphoreSlim slots;
    private readonly Lazy<string> dummy;

    public LocalPasswordHasher(IOptions<NidAuthOptions> options)
    {
        slots = new SemaphoreSlim(options.Value.Local.Bounded().MaxConcurrentHashes);
        // Verified when the identifier is unknown, so the answer takes as long as for a known one (spec §3.1).
        dummy = new Lazy<string>(() => hasher.HashPassword(User, "nid-dummy-" + Guid.NewGuid().ToString("N")));
    }

    /// <summary>A hash in the V3 format. <c>null</c> when no slot freed within 5 seconds.</summary>
    public async Task<string?> HashAsync(string password, CancellationToken ct)
    {
        if (!await slots.WaitAsync(TimeSpan.FromSeconds(5), ct))
            return null;
        try { return hasher.HashPassword(User, password); }
        finally { slots.Release(); }
    }

    /// <summary><c>null</c> when no slot freed within 5 seconds. A null <paramref name="hash"/> checks against the dummy hash and fails.</summary>
    public async Task<PasswordCheck?> VerifyAsync(string? hash, string password, CancellationToken ct)
    {
        if (!await slots.WaitAsync(TimeSpan.FromSeconds(5), ct))
            return null;
        try
        {
            var result = hasher.VerifyHashedPassword(User, hash ?? dummy.Value, password);
            if (hash is null) return PasswordCheck.Failed;
            return result switch
            {
                PasswordVerificationResult.Success => PasswordCheck.Success,
                PasswordVerificationResult.SuccessRehashNeeded => PasswordCheck.SuccessRehashNeeded,
                _ => PasswordCheck.Failed,
            };
        }
        catch (FormatException)
        {
            return PasswordCheck.Failed;
        }
        finally { slots.Release(); }
    }

    public void Dispose() => slots.Dispose();
}
