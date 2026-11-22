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
    public WorkerHarness(string? dataPath = null, TestClock? clock = null)
    {
        DataPath = dataPath ?? Directory.CreateTempSubdirectory("nid-worker-").FullName;
        Clock = clock ?? new TestClock();
    }

    public string DataPath { get; }

    public TestClock Clock { get; }

    public StateStore State => Services.GetRequiredService<StateStore>();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Worker:DataPath", DataPath);
        builder.ConfigureServices(services => services.Replace(ServiceDescriptor.Singleton<TimeProvider>(Clock)));
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
        var (message, secret) = SealedRequest.Seal(sealTo ?? WorkerKey, plaintext);
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(worker.BaseAddress!, path)) { Content = new ByteArrayContent(message) };
        await WorkerRequestSigning.SignAsync(request, signer, TestContext.Current.CancellationToken);
        using var response = await worker.SendAsync(request, TestContext.Current.CancellationToken);
        if (response.StatusCode != HttpStatusCode.OK)
            return (response.StatusCode, null);

        Frame? last = null;
        await foreach (var frame in ResponseReader.ReadAsync(secret, await response.Content.ReadAsStreamAsync(TestContext.Current.CancellationToken),
                           FrameLimits.Default, TestContext.Current.CancellationToken))
            last = frame;

        Assert.Equal(FrameType.End, last!.Type);
        return (response.StatusCode, typeof(T) == typeof(Empty) ? null : ContractJson.Read<T>(last.Content.Span) as T);
    }

    public void Dispose() => Key.Dispose();

    public sealed record WorkerKeyView(string PublicKey, string Fingerprint);

    public sealed record Empty : IContract
    {
        public bool IsValid() => true;
    }
}
