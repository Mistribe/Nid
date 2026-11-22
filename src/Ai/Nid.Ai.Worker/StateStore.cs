using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Nid.Ai.Protocol.Contracts;
using Nid.Ai.Protocol.Keys;
using Nid.Ai.Protocol.Sealing;

namespace Nid.Ai.Worker;

/// <summary>Reads and writes <c>state.json</c>. The console commands (<see cref="Cli"/>) run in another process, so every
/// change holds <c>state.lock</c> (an exclusive lock, across processes) while it reads the file and writes it back. A write
/// goes to a temporary file of its own, created readable by the owner only, then replaces the file: a crash mid-write
/// leaves the previous state. A previous key past its overlap is erased at the next change.</summary>
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
            LoadLocked(); // creates the state first when there is none (it takes the file lock itself)
            using var _ = AcquireFileLock();
            _cached = null; // another process may have written since: read the file under the lock
            var current = LoadLocked();
            var next = Prune(change(current));
            if (next != current)
                Save(next);
            return next;
        }
    }

    private WorkerState Prune(WorkerState state)
    {
        var now = time.GetUtcNow();
        if (state.PreviousWorkerKeyUntil <= now)
            state = state with { PreviousWorkerKey = null, PreviousWorkerKeyUntil = null };
        if (state.PreviousNidKeyUntil <= now)
            state = state with { PreviousNidKey = null, PreviousNidKeyUntil = null };
        return state;
    }

    private FileStream AcquireFileLock()
    {
        Directory.CreateDirectory(options.Value.DataPath);
        var path = Path.Combine(options.Value.DataPath, "state.lock");
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (attempt < 1000)
            {
                Thread.Sleep(10);
            }
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
            using var _ = AcquireFileLock();
            if (!File.Exists(FilePath))
            {
                var created = Initial();
                Save(created);
                return created;
            }
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
        var temporary = $"{FilePath}.{Guid.NewGuid():N}.tmp";
        var create = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows())
            create.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var file = new FileStream(temporary, create))
        {
            JsonSerializer.Serialize(file, state, Json);
            file.Flush(flushToDisk: true);
        }

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
