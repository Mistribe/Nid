using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Nid.Ai.Protocol.Keys;
using Nid.Ai.Protocol.Sealing;
using Nid.Ai.Protocol.Signing;

namespace Nid.Ai.Worker.Tests;

public sealed class WorkerTests
{
    [Fact]
    public async Task Key_is_published_with_its_fingerprint()
    {
        await using var worker = new WorkerHarness();
        using var client = worker.CreateClient();

        var key = await client.GetFromJsonAsync<TestNid.WorkerKeyView>("/v1/key", TestContext.Current.CancellationToken);

        Assert.Equal(KeyFingerprint.Of(Convert.FromBase64String(key!.PublicKey)), key.Fingerprint);
        Assert.Equal(65, Convert.FromBase64String(key.PublicKey).Length);
    }

    [Fact]
    public async Task Pairing_with_the_code_pins_the_key()
    {
        await using var worker = new WorkerHarness();
        using var nid = new TestNid(worker.CreateClient());
        await nid.FetchWorkerKeyAsync();

        var (status, body) = await nid.PairAsync(worker.PairingCode());

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(WorkerSignatures.KeyId(nid.Key), body!.Declaration.NidKeyFingerprint);
        Assert.Equal(64, body.TlsCertificateSha256.Length);
        Assert.Null(worker.State.Load().PairingCode);
    }

    [Fact]
    public async Task A_paired_worker_refuses_a_second_pairing()
    {
        await using var worker = new WorkerHarness();
        using var nid = new TestNid(worker.CreateClient());
        await nid.FetchWorkerKeyAsync();
        var code = worker.PairingCode();
        await nid.PairAsync(code);

        using var intruder = new TestNid(worker.CreateClient()) { WorkerKey = nid.WorkerKey };
        Assert.Equal(HttpStatusCode.Forbidden, (await intruder.PairAsync(code)).Status);
        Assert.Equal(WorkerSignatures.KeyId(nid.Key), (await nid.DeclarationAsync()).Body!.NidKeyFingerprint);
    }

    [Fact]
    public async Task A_wrong_code_uses_a_try_and_five_kill_it()
    {
        await using var worker = new WorkerHarness();
        using var nid = new TestNid(worker.CreateClient());
        await nid.FetchWorkerKeyAsync();
        var code = worker.PairingCode();
        var wrong = code[0] == 'A' ? "BBBBBBBB" : "AAAAAAAA";

        for (var i = 0; i < 5; i++)
            Assert.Equal(HttpStatusCode.Forbidden, (await nid.PairAsync(wrong)).Status);

        Assert.Equal(HttpStatusCode.Forbidden, (await nid.PairAsync(code)).Status);
    }

    [Fact]
    public async Task An_expired_code_is_refused()
    {
        await using var worker = new WorkerHarness();
        using var nid = new TestNid(worker.CreateClient());
        await nid.FetchWorkerKeyAsync();
        var code = worker.PairingCode();

        worker.Clock.Now += TimeSpan.FromMinutes(16);

        Assert.Equal(HttpStatusCode.Forbidden, (await nid.PairAsync(code)).Status);
    }

    [Fact]
    public async Task A_pair_request_not_signed_by_its_key_is_refused()
    {
        await using var worker = new WorkerHarness();
        using var nid = new TestNid(worker.CreateClient());
        await nid.FetchWorkerKeyAsync();
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        Assert.NotEqual(HttpStatusCode.OK, (await nid.PairAsync(worker.PairingCode(), signedBy: other)).Status);
        Assert.NotNull(worker.State.Load().PairingCode);
    }

    [Fact]
    public async Task Unpair_allows_a_new_pairing()
    {
        await using var worker = new WorkerHarness();
        using var nid = new TestNid(worker.CreateClient());
        await nid.FetchWorkerKeyAsync();
        await nid.PairAsync(worker.PairingCode());

        Assert.Equal(0, Cli.Run(["unpair"], worker.State, TextWriter.Null));

        using var next = new TestNid(worker.CreateClient()) { WorkerKey = nid.WorkerKey };
        Assert.Equal(HttpStatusCode.OK, (await next.PairAsync(worker.PairingCode())).Status);
        Assert.NotEqual(HttpStatusCode.OK, (await nid.DeclarationAsync()).Status);
    }

