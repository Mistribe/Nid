using System.Text.Json;
using Nid.Ai.Application;
using Nid.Ai.Contracts;
using Nid.Ai.Domain;
using Nid.Ai.Protocol;
using Nid.Ai.Protocol.Contracts;
using Nid.Ai.Protocol.Framing;
using Nid.Ai.Protocol.Keys;
using Nid.Ai.Protocol.Sealing;
using Nid.Ai.Protocol.Signing;

namespace Nid.Ai.Infrastructure.Workers;

/// <summary>Nid's side of the worker protocol (<c>docs/ai/README.md</c> §4): every call sealed to the worker's pinned key
/// and signed with the Nid key it pinned, its answer read from frames bound to the call. Its <see cref="HttpClient"/> goes
/// through Myelin's registered endpoints and the TLS pins of <see cref="WorkerDirectory"/>.</summary>
public sealed class WorkerClient(HttpClient http) : IWorkerClient, IDisposable
{
    public void Dispose() => http.Dispose();

    /// <summary>A call's time: 30 s; sending a job's file, up to 10 min.</summary>
    public static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan SubmitTimeout = TimeSpan.FromMinutes(10);

    private static readonly FrameLimits Limits = new(MaxFramePlaintext: 64 * 1024, MaxFrames: 4, MaxTotalPlaintext: 256 * 1024);

    /// <summary>The most a worker's key answer may weigh: a P-256 public key in JSON is well under it.</summary>
    internal const int MaxKeyBody = 4 * 1024;

    public async Task<WorkerKey> GetKeyAsync(Uri url, CancellationToken outer)
    {
        // An address an admin typed, not yet pinned: bounded in time and size like a call (ExchangeAsync).
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(outer);
        deadline.CancelAfter(CallTimeout);
        var ct = deadline.Token;
        KeyView? view;
        try
        {
            using var response = await http.GetAsync(new Uri(url, "/v1/key"), HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            await using var body = await response.Content.ReadAsStreamAsync(ct);
            var buffer = new byte[MaxKeyBody + 1];
            var read = await body.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, ct);
            if (read > MaxKeyBody)
                throw new WorkerCallException(AiCodes.WorkerRefused);
            view = JsonSerializer.Deserialize<KeyView>(buffer.AsSpan(0, read), JsonSerializerOptions.Web);
        }
        catch (Exception e) when (e is HttpRequestException or IOException or OperationCanceledException or JsonException
                                      or UnambitiousFx.Myelin.OutboundRefusedException
                                  && !outer.IsCancellationRequested)
        {
            throw new WorkerCallException(AiCodes.WorkerUnreachable);
        }

        var publicKey = view?.PublicKey is { } encoded ? TryBase64(encoded) : null;
        if (publicKey is not { Length: 65 })
            throw new WorkerCallException(AiCodes.WorkerRefused);

        // The fingerprint shown to the admin is computed here, never taken from the worker.
        return new WorkerKey(publicKey, KeyFingerprint.Of(publicKey));
    }

    public async Task<PairResponse> PairAsync(WorkerRecord worker, NidKey key, string code, CancellationToken ct)
    {
        if (WorkerRules.Pair(worker.Status) is { } refusal)
            throw new WorkerCallException(refusal);

        var request = new PairRequest(Convert.ToBase64String(key.Key.ExportSubjectPublicKeyInfo()), code);
        return await CallAsync<PairResponse>(worker, "/v1/pair", ContractJson.Write(request), key, AiCodes.PairingRefused, ct);
    }

    public Task<WorkerDeclaration> DeclarationAsync(WorkerRecord worker, NidKey key, CancellationToken ct)
        => ActiveCallAsync<WorkerDeclaration>(worker, "/v1/declaration", "{}"u8.ToArray(), key, ct);

    public async Task<byte[]> RotateWorkerKeyAsync(WorkerRecord worker, NidKey key, CancellationToken ct)
    {
        var response = await ActiveCallAsync<RotateWorkerKeyResponse>(worker, "/v1/rotate-worker-key", "{}"u8.ToArray(), key, ct);
        return Convert.FromBase64String(response.NewPublicKey);
    }

