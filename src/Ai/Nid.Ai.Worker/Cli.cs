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
        if (args[0] == "healthcheck")
            return HealthCheck(options.Port);

        return Run(args, new StateStore(Options.Create(options), TimeProvider.System), Console.Out);
    }

    public static int Run(string[] args, StateStore state, TextWriter output)
    {
        switch (args)
        {
            case ["pairing-code"]:
                output.WriteLine(StateStore.Describe(state.Start()));
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
        var current = state.Load();
        if (current.PendingNidKey is not { } pending
            || KeyFingerprint.Compact(Convert.FromBase64String(pending)) != expected)
        {
            output.WriteLine("No pending Nid key with this fingerprint. Nothing changed.");
            return 1;
        }

        state.Update(s => s with
        {
            PreviousNidKey = s.NidKey,
            PreviousNidKeyUntil = state.Time.GetUtcNow() + StateStore.Overlap,
            NidKey = s.PendingNidKey,
            PendingNidKey = null,
        });
        output.WriteLine("Nid's new key is pinned. The previous one stays valid for 24 hours.");
        return 0;
    }

    private static int HealthCheck(int port)
    {
        // The worker's own self-signed certificate, on the loopback: nothing to verify against.
        using var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator };
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