    [Fact]
    public async Task An_unpaired_worker_refuses_signed_routes()
    {
        await using var worker = new WorkerHarness();
        using var nid = new TestNid(worker.CreateClient());
        await nid.FetchWorkerKeyAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, (await nid.DeclarationAsync()).Status);
    }

    [Fact]
    public async Task After_rotation_the_previous_key_opens_for_24_hours_then_not()
    {
        var clock = new TestClock();
        await using var worker = new WorkerHarness(clock: clock);
        using var nid = new TestNid(worker.CreateClient());
        var oldKey = await nid.FetchWorkerKeyAsync();
        await nid.PairAsync(worker.PairingCode());

        var (status, rotated) = await nid.RotateWorkerKeyAsync();
        Assert.Equal(HttpStatusCode.OK, status);
        var newKey = Convert.FromBase64String(rotated!.NewPublicKey);
        Assert.NotEqual(oldKey, newKey);

        // Restarted on the same data: the previous key and its expiry survive.
        await using var restarted = new WorkerHarness(worker.DataPath, clock);
        using var again = new TestNid(restarted.CreateClient()) { WorkerKey = oldKey };
        again.UseKey(nid.Key);

        clock.Now += TimeSpan.FromHours(23);
        Assert.Equal(HttpStatusCode.OK, (await again.DeclarationAsync()).Status);

        clock.Now += TimeSpan.FromHours(2);
        Assert.NotEqual(HttpStatusCode.OK, (await again.DeclarationAsync()).Status);
        again.WorkerKey = newKey;
        Assert.Equal(HttpStatusCode.OK, (await again.DeclarationAsync()).Status);
    }

    [Fact]
    public async Task Nid_rotation_is_adopted_only_after_confirm_with_the_right_fingerprint()
    {
        await using var worker = new WorkerHarness();
        using var nid = new TestNid(worker.CreateClient());
        await nid.FetchWorkerKeyAsync();
        await nid.PairAsync(worker.PairingCode());
        using var next = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var old = nid.Key;

        Assert.Equal(HttpStatusCode.OK, await nid.SendNidRotationAsync(next));
        nid.UseKey(next);
        Assert.NotEqual(HttpStatusCode.OK, (await nid.DeclarationAsync()).Status);

        Assert.NotEqual(0, Cli.Run(["confirm-rotation", new string('0', 64)], worker.State, TextWriter.Null));
        Assert.Equal(0, Cli.Run(["confirm-rotation", KeyFingerprint.Of(next.ExportSubjectPublicKeyInfo())], worker.State, TextWriter.Null));

        Assert.Equal(WorkerSignatures.KeyId(next), (await nid.DeclarationAsync()).Body!.NidKeyFingerprint);
        nid.UseKey(old);
        Assert.Equal(HttpStatusCode.OK, (await nid.DeclarationAsync()).Status); // the old key stays valid for 24 hours
        old.Dispose();
    }

    [Fact]
    public async Task State_writes_are_atomic()
    {
        await using var worker = new WorkerHarness();
        var before = worker.State.Load();
        await File.WriteAllTextAsync(Path.Combine(worker.DataPath, "state.json.tmp"), "{ half a fi", TestContext.Current.CancellationToken);

        await using var restarted = new WorkerHarness(worker.DataPath);
        using var client = restarted.CreateClient();
        var key = await client.GetFromJsonAsync<TestNid.WorkerKeyView>("/v1/key", TestContext.Current.CancellationToken);

        Assert.Equal(before.WorkerKey, restarted.State.Load().WorkerKey);
        Assert.Equal(KeyFingerprint.Of(WorkerKeyPair.Import(Convert.FromBase64String(before.WorkerKey)).PublicKey), key!.Fingerprint);
    }

    [Fact]
    public async Task Two_processes_updating_the_state_lose_nothing()
    {
        await using var worker = new WorkerHarness();
        var options = Microsoft.Extensions.Options.Options.Create(new WorkerOptions { DataPath = worker.DataPath });
        var server = worker.State;
        var console = new StateStore(options, worker.Clock); // another process: its own lock, its own cache
        server.Update(s => s with { PairingAttemptsLeft = 0 });

        await Task.WhenAll(Enumerable.Range(0, 40).Select(i => Task.Run(() =>
            (i % 2 == 0 ? server : console).Update(s => s with { PairingAttemptsLeft = s.PairingAttemptsLeft + 1 }))));

        Assert.Equal(40, console.Load().PairingAttemptsLeft);
        Assert.Equal(40, server.Load().PairingAttemptsLeft);
    }

    [Fact]
    public async Task An_expired_previous_key_is_erased_from_the_state()
    {
        await using var worker = new WorkerHarness();
        using var nid = new TestNid(worker.CreateClient());
        await nid.FetchWorkerKeyAsync();
        await nid.PairAsync(worker.PairingCode());
        await nid.RotateWorkerKeyAsync();
        Assert.NotNull(worker.State.Load().PreviousWorkerKey);

        worker.Clock.Now += TimeSpan.FromHours(25);
        Cli.Run(["pairing-code"], worker.State, TextWriter.Null);

        Assert.Null(worker.State.Load().PreviousWorkerKey);
        Assert.Null(worker.State.Load().PreviousWorkerKeyUntil);
    }

    [Fact]
    public async Task The_state_file_is_readable_by_its_owner_only()
    {
        await using var worker = new WorkerHarness();
        worker.State.Load();

        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(worker.DataPath, "state.json")));
    }

    [Fact]
    public async Task Pairing_code_command_keeps_a_code_that_is_still_valid()
    {
        await using var worker = new WorkerHarness();
        var shown = worker.PairingCode();

        Cli.Run(["pairing-code"], worker.State, TextWriter.Null);
        Assert.Equal(shown, worker.PairingCode());

        worker.Clock.Now += TimeSpan.FromMinutes(16);
        Cli.Run(["pairing-code"], worker.State, TextWriter.Null);
        Assert.NotEqual(shown, worker.PairingCode());
    }

    [Fact]
    public async Task Pairing_code_command_prints_the_code_or_says_it_is_paired()
    {
        await using var worker = new WorkerHarness();
        using var output = new StringWriter();

        Assert.Equal(0, Cli.Run(["pairing-code"], worker.State, output));
        Assert.Contains(worker.PairingCode()[..4], output.ToString());
    }
}
