using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Nid.Ai.Protocol.Contracts;
using Nid.Ai.Protocol.Keys;
using Nid.Ai.Protocol.Sealing;

namespace Nid.Ai.Worker;

/// <summary>Reads and writes <c>state.json</c>. A write goes to <c>state.json.tmp</c> first, then replaces the file, so a
/// crash mid-write leaves the previous state. The file is re-read when it changed on disk: the console commands
/// (<see cref="Cli"/>) run in another process.</summary>
public sealed class StateStore(IOptions<WorkerOptions> options, TimeProvider time)
{
    public static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(15);
    public const int CodeAttempts = 5;
    public static readonly TimeSpan Overlap = TimeSpan.FromHours(24);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly Lock _lock = new();
    private WorkerState? _cached;
    private DateTime _cachedAt;

    private string FilePath => Path.Combine(options.Value.DataPath, "state.json");

    public TimeProvider Time => time;

    public WorkerState Load()
    {
        lock (_lock)
            return LoadLocked();
    }

    public WorkerState Update(Func<WorkerState, WorkerState> change)
    {
        lock (_lock)
        {
            var next = change(LoadLocked());
            Save(next);
            return next;
        }
    }

    /// <summary>At start: a worker that is not paired gets a fresh code, so a restart is how an operator gets a new one.</summary>
    public WorkerState Start()
        => Update(state => state.IsPaired ? state : WithNewCode(state));

    /// <summary>For the console: the code the operator was shown stays while it can still be used.</summary>
    public WorkerState CurrentCode()
        => Update(state => state.IsPaired
                           || (state.PairingCode is not null && state.PairingCodeUntil > time.GetUtcNow() && state.PairingAttemptsLeft > 0)
            ? state
            : WithNewCode(state));

    public WorkerState WithNewCode(WorkerState state) => state with
    {
        PairingCode = RandomNumberGenerator.GetString(PairRequest.CodeAlphabet, PairRequest.CodeLength),
        PairingCodeUntil = time.GetUtcNow() + CodeLifetime,
        PairingAttemptsLeft = CodeAttempts,
    };

    public static string Describe(WorkerState state)
    {
        using var key = WorkerKeyPair.Import(Convert.FromBase64String(state.WorkerKey));
        var fingerprint = KeyFingerprint.Of(key.PublicKey);
        return state.IsPaired
            ? $"Paired. Key fingerprint: {fingerprint}"
            : $"Pairing code: {state.PairingCode![..4]}-{state.PairingCode[4..]}  Key fingerprint: {fingerprint}";
    }

    private WorkerState LoadLocked()
    {
        if (!File.Exists(FilePath))
        {
            Directory.CreateDirectory(options.Value.DataPath);
            var created = Initial();
            Save(created);
            return created;
        }

        var writtenAt = File.GetLastWriteTimeUtc(FilePath);
        if (_cached is null || writtenAt != _cachedAt)
        {
            _cached = JsonSerializer.Deserialize<WorkerState>(File.ReadAllBytes(FilePath), Json)
                      ?? throw new InvalidDataException($"{FilePath} is empty.");
            _cachedAt = writtenAt;
        }

        return _cached;
    }

    private void Save(WorkerState state)
    {
        var temporary = FilePath + ".tmp";
        using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(file, state, Json);
            file.Flush(flushToDisk: true);
        }

        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(temporary, FilePath, overwrite: true);

        _cached = state;
        _cachedAt = File.GetLastWriteTimeUtc(FilePath);
    }

    private WorkerState Initial()
    {
        using var key = WorkerKeyPair.Generate();
        var hostnames = options.Value.Hostnames.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return WithNewCode(new WorkerState
        {
            WorkerKey = Convert.ToBase64String(key.ExportPrivateKey()),
            TlsCertificate = Convert.ToBase64String(SelfSignedCertificate.Create(hostnames, time.GetUtcNow())),
        });
    }
}
