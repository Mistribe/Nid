using System.Reflection;
using System.Security.Cryptography;
using Nid.Ai.Protocol;
using Nid.Ai.Protocol.Contracts;
using Nid.Ai.Protocol.Framing;
using Nid.Ai.Protocol.Keys;
using Nid.Ai.Protocol.Sealing;
using Nid.Ai.Protocol.Signing;

namespace Nid.Ai.Worker;

/// <summary>The worker's routes. <c>GET /v1/key</c> and <c>POST /v1/pair</c> are the only ones without a pinned Nid key;
/// every other <c>/v1</c> route is checked (digest, then signature against the pinned key) before its body is opened.</summary>
public static class WorkerEndpoints
{
    public const string PairPath = "/v1/pair";
    public const string PairKeyItem = "nid.pair-key";
    private const int MaxRequestBytes = 1024 * 1024;

    public static readonly string SoftwareVersion =
        typeof(WorkerEndpoints).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? "0.0.0";

    public static bool IsSigned(PathString path)
        => path.StartsWithSegments("/v1") && path != "/v1/key" && path != PairPath;

    public static void Map(WebApplication app)
    {
        app.MapGet("/healthz", () => Results.Ok());

        app.MapGet("/v1/key", (StateStore store) =>
        {
            using var key = WorkerKeyPair.Import(Convert.FromBase64String(store.Load().WorkerKey));
            return Results.Json(new WorkerKeyView(Convert.ToBase64String(key.PublicKey), KeyFingerprint.Of(key.PublicKey)));
        });

        app.MapPost(PairPath, Pair);
        app.MapPost("/v1/declaration", (HttpContext context, StateStore store) =>
            Signed(context, store, (_, secret) => Respond(context, secret, Declaration(store.Load()))));
        app.MapPost("/v1/rotate-worker-key", (HttpContext context, StateStore store) =>
            Signed(context, store, (_, secret) => RotateWorkerKey(context, store, secret)));
        app.MapPost("/v1/rotate", (HttpContext context, StateStore store) =>
            Signed(context, store, (plaintext, secret) => StoreNidRotation(context, store, plaintext, secret)));
    }

