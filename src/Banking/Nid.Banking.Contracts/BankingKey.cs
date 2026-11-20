using System.Security.Cryptography;

namespace Nid.Banking.Contracts;

/// <summary>The values of <see cref="BankingKey.Status"/>, as the admin banking API writes them.</summary>
public static class BankingKeyStatus
{
    /// <summary>Nothing at <c>&lt;Secrets:Path&gt;/EnableBanking__PrivateKey</c> and no other setting.</summary>
    public const string Missing = "missing";

    /// <summary>A folder in the file's place: Docker makes one when a bind mount's source does not exist.</summary>
    public const string Directory = "directory";

    /// <summary>A file Nid's user may not read.</summary>
    public const string Unreadable = "unreadable";

    /// <summary>Something that is not an RSA private key in PEM (an empty file, a certificate, a public key, an encrypted key).</summary>
    public const string Invalid = "invalid";

    public const string Loaded = "loaded";
}

/// <summary>
/// Issue #343: where the Enable Banking private key stands on this server, read once at startup like the key itself. Never holds the key.
/// </summary>
public sealed record BankingKey(string Status)
{
    /// <summary>The Docker secret's file name, <c>EnableBanking:PrivateKey</c> once read by key-per-file configuration.</summary>
    public const string FileName = "EnableBanking__PrivateKey";

    /// <summary>
    /// <paramref name="configured"/> is the key configuration holds: from the secret file, or from an environment variable, in which case
    /// only <see cref="BankingKeyStatus.Loaded"/> and <see cref="BankingKeyStatus.Invalid"/> apply.
    /// </summary>
    public static BankingKey Inspect(string secretsPath, string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
            return new(IsRsaPrivateKey(configured) ? BankingKeyStatus.Loaded : BankingKeyStatus.Invalid);
        var path = Path.Combine(secretsPath, FileName);
        if (System.IO.Directory.Exists(path))
            return new(BankingKeyStatus.Directory);
        if (!File.Exists(path))
            return new(BankingKeyStatus.Missing);
        return new(CanRead(path) ? BankingKeyStatus.Invalid : BankingKeyStatus.Unreadable); // read, yet empty
    }

    public static bool CanRead(string path)
    {
        try
        {
            using var _ = File.OpenRead(path);
            return true;
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    public static bool IsRsaPrivateKey(string pem)
    {
        try
        {
            using var rsa = RSA.Create();
            rsa.ImportFromPem(pem);
            // A public key imports too: only a private one exports its private part.
            rsa.ExportParameters(includePrivateParameters: true);
            return true;
        }
        catch (Exception e) when (e is ArgumentException or CryptographicException)
        {
            return false;
        }
    }
}
