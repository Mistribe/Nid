using System.Runtime.Versioning;
using System.Security.Cryptography;
using Nid.Banking.Contracts;

namespace Nid.Banking.Tests;

/// <summary>Issue #343: each state of the Enable Banking key, from a throwaway secrets folder.</summary>
public sealed class BankingKeyTests : IDisposable
{
    private readonly string secrets = Directory.CreateTempSubdirectory("nid-secrets-").FullName;

    private string KeyPath => Path.Combine(secrets, BankingKey.FileName);

    public void Dispose()
    {
        if (File.Exists(KeyPath) && !OperatingSystem.IsWindows())
            File.SetUnixFileMode(KeyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        Directory.Delete(secrets, recursive: true);
    }

    private static string RsaPem()
    {
        using var rsa = RSA.Create(2048);
        return rsa.ExportPkcs8PrivateKeyPem();
    }

    [Fact]
    public void Nothing_there_is_missing()
        => Assert.Equal(BankingKeyStatus.Missing, BankingKey.Inspect(secrets, null).Status);

    [Fact]
    public void A_secrets_folder_that_does_not_exist_is_missing()
        => Assert.Equal(BankingKeyStatus.Missing, BankingKey.Inspect(Path.Combine(secrets, "absent"), "").Status);

    [Fact]
    public void A_folder_in_the_file_s_place_is_a_directory()
    {
        // What Docker makes of a bind mount whose source does not exist, with the .pem inside it.
        Directory.CreateDirectory(KeyPath);
        File.WriteAllText(Path.Combine(KeyPath, "key.pem"), RsaPem());

        Assert.Equal(BankingKeyStatus.Directory, BankingKey.Inspect(secrets, null).Status);
    }

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public void A_file_nid_may_not_read_is_unreadable()
    {
        File.WriteAllText(KeyPath, RsaPem());
        File.SetUnixFileMode(KeyPath, UnixFileMode.None);
        Assert.SkipWhen(BankingKey.CanRead(KeyPath), "root reads every file");

        Assert.Equal(BankingKeyStatus.Unreadable, BankingKey.Inspect(secrets, null).Status);
    }

    [Fact]
    public void An_empty_file_is_invalid()
    {
        File.WriteAllText(KeyPath, "");

        Assert.Equal(BankingKeyStatus.Invalid, BankingKey.Inspect(secrets, null).Status);
    }

    [Theory]
    [InlineData("not a key")]
    [InlineData("-----BEGIN CERTIFICATE-----\nMIIB\n-----END CERTIFICATE-----")]
    public void Something_else_than_a_pem_private_key_is_invalid(string value)
        => Assert.Equal(BankingKeyStatus.Invalid, BankingKey.Inspect(secrets, value).Status);

    [Fact]
    public void A_public_key_is_invalid()
    {
        using var rsa = RSA.Create(2048);

        Assert.Equal(BankingKeyStatus.Invalid, BankingKey.Inspect(secrets, rsa.ExportSubjectPublicKeyInfoPem()).Status);
    }

    [Fact]
    public void An_rsa_private_key_is_loaded_in_pkcs8_or_pkcs1()
    {
        using var rsa = RSA.Create(2048);

        Assert.Equal(BankingKeyStatus.Loaded, BankingKey.Inspect(secrets, rsa.ExportPkcs8PrivateKeyPem()).Status);
        Assert.Equal(BankingKeyStatus.Loaded, BankingKey.Inspect(secrets, rsa.ExportRSAPrivateKeyPem()).Status);
    }

    [Fact]
    public void A_key_from_an_environment_variable_is_judged_alone()
    {
        // The folder in the file's place does not matter: the setting came from elsewhere.
        Directory.CreateDirectory(KeyPath);

        Assert.Equal(BankingKeyStatus.Loaded, BankingKey.Inspect(secrets, RsaPem()).Status);
        Assert.Equal(BankingKeyStatus.Invalid, BankingKey.Inspect(secrets, "garbage").Status);
    }
}