    public async Task SendNidRotationAsync(WorkerRecord worker, NidKey key, NidKey next, CancellationToken ct)
    {
        var request = new RotateNidKeyRequest(Convert.ToBase64String(next.Key.ExportSubjectPublicKeyInfo()));
        await ActiveCallAsync<Acknowledged>(worker, "/v1/rotate", ContractJson.Write(request), key, ct);
    }

    public async Task<SubmittedJob> SubmitJobAsync(WorkerRecord worker, NidKey key, JobSubmission submission, CancellationToken ct)
    {
        Active(worker);
        var (frames, secret) = await ExchangeAsync(worker, "/v1/jobs", ContractJson.Write(submission), key, AiCodes.WorkerRefused, Limits, ct,
            submission.File is null ? CallTimeout : SubmitTimeout);
        return new SubmittedJob(Read<JobAccepted>(frames[^1], AiCodes.WorkerRefused).Id, secret.ToBytes());
    }

    public async Task<PolledJob> PollJobAsync(WorkerRecord worker, NidKey key, string workerJobId, byte[] responseSecret, CancellationToken ct)
    {
        Active(worker);
        var limits = new FrameLimits(64 * 1024, JobStatus.MaxResultBytes / JobStatus.ResultChunk + 2, JobStatus.MaxResultBytes + 64 * 1024);
        var (frames, _) = await ExchangeAsync(worker, $"/v1/jobs/{Uri.EscapeDataString(workerJobId)}",
            ContractJson.Write(new JobPoll(workerJobId)), key, AiCodes.WorkerRefused, limits, ct);
        var status = Read<JobStatus>(frames[^1], AiCodes.WorkerRefused);
        var result = frames.Take(frames.Count - 1).SelectMany(f => f.Content.ToArray()).ToArray();
        if (result.Length != (status.ResultLength ?? 0))
            throw new WorkerCallException(AiCodes.WorkerRefused);
        return new PolledJob(status, status.ResultLength is null ? null : Open(responseSecret, result));
    }

    /// <summary>A job's result, sealed by the worker under the secret of its submission (<c>docs/ai/README.md</c> §4.3): its data
    /// frames joined, or null when the frames do not read or end before their <c>End</c> frame.</summary>
    internal static byte[]? Open(byte[] responseSecret, byte[] sealedResult)
    {
        try
        {
            var output = new MemoryStream();
            var frames = ResponseReader.ReadAsync(ResponseSecret.FromBytes(responseSecret), new MemoryStream(sealedResult),
                new FrameLimits(64 * 1024, JobStatus.MaxResultBytes / JobStatus.ResultChunk + 2, JobStatus.MaxResultBytes), CancellationToken.None);
            var ended = false;
            foreach (var frame in frames.ToBlockingEnumerable())
            {
                if (frame.Type == FrameType.Data)
                    output.Write(frame.Content.Span);
                ended = frame.Type == FrameType.End;
            }

            return ended ? output.ToArray() : null;
        }
        catch (ProtocolException)
        {
            return null;
        }
    }

