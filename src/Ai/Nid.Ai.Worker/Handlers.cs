using System.Runtime.CompilerServices;
using System.Text.Json;
using Nid.Ai.Protocol;
using Nid.Ai.Protocol.Contracts;

namespace Nid.Ai.Worker;

/// <summary>A job type this worker runs: its input as its contract reads it, its output as the contract's JSON.</summary>
public interface IJobHandler
{
    string Type { get; }

    int Version { get; }

    /// <summary>False: the worker does not declare it and answers <c>unsupported</c> (document text without its tools).</summary>
    bool Available => true;

    Task<byte[]> RunAsync(JsonElement input, byte[]? file, CancellationToken ct);
}

/// <summary>A request type this worker answers, in data frames; closing the request cancels it.</summary>
public interface IRequestHandler
{
    string Type { get; }

    int Version { get; }

    /// <summary>False: the worker does not declare it and answers <c>unsupported</c> (chat without an engine).</summary>
    bool Available => true;

    IAsyncEnumerable<byte[]> RunAsync(byte[] input, CancellationToken ct);
}

/// <summary>A handler's input does not follow its contract: the job fails as <c>input_invalid</c>.</summary>
public sealed class InputInvalidException() : Exception("The input does not follow its contract.");

/// <summary>The test job (<c>contracts/jobs/echo/v1</c>): the text back.</summary>
public sealed class EchoJob : IJobHandler
{
    public string Type => "echo";

    public int Version => 1;

    public Task<byte[]> RunAsync(JsonElement input, byte[]? file, CancellationToken ct)
        => Task.FromResult(ContractJson.Write(new EchoJobOutput(Read<EchoJobInput>(JsonSerializer.SerializeToUtf8Bytes(input)).Text)));

    internal static T Read<T>(byte[] json) where T : IContract
    {
        try
        {
            return ContractJson.Read<T>(json);
        }
        catch (ProtocolException)
        {
            throw new InputInvalidException();
        }
    }
}

/// <summary>The test request (<c>contracts/requests/echo/v1</c>): the text back, in as many data frames as asked.</summary>
public sealed class EchoRequest : IRequestHandler
{
    public string Type => "echo";

    public int Version => 1;

    public async IAsyncEnumerable<byte[]> RunAsync(byte[] input, [EnumeratorCancellation] CancellationToken ct)
    {
        var request = EchoJob.Read<EchoRequestInput>(input);
        for (var i = 0; i < request.Frames; i++)
        {
            ct.ThrowIfCancellationRequested();
            yield return ContractJson.Write(new EchoRequestOutput(request.Text));
            await Task.Yield();
        }
    }
}
