namespace Nid.Host;

/// <summary>What the host says once at start (Program.cs), as source-generated log messages.</summary>
internal static partial class StartupLog
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Documents are stored in S3: they are outside the restic snapshot and rely on the bucket's versioning or replication.")]
    public static partial void DocumentsInS3(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The Enable Banking private key at {Path} is {Status}: bank sync stays not configured. See deploy/README.md §12.")]
    public static partial void BankingKeyUnusable(ILogger logger, string path, string status);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Plain HTTP is allowed (Auth__AllowInsecureHttp): passwords and the session cookie can travel unencrypted. Put Nid behind an HTTPS reverse proxy unless this network is trusted.")]
    public static partial void InsecureHttp(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Demo mode is on (Nid__Demo__Enabled): anyone can sign in as the demo household, without a password.")]
    public static partial void DemoOn(ILogger logger);
}
