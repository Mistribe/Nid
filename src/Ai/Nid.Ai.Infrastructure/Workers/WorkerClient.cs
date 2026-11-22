using System.Net.Http.Json;
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

    private static readonly FrameLimits Limits = new(MaxFramePlaintext: 64 * 1024, MaxFrames: 4, MaxTotalPlaintext: 256 * 1024);

    public async Task<WorkerKey> GetKeyAsync(Uri url, CancellationToken ct)
    {
        KeyView? view;
        try
        {
            view = await http.GetFromJsonAsync<KeyView>(new Uri(url, "/v1/key"), ct);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or UnambitiousFx.Myelin.OutboundRefusedException)
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

    private Task<T> ActiveCallAsync<T>(WorkerRecord worker, string path, byte[] plaintext, NidKey key, CancellationToken ct)
        where T : IContract
        => WorkerRules.Call(worker.Status) is { } refusal
            ? throw new WorkerCallException(refusal)
            : CallAsync<T>(worker, path, plaintext, key, AiCodes.WorkerRefused, ct);

    private async Task<T> CallAsync<T>(WorkerRecord worker, string path, byte[] plaintext, NidKey key, string refused, CancellationToken ct)
        where T : IContract
    {
        var (message, secret) = SealedRequest.Seal(worker.WorkerPublicKey, plaintext);
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(worker.Url, path)) { Content = new ByteArrayContent(message) };
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
                throw new WorkerCallException(refused);

            try
            {
                Frame? last = null;
                await foreach (var frame in ResponseReader.ReadAsync(secret, await response.Content.ReadAsStreamAsync(ct), Limits, ct))
                    last = frame;

                return last is { Type: FrameType.End }
                    ? typeof(T) == typeof(Acknowledged) ? (T)(object)Acknowledged.Instance : ContractJson.Read<T>(last.Content.Span)
                    : throw new WorkerCallException(refused);
            }
            catch (ProtocolException)
            {
                throw new WorkerCallException(refused);
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