    /// <summary>Opens a pairing request before its signature is checked: the key that signs it is inside. Runs only on
    /// <see cref="PairPath"/>, and only while the worker is not paired.</summary>
    public static async Task OpenPairing(HttpContext context, Func<Task> next)
    {
        var store = context.RequestServices.GetRequiredService<StateStore>();
        var body = await ReadBody(context);
        if (store.Load().IsPaired || body is null || !TryOpen(store, body, out var plaintext, out var secret))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        PairRequest request;
        try
        {
            request = ContractJson.Read<PairRequest>(plaintext);
        }
        catch (ProtocolException)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        var key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(request.NidPublicKey), out _);
        context.Response.RegisterForDispose(key);
        context.Items[PairKeyItem] = key;
        context.Items[typeof(PairRequest)] = (request, secret);
        context.Request.Body = new MemoryStream(body);
        await next();
    }

    private static async Task<IResult> Pair(HttpContext context, StateStore store)
    {
        if (context.Items[typeof(PairRequest)] is not (PairRequest request, ResponseSecret secret))
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        var now = store.Time.GetUtcNow();
        var accepted = false;
        var state = store.Update(s =>
        {
            if (s.IsPaired || s.PairingCode is null || s.PairingCodeUntil <= now || s.PairingAttemptsLeft <= 0)
                return s;
            if (!CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(s.PairingCode),
                    System.Text.Encoding.ASCII.GetBytes(request.Code)))
                return s with { PairingAttemptsLeft = s.PairingAttemptsLeft - 1 };

            accepted = true;
            return s with { NidKey = request.NidPublicKey, PairingCode = null, PairingCodeUntil = null, PairingAttemptsLeft = 0 };
        });

        if (!accepted)
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        using var certificate = SelfSignedCertificate.Load(state.TlsCertificate);
        await Respond(context, secret, new PairResponse(SelfSignedCertificate.Sha256(certificate), Declaration(state)));
        return Results.Empty;
    }

    private static async Task<IResult> Signed(HttpContext context, StateStore store, Func<byte[], ResponseSecret, Task> handle)
    {
        var body = await ReadBody(context);
        if (body is null || !TryOpen(store, body, out var plaintext, out var secret))
            return Results.StatusCode(StatusCodes.Status401Unauthorized);

        await handle(plaintext, secret);
        return Results.Empty;
    }

    private static async Task RotateWorkerKey(HttpContext context, StateStore store, ResponseSecret secret)
    {
        using var next = WorkerKeyPair.Generate();
        var now = store.Time.GetUtcNow();
        store.Update(s => s with
        {
            PreviousWorkerKey = s.WorkerKey,
            PreviousWorkerKeyUntil = now + StateStore.Overlap,
            WorkerKey = Convert.ToBase64String(next.ExportPrivateKey()),
        });
        await Respond(context, secret, new RotateWorkerKeyResponse(Convert.ToBase64String(next.PublicKey)));
    }

    private static async Task StoreNidRotation(HttpContext context, StateStore store, byte[] plaintext, ResponseSecret secret)
    {
        RotateNidKeyRequest request;
        try
        {
            request = ContractJson.Read<RotateNidKeyRequest>(plaintext);
        }
        catch (ProtocolException)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        store.Update(s => s with { PendingNidKey = request.NewNidPublicKey });
        await new ResponseWriter(secret, context.Response.Body).WriteAsync(FrameType.End, ReadOnlyMemory<byte>.Empty, context.RequestAborted);
    }

    /// <summary>The Nid keys this worker accepts, by <c>keyid</c>: the pinned one and, for 24 hours after a confirmed
    /// rotation, the previous one; during a pairing, only the key inside the pairing request.</summary>
    public static ECDsa? NidKeyById(HttpContext? context, StateStore store, string keyId)
    {
        if (context?.Items[PairKeyItem] is ECDsa pairing)
            return WorkerSignatures.KeyId(pairing) == keyId ? pairing : null;

        var state = store.Load();
        foreach (var (spki, until) in new[] { (state.NidKey, (DateTimeOffset?)null), (state.PreviousNidKey, state.PreviousNidKeyUntil) })
        {
            if (spki is null || (until is { } u && u <= store.Time.GetUtcNow()))
                continue;
            if (KeyFingerprint.Compact(Convert.FromBase64String(spki)) != keyId)
                continue;

            var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(spki), out _);
            context?.Response.RegisterForDispose(key);
            return key;
        }

        return null;
    }

    private static WorkerDeclaration Declaration(WorkerState state)
        => new(SoftwareVersion, [], [], MaxConcurrentRequests: 4, [],
            state.NidKey is null ? new string('0', 64) : KeyFingerprint.Compact(Convert.FromBase64String(state.NidKey)));

    private static bool TryOpen(StateStore store, byte[] message, out byte[] plaintext, out ResponseSecret secret)
    {
        var state = store.Load();
        var keys = new List<string> { state.WorkerKey };
        if (state.PreviousWorkerKey is { } previous && state.PreviousWorkerKeyUntil > store.Time.GetUtcNow())
            keys.Add(previous);

        foreach (var encoded in keys)
        {
            using var key = WorkerKeyPair.Import(Convert.FromBase64String(encoded));
            try
            {
                (plaintext, secret) = SealedRequest.Open(key, message);
                return true;
            }
            catch (ProtocolException)
            {
                // try the previous key
            }
        }

        plaintext = [];
        secret = null!;
        return false;
    }

    private static async Task<byte[]?> ReadBody(HttpContext context)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await context.Request.Body.ReadAsync(chunk, context.RequestAborted)) > 0)
        {
            if (buffer.Length + read > MaxRequestBytes)
                return null;
            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static async Task Respond<T>(HttpContext context, ResponseSecret secret, T body) where T : IContract
    {
        context.Response.ContentType = "application/octet-stream";
        await new ResponseWriter(secret, context.Response.Body).WriteAsync(FrameType.End, ContractJson.Write(body), context.RequestAborted);
    }

    public sealed record WorkerKeyView(string PublicKey, string Fingerprint);
}
