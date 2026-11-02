using System.Text;
using System.Text.RegularExpressions;
using Quartz;

namespace Nid.Host.Jobs;

/// <summary>
/// Where the runs' log files live: <c>jobs/&lt;job key&gt;/&lt;run id&gt;.log</c> under the same root as <c>nid-*.log</c>
/// (admin jobs design §3.3). A null root means file logs are off (unset, or the folder could not be written).
/// </summary>
public sealed partial class RunLogFolder(string? root)
{
    public bool Available => root is not null;

    /// <summary>The writer of one run, or null when file logs are off or the key could name another folder.</summary>
    public RunLogWriter? Open(string jobKey, Guid runId) => PathOf(jobKey, runId) is { } path ? new RunLogWriter(path) : null;

    /// <summary>The run's lines, or null without a file. Shared read: the run may still be writing it, or a purge removing it.</summary>
    public IReadOnlyList<string>? Read(string jobKey, Guid runId)
    {
        if (PathOf(jobKey, runId) is not { } path) return null;
        try
        {
            using var reader = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
            var lines = new List<string>();
            while (reader.ReadLine() is { } line) lines.Add(line);
            return lines;
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Delete(string jobKey, IEnumerable<Guid> runIds)
    {
        foreach (var id in runIds)
            if (PathOf(jobKey, id) is { } path)
                File.Delete(path);
    }

    private string? PathOf(string jobKey, Guid runId)
        => root is not null && SafeKey().IsMatch(jobKey) ? Path.Combine(root, "jobs", jobKey, $"{runId:D}.log") : null;

    [GeneratedRegex("^[a-z0-9-]+$")]
    private static partial Regex SafeKey();
}

/// <summary>
/// One run's file: created on the first line, cut at 1 MB. Writes after <see cref="Dispose"/> are dropped; any IO failure
/// turns the writer off for the rest of the run, never the run itself.
/// </summary>
public sealed class RunLogWriter(string path) : IDisposable
{
    public const int MaxBytes = 1024 * 1024;
    private readonly Lock _gate = new();
    private StreamWriter? _file;
    private long _bytes;
    private bool _closed;

    public bool HadLines { get; private set; }

    public void Write(string line)
    {
        lock (_gate)
        {
            if (_closed) return;
            try
            {
                if (_file is null)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    // Flushed line by line: the run page reads a run still going, and a killed process loses nothing it wrote.
                    _file = new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete), Encoding.UTF8) { AutoFlush = true };
                }

                var size = Encoding.UTF8.GetByteCount(line) + 1;
                if (_bytes + size > MaxBytes)
                {
                    _file.WriteLine("… log cut at 1 MB");
                    HadLines = true;
                    Close();
                    return;
                }

                _file.WriteLine(line);
                _bytes += size;
                HadLines = true;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Close();
            }
        }
    }

    public void Dispose()
    {
        lock (_gate) Close();
    }

    private void Close()
    {
        _closed = true;
        try { _file?.Dispose(); }
        catch (IOException) { }
        _file = null;
    }
}

/// <summary>
/// Writes the lines logged during a run to that run's file, filtered for the admin area (admin jobs design §3.3,
/// authorization.md §8): the jobs' own lines in full; every other category at Warning and above only, as its message template
/// with the placeholders left unfilled, so no id, label or amount reaches the page.
/// </summary>
public sealed class RunLogProvider(Func<RunLogWriter?> current) : ILoggerProvider
{
    public const string JobsCategory = "Nid.Host.Jobs.";

    public ILogger CreateLogger(string categoryName) => new RunLogger(categoryName, current);

    public void Dispose() { }

    /// <summary>The current run's writer: Quartz's accessor holds the firing on the flow that runs the job.</summary>
    public static Func<RunLogWriter?> FromQuartz(IServiceProvider services)
    {
        IJobExecutionContextAccessor? accessor = null;
        var resolved = false;
        return () =>
        {
            if (!resolved)
            {
                resolved = true;
                accessor = services.GetService<IJobExecutionContextAccessor>();
            }

            return accessor?.Current?.MergedJobDataMap.TryGetValue(RunLogs.WriterKey, out var writer) == true ? writer as RunLogWriter : null;
        };
    }

    private sealed class RunLogger(string category, Func<RunLogWriter?> current) : ILogger
    {
        // The listener closes the run's file then may log about recording it: those lines would reopen nothing, but say nothing either.
        private readonly bool _own = category.StartsWith(JobsCategory, StringComparison.Ordinal) && category != typeof(JobRunListener).FullName;
        private readonly bool _listener = category == typeof(JobRunListener).FullName;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None && !_listener && logLevel >= (_own ? LogLevel.Information : LogLevel.Warning);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel) || current() is not { } writer) return;
            var message = _own ? formatter(state, exception) : Template(state);
            var line = $"{DateTimeOffset.Now:yyyy-MM-ddTHH:mm:ss.fffzzz} [{Level(logLevel)}] {category}: {message}";
            if (exception is not null)
                line += $" {exception.GetType().Name}: {exception.Message}";
            writer.Write(line);
        }

        private static string Template<TState>(TState state)
            => state is IReadOnlyList<KeyValuePair<string, object?>> values
               && values.FirstOrDefault(v => v.Key == "{OriginalFormat}").Value is string template
                ? template
                : "(message hidden)";

        private static string Level(LogLevel level) => level switch
        {
            LogLevel.Trace => "VRB",
            LogLevel.Debug => "DBG",
            LogLevel.Information => "INF",
            LogLevel.Warning => "WRN",
            LogLevel.Error => "ERR",
            _ => "FTL",
        };
    }
}

internal static class RunLogs
{
    public const string WriterKey = "nid.run.log";
}
