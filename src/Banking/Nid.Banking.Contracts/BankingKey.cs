using System.Security.Cryptography;

namespace Nid.Banking.Contracts;

/// <summary>An adult's Enable Banking private key (#382), as « Mon application » accepts it.</summary>
public static class BankingKey
{
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
