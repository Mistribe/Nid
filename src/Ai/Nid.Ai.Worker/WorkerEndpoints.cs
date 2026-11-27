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

    /// <summary>A job's body: its file (50 MiB, base64) and its input.</summary>
    public const int MaxJobBytes = 70 * 1024 * 1024;

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
        app.MapPost("/v1/declaration", (HttpContext context, StateStore store, JobTable jobs) =>
            Signed(context, store, async (_, secret) => await Respond(context, secret, await DeclarationAsync(context, store.Load(), jobs))));
        app.MapPost("/v1/jobs", (HttpContext context, StateStore store, JobTable jobs) =>
            Signed(context, store, (plaintext, secret) => SubmitJob(context, jobs, plaintext, secret), MaxJobBytes));
        app.MapPost("/v1/jobs/{id}", (string id, HttpContext context, StateStore store, JobTable jobs) =>
            Signed(context, store, (_, secret) => JobStatusOf(context, jobs, id, secret)));
        app.MapPost("/v1/jobs/{id}/cancel", (string id, HttpContext context, StateStore store, JobTable jobs) =>
            Signed(context, store, (_, secret) =>
            {
                jobs.Cancel(id);
                return new ResponseWriter(secret, context.Response.Body).WriteAsync(FrameType.End, ReadOnlyMemory<byte>.Empty, context.RequestAborted);
            }));
        app.MapPost("/v1/requests/{type}/{version:int}",
            (string type, int version, HttpContext context, StateStore store, JobTable jobs, IEnumerable<IRequestHandler> handlers) =>
                Signed(context, store, (plaintext, secret) => AnswerAsync(context, jobs, handlers, type, version, plaintext, secret)));
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
        if (store.Load().IsPaired)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        var body = await ReadBody(context);
        if (body is null || !TryOpen(store, body, out var plaintext, out var secret))
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
        var state = store.Update(s =>
        {
            if (s.IsPaired || s.PairingCode is null || s.PairingCodeUntil <= now || s.PairingAttemptsLeft <= 0)
                return s;
            if (!CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(s.PairingCode),
                    System.Text.Encoding.ASCII.GetBytes(request.Code)))
                return s with { PairingAttemptsLeft = s.PairingAttemptsLeft - 1 };

            return s with { NidKey = request.NidPublicKey, PairingCode = null, PairingCodeUntil = null, PairingAttemptsLeft = 0 };
        });

        // Paired by this request: its key is the one pinned now (OpenPairing refused a worker paired before).
        if (state.NidKey != request.NidPublicKey)
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        using var certificate = SelfSignedCertificate.Load(state.TlsCertificate);
        await Respond(context, secret, new PairResponse(SelfSignedCertificate.Sha256(certificate), await DeclarationAsync(context, state, context.RequestServices.GetRequiredService<JobTable>())));
        return Results.Empty;
    }

    private static async Task SubmitJob(HttpContext context, JobTable jobs, byte[] plaintext, ResponseSecret secret)
    {
        JobSubmission submission;
        byte[]? file;
        try
        {
            submission = ContractJson.Read<JobSubmission>(plaintext);
            file = submission.File is null ? null : Convert.FromBase64String(submission.File);
        }
        catch (Exception e) when (e is ProtocolException or FormatException)
        {
            await Refuse(context, secret, "input_invalid");
            return;
        }

        var (id, refusal) = jobs.Submit(submission.Type, submission.Version, submission.Input, file, secret);
        if (id is null)
            await Refuse(context, secret, refusal!);
        else
            await Respond(context, secret, new JobAccepted(id));
    }

    private static async Task JobStatusOf(HttpContext context, JobTable jobs, string id, ResponseSecret secret)
    {
        context.Response.ContentType = "application/octet-stream";
        var writer = new ResponseWriter(secret, context.Response.Body);
        var job = jobs.Find(id);
        var succeeded = job is { Status: "succeeded", Result: { } result } ? result : null;
        if (succeeded is not null)
            for (var at = 0; at < succeeded.Length; at += JobStatus.ResultChunk)
                await writer.WriteAsync(FrameType.Data, succeeded.AsMemory(at, Math.Min(JobStatus.ResultChunk, succeeded.Length - at)), context.RequestAborted);

        var status = job is null
            ? new JobStatus("unknown", null, null)
            : new JobStatus(succeeded is null && job.Status == "succeeded" ? "running" : job.Status, job.Error, succeeded?.Length);
        await writer.WriteAsync(FrameType.End, ContractJson.Write(status), context.RequestAborted);
    }

    /// <summary>A request's answer, in data frames as the handler yields them, then <c>end</c>; an <c>error</c> frame when the
    /// type is unknown, its input does not follow its contract, or the handler fails. Closing the request cancels it.</summary>
    private static async Task AnswerAsync(
        HttpContext context, JobTable jobs, IEnumerable<IRequestHandler> handlers, string type, int version, byte[] input,
        ResponseSecret secret)
    {
        context.Response.ContentType = "application/octet-stream";
        var writer = new ResponseWriter(secret, context.Response.Body);
        var ct = context.RequestAborted;
        if (handlers.LastOrDefault(h => h.Type == type && h.Version == version && h.Available) is not { } handler)
        {
            await writer.WriteAsync(FrameType.Error, "unsupported"u8.ToArray(), ct);
            return;
        }

        using var _ = jobs.Answering();
        string? failure = null;
        try
        {
            await foreach (var frame in handler.RunAsync(input, ct).WithCancellation(ct))
                await writer.WriteAsync(FrameType.Data, frame, ct);
        }
        catch (InputInvalidException)
        {
            failure = "input_invalid";
        }
        catch (EngineException e)
        {
            failure = e.Code;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return; // the caller is gone: nothing to end
        }
        catch (Exception)
        {
            failure = "handler_failed";
        }

        if (failure is null)
            await writer.WriteAsync(FrameType.End, ReadOnlyMemory<byte>.Empty, ct);
        else
            await writer.WriteAsync(FrameType.Error, System.Text.Encoding.UTF8.GetBytes(failure), ct);
    }

    private static Task Refuse(HttpContext context, ResponseSecret secret, string reason)
    {
        context.Response.ContentType = "application/octet-stream";
        return new ResponseWriter(secret, context.Response.Body).WriteAsync(FrameType.Error, System.Text.Encoding.UTF8.GetBytes(reason), context.RequestAborted);
    }

    private static async Task<IResult> Signed(
        HttpContext context, StateStore store, Func<byte[], ResponseSecret, Task> handle, int maxBytes = MaxRequestBytes)
    {
        var body = await ReadBody(context, maxBytes);
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

    private static async Task<WorkerDeclaration> DeclarationAsync(HttpContext context, WorkerState state, JobTable jobs)
        => new(SoftwareVersion,
            [.. jobs.Types.Select(t => new ContractVersion(t.Type, t.Version))],
            [.. jobs.RequestTypes.Select(t => new ContractVersion(t.Type, t.Version))],
            MaxConcurrentRequests: JobTable.MaxConcurrent,
            [.. await context.RequestServices.GetRequiredService<EngineClient>().ModelsAsync(context.RequestAborted),
                .. context.RequestServices.GetRequiredService<NliDecider>().Model is { } nli ? [nli] : Array.Empty<DeclaredModel>()],
            state.NidKey is null ? new string('0', 64) : KeyFingerprint.Compact(Convert.FromBase64String(state.NidKey)),
            jobs.Load);

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

    private static async Task<byte[]?> ReadBody(HttpContext context, int maxBytes = MaxRequestBytes)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await context.Request.Body.ReadAsync(chunk, context.RequestAborted)) > 0)
        {
            if (buffer.Length + read > maxBytes)
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
