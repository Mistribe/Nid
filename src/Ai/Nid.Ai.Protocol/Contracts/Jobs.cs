using System.Text.Json;

namespace Nid.Ai.Protocol.Contracts;

/// <summary>A job sent to a worker (<c>POST /v1/jobs</c>): its type and contract version, its input as the contract of that
/// type reads it, and its file, when it has one (base64, 50 MiB at most).</summary>
public sealed record JobSubmission(string Type, int Version, JsonElement Input, string? File) : IContract
{
    public const int MaxFileBytes = 50 * 1024 * 1024;

    public bool IsValid()
        => Type.Length is > 0 and <= 64 && Version >= 1
           && Input.ValueKind is JsonValueKind.Object
           && (File is null || (File.Length <= Base64Length(MaxFileBytes) && IsBase64(File)));

    internal static int Base64Length(int bytes) => (bytes + 2) / 3 * 4;

    internal static bool IsBase64(string value)
    {
        var buffer = new byte[value.Length / 4 * 3 + 3];
        return Convert.TryFromBase64String(value, buffer, out _);
    }
}

/// <summary>The worker's id for a job it accepted.</summary>
public sealed record JobAccepted(string Id) : IContract
{
    public bool IsValid() => Id.Length is > 0 and <= 64;
}

/// <summary>Asks a job's status (<c>POST /v1/jobs/{id}</c>, sealed and signed like any request).</summary>
public sealed record JobPoll(string Id) : IContract
{
    public bool IsValid() => Id.Length is > 0 and <= 64;
}

/// <summary>A job's status, the <c>end</c> frame of a poll's answer. Once <c>succeeded</c>, the data frames before it carry
/// the result, <see cref="ResultLength"/> bytes of frames sealed under the <b>submission's</b> context, which only Nid,
/// keeping that secret, opens (<c>docs/ai/README.md</c> §4.2). <c>unknown</c>: the worker no longer has the job
/// (restarted, or the result expired).</summary>
public sealed record JobStatus(string Status, string? Error, long? ResultLength) : IContract
{
    public static readonly string[] Statuses = ["queued", "running", "succeeded", "failed", "unknown"];

    public const int MaxResultBytes = 24 * 1024 * 1024;

    /// <summary>The result's bytes per data frame of a poll's answer.</summary>
    public const int ResultChunk = 60 * 1024;

    public bool IsValid()
        => Statuses.Contains(Status)
           && (Status == "succeeded") == (ResultLength is not null)
           && ResultLength is null or (> 0 and <= MaxResultBytes)
           && (Error is null || Error.Length <= 200);
}
