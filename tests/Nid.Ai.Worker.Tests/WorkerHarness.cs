using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nid.Ai.Protocol.Contracts;
using Nid.Ai.Protocol.Framing;
using Nid.Ai.Protocol.Sealing;
using Nid.Ai.Protocol.Signing;
using Nid.Ai.Worker;

namespace Nid.Ai.Worker.Tests;

/// <summary>A worker hosted in memory on its own data directory, and the clock it reads. A second harness on the same
/// directory is the same worker restarted.</summary>
public sealed class WorkerHarness : WebApplicationFactory<Program>
{
    private readonly Action<IServiceCollection>? _services;

    public WorkerHarness(string? dataPath = null, TestClock? clock = null, Action<IServiceCollection>? services = null)
    {
        DataPath = dataPath ?? Directory.CreateTempSubdirectory("nid-worker-").FullName;
        Clock = clock ?? new TestClock();
        _services = services;
    }

    public string DataPath { get; }

    public TestClock Clock { get; }

    public StateStore State => Services.GetRequiredService<StateStore>();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Worker:DataPath", DataPath);
        builder.ConfigureServices(services =>
        {
            services.Replace(ServiceDescriptor.Singleton<TimeProvider>(Clock));
            _services?.Invoke(services);
        });
    }

    public string PairingCode() => State.Load().PairingCode!;
}

public sealed class TestClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;

    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>What Nid does, reduced to the protocol: its key, the worker key it pinned, sealed and signed calls.</summary>
public sealed class TestNid(HttpClient worker) : IDisposable
{
    public ECDsa Key { get; private set; } = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public byte[] WorkerKey { get; set; } = [];

    public async Task<byte[]> FetchWorkerKeyAsync()
    {
        var key = await worker.GetFromJsonAsync<WorkerKeyView>("/v1/key", TestContext.Current.CancellationToken);
        WorkerKey = Convert.FromBase64String(key!.PublicKey);
        return WorkerKey;
    }

    public Task<(HttpStatusCode Status, PairResponse? Body)> PairAsync(string code, ECDsa? signedBy = null)
        => CallAsync<PairResponse>("/v1/pair",
            ContractJson.Write(new PairRequest(Convert.ToBase64String(Key.ExportSubjectPublicKeyInfo()), code)), signedBy ?? Key);

    public Task<(HttpStatusCode Status, WorkerDeclaration? Body)> DeclarationAsync()
        => CallAsync<WorkerDeclaration>("/v1/declaration", "{}"u8.ToArray(), Key);

    public Task<(HttpStatusCode Status, RotateWorkerKeyResponse? Body)> RotateWorkerKeyAsync()
        => CallAsync<RotateWorkerKeyResponse>("/v1/rotate-worker-key", "{}"u8.ToArray(), Key);

    public async Task<HttpStatusCode> SendNidRotationAsync(ECDsa next)
        => (await CallAsync<Empty>("/v1/rotate",
            ContractJson.Write(new RotateNidKeyRequest(Convert.ToBase64String(next.ExportSubjectPublicKeyInfo()))), Key)).Status;

    public void UseKey(ECDsa key) => Key = key;

    public async Task<(HttpStatusCode Status, T? Body)> CallAsync<T>(string path, byte[] plaintext, ECDsa signer, byte[]? sealTo = null)
        where T : class, IContract
    {
        var (status, frames, _) = await CallFramesAsync(path, plaintext, signer, sealTo);
        if (status != HttpStatusCode.OK)
            return (status, null);

        var last = frames[^1];
        Assert.Equal(FrameType.End, last.Type);
        return (status, typeof(T) == typeof(Empty) ? null : ContractJson.Read<T>(last.Content.Span) as T);
    }

    /// <summary>Every frame of the answer, and the secret of the call (a job's result opens with its submission's).</summary>
    public async Task<(HttpStatusCode Status, List<Frame> Frames, ResponseSecret Secret)> CallFramesAsync(
        string path, byte[] plaintext, ECDsa? signer = null, byte[]? sealTo = null, CancellationToken ct = default)
    {
        ct = ct == default ? TestContext.Current.CancellationToken : ct;
        var (message, secret) = SealedRequest.Seal(sealTo ?? WorkerKey, plaintext);
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(worker.BaseAddress!, path)) { Content = new ByteArrayContent(message) };
        await WorkerRequestSigning.SignAsync(request, signer ?? Key, ct);
        using var response = await worker.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        var frames = new List<Frame>();
        if (response.StatusCode != HttpStatusCode.OK)
            return (response.StatusCode, frames, secret);

        await foreach (var frame in ResponseReader.ReadAsync(secret, await response.Content.ReadAsStreamAsync(ct), FrameLimits.Default, ct))
            frames.Add(frame);
        return (response.StatusCode, frames, secret);
    }

    public async Task<(string? Id, string? Error, ResponseSecret Secret)> SubmitAsync(string type, int version, object input)
    {
        var submission = new JobSubmission(type, version, System.Text.Json.JsonSerializer.SerializeToElement(input, ContractJson.Strict), null);
        var (status, frames, secret) = await CallFramesAsync("/v1/jobs", ContractJson.Write(submission));
        Assert.Equal(HttpStatusCode.OK, status);
        var last = frames[^1];
        return last.Type == FrameType.End
            ? (ContractJson.Read<JobAccepted>(last.Content.Span).Id, null, secret)
            : (null, System.Text.Encoding.UTF8.GetString(last.Content.Span), secret);
    }

    public async Task<JobStatus> PollAsync(string id)
        => (await CallAsync<JobStatus>($"/v1/jobs/{id}", ContractJson.Write(new JobPoll(id)), Key)).Body!;

    /// <summary>Polls until the job leaves queued and running (in memory, a few milliseconds).</summary>
    public async Task<JobStatus> WaitAsync(string id)
    {
        for (var i = 0; i < 200; i++)
        {
            var status = await PollAsync(id);
            if (status.Status is not ("queued" or "running"))
                return status;
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        throw new TimeoutException($"Job {id} did not finish.");
    }

    public static async Task<List<Frame>> OpenResultAsync(string result, ResponseSecret submission)
    {
        var frames = new List<Frame>();
        await foreach (var frame in ResponseReader.ReadAsync(submission, new MemoryStream(Convert.FromBase64String(result)),
                           FrameLimits.Default, TestContext.Current.CancellationToken))
            frames.Add(frame);
        return frames;
    }

    public void Dispose() => Key.Dispose();

    public sealed record WorkerKeyView(string PublicKey, string Fingerprint);

    public sealed record Empty : IContract
    {
        public bool IsValid() => true;
    }
}
