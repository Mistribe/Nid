using Microsoft.Extensions.Options;
using Nid.Ai.Protocol.Keys;

namespace Nid.Ai.Worker;

/// <summary>The operator's commands, run on the worker's console
/// (<c>docker compose exec worker dotnet Nid.Ai.Worker.dll &lt;command&gt;</c>). Having the console is what trusting the
/// worker means: nothing here is reachable over the network.</summary>
public static class Cli
{
    public static readonly string[] Commands = ["pairing-code", "unpair", "confirm-rotation", "healthcheck"];

    /// <summary>Runs a command against the state file named by the configuration (<c>Worker__DataPath</c>).</summary>
    public static int Execute(string[] args)
    {
        var options = new WorkerOptions();
        new ConfigurationBuilder().AddEnvironmentVariables().Build().GetSection("Worker").Bind(options);
        var state = new StateStore(Options.Create(options), TimeProvider.System);
        return args[0] == "healthcheck" ? HealthCheck(options.Port, state) : Run(args, state, Console.Out);
    }

    public static int Run(string[] args, StateStore state, TextWriter output)
    {
        switch (args)
        {
            case ["pairing-code"]:
                output.WriteLine(StateStore.Describe(state.CurrentCode()));
                return 0;

            case ["unpair"]:
                var unpaired = state.Update(s => state.WithNewCode(s with
                {
                    NidKey = null, PreviousNidKey = null, PreviousNidKeyUntil = null, PendingNidKey = null,
                }));
                output.WriteLine("Unpaired. Register the worker again in Nid.");
                output.WriteLine(StateStore.Describe(unpaired));
                return 0;

            case ["confirm-rotation", var fingerprint]:
                return ConfirmRotation(state, fingerprint, output);

            default:
                output.WriteLine($"Usage: {string.Join(" | ", Commands)} (confirm-rotation <fingerprint>)");
                return 2;
        }
    }

    private static int ConfirmRotation(StateStore state, string fingerprint, TextWriter output)
    {
        var expected = fingerprint.Replace(" ", "", StringComparison.Ordinal).ToLowerInvariant();
        var after = state.Update(s =>
            // Checked on the key that gets pinned, in the same update: a rotation arriving meanwhile is not pinned unseen.
            s.PendingNidKey is { } pending && KeyFingerprint.Compact(Convert.FromBase64String(pending)) == expected
                ? s with
                {
                    PreviousNidKey = s.NidKey,
                    PreviousNidKeyUntil = state.Time.GetUtcNow() + StateStore.Overlap,
                    NidKey = pending,
                    PendingNidKey = null,
                }
                : s);
        if (after.NidKey is not { } pinned || KeyFingerprint.Compact(Convert.FromBase64String(pinned)) != expected)
        {
            output.WriteLine("No pending Nid key with this fingerprint. Nothing changed.");
            return 1;
        }

        output.WriteLine("Nid's new key is pinned. The previous one stays valid for 24 hours.");
        return 0;
    }

    private static int HealthCheck(int port, StateStore state)
    {
        // The worker's own certificate, self-signed: accepted when it is the one in its state, and no other.
        using var own = SelfSignedCertificate.Load(state.Load().TlsCertificate);
        var expected = SelfSignedCertificate.Sha256(own);
        using var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
                certificate is not null && SelfSignedCertificate.Sha256(certificate) == expected,
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        try
        {
            return client.GetAsync(new Uri($"https://localhost:{port}/healthz")).GetAwaiter().GetResult().IsSuccessStatusCode ? 0 : 1;
        }
        catch (HttpRequestException)
        {
            return 1;
        }
    }
}