    public async IAsyncEnumerable<Frame> StreamAsync(
        WorkerRecord worker, NidKey key, string type, int version, byte[] input, FrameLimits limits,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        Active(worker);
        var (message, secret) = SealedRequest.Seal(worker.WorkerPublicKey, input);
        using var request = new HttpRequestMessage(HttpMethod.Post,
            new Uri(worker.Url, $"/v1/requests/{Uri.EscapeDataString(type)}/{version}")) { Content = new ByteArrayContent(message) };
        await WorkerRequestSigning.SignAsync(request, key.Key, ct);

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or UnambitiousFx.Myelin.OutboundRefusedException && !ct.IsCancellationRequested)
        {
            throw new WorkerCallException(AiCodes.WorkerUnreachable);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
                throw new WorkerCallException(AiCodes.WorkerRefused);

            await using var frames = ResponseReader.ReadAsync(secret, await response.Content.ReadAsStreamAsync(ct), limits, ct).GetAsyncEnumerator(ct);
            while (true)
            {
                try
                {
                    if (!await frames.MoveNextAsync())
                        yield break;
                }
                catch (ProtocolException)
                {
                    throw new WorkerCallException(AiCodes.WorkerRefused);
                }
                catch (Exception e) when (e is HttpRequestException or IOException && !ct.IsCancellationRequested)
                {
                    throw new WorkerCallException(AiCodes.WorkerUnreachable);
                }

                yield return frames.Current;
            }
        }
    }

    public async Task CancelJobAsync(WorkerRecord worker, NidKey key, string workerJobId, CancellationToken ct)
    {
        Active(worker);
        await ExchangeAsync(worker, $"/v1/jobs/{Uri.EscapeDataString(workerJobId)}/cancel", "{}"u8.ToArray(), key, AiCodes.WorkerRefused, Limits, ct);
    }

    private static void Active(WorkerRecord worker)
    {
        if (WorkerRules.Call(worker.Status) is { } refusal)
            throw new WorkerCallException(refusal);
    }

    private static T Read<T>(Frame frame, string refused) where T : IContract
    {
        if (frame.Type != FrameType.End)
            throw new WorkerCallException(frame.Type == FrameType.Error ? $"{refused}:{System.Text.Encoding.UTF8.GetString(frame.Content.Span)}" : refused);
        try
        {
            return ContractJson.Read<T>(frame.Content.Span);
        }
        catch (ProtocolException)
        {
            throw new WorkerCallException(refused);
        }
    }

    private Task<T> ActiveCallAsync<T>(WorkerRecord worker, string path, byte[] plaintext, NidKey key, CancellationToken ct)
        where T : IContract
        => WorkerRules.Call(worker.Status) is { } refusal
            ? throw new WorkerCallException(refusal)
            : CallAsync<T>(worker, path, plaintext, key, AiCodes.WorkerRefused, ct);

    private async Task<T> CallAsync<T>(WorkerRecord worker, string path, byte[] plaintext, NidKey key, string refused, CancellationToken ct)
        where T : IContract
    {
        var (frames, _) = await ExchangeAsync(worker, path, plaintext, key, refused, Limits, ct);
        var last = frames[^1];
        return last is { Type: FrameType.End } && typeof(T) == typeof(Acknowledged) ? (T)(object)Acknowledged.Instance : Read<T>(last, refused);
    }

    /// <summary>One call: sealed, signed, sent; every frame of the answer, which must end with <c>end</c> or <c>error</c>.</summary>
    private async Task<(List<Frame> Frames, ResponseSecret Secret)> ExchangeAsync(
        WorkerRecord worker, string path, byte[] plaintext, NidKey key, string refused, FrameLimits limits, CancellationToken outer,
        TimeSpan? timeout = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(outer);
        deadline.CancelAfter(timeout ?? CallTimeout);
        var ct = deadline.Token;
        var (message, secret) = SealedRequest.Seal(worker.WorkerPublicKey, plaintext);
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(worker.Url, path)) { Content = new ByteArrayContent(message) };
        await WorkerRequestSigning.SignAsync(request, key.Key, ct);

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or UnambitiousFx.Myelin.OutboundRefusedException
                                  && !outer.IsCancellationRequested)
        {
            throw new WorkerCallException(AiCodes.WorkerUnreachable);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
                throw new WorkerCallException(refused);

            try
            {
                var frames = new List<Frame>();
                await foreach (var frame in ResponseReader.ReadAsync(secret, await response.Content.ReadAsStreamAsync(ct), limits, ct))
                    frames.Add(frame);
                return (frames, secret);
            }
            catch (ProtocolException)
            {
                throw new WorkerCallException(refused);
            }
            catch (Exception e) when (e is HttpRequestException or IOException or OperationCanceledException && !outer.IsCancellationRequested)
            {
                throw new WorkerCallException(AiCodes.WorkerUnreachable);
            }
        }
    }

    private static byte[]? TryBase64(string value)
    {
        var buffer = new byte[value.Length];
        return Convert.TryFromBase64String(value, buffer, out var written) ? buffer[..written] : null;
    }

    private sealed record KeyView(string? PublicKey, string? Fingerprint);

    private sealed record Acknowledged : IContract
    {
        public static readonly Acknowledged Instance = new();

        public bool IsValid() => true;
    }
}
