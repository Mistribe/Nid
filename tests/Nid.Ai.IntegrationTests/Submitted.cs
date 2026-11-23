using Nid.Ai.Contracts;
using UnambitiousFx.Functional;

namespace Nid.Ai.IntegrationTests;

internal static class Submitted
{
    /// <summary>The job's id, for a submission the test expects to be queued.</summary>
    public static async Task<Guid> Accepted(this Task<Result<Guid>> submission)
    {
        var result = await submission;
        Assert.True(result.TryGetValue(out var id), "the job was refused");
        return id;
    }

    /// <summary>The <c>ai.*</c> code a submission the test expects to be refused carries.</summary>
    public static async Task<string> Refused(this Task<Result<Guid>> submission)
    {
        var result = await submission;
        Assert.True(result.TryGetFailure(out var failure), "the job was queued");
        return failure!.Code;
    }
}
