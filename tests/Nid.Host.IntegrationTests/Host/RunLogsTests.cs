using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Nid.Host.Jobs;

namespace Nid.Host.IntegrationTests.Host;

/// <summary>Admin jobs design §3.3: a run's own log file, filtered for the admin area.</summary>
public class RunLogsTests
{
    private static readonly AsyncLocal<RunLogWriter?> Current = new();
    private readonly string _root = Directory.CreateTempSubdirectory("nid-runlogs-").FullName;

    private ILoggerFactory Loggers() => LoggerFactory.Create(b =>
    {
        b.SetMinimumLevel(LogLevel.Trace);
        b.AddProvider(new RunLogProvider(() => Current.Value));
    });

    private (RunLogFolder Folder, Guid Id, RunLogWriter Writer) Run(string job = "fx-daily")
    {
        var folder = new RunLogFolder(_root);
        var id = Guid.NewGuid();
        return (folder, id, folder.Open(job, id)!);
    }

    [Fact]
    public void A_job_line_inside_a_run_lands_in_its_file()
    {
        using var loggers = Loggers();
        var (folder, id, writer) = Run();
        Current.Value = writer;
        loggers.CreateLogger("Nid.Host.Jobs.FxDailyJob").LogInformation("done {Code}", "x");
        Current.Value = null;
        loggers.CreateLogger("Nid.Host.Jobs.FxDailyJob").LogInformation("outside the run");
        writer.Dispose();

        var line = Assert.Single(folder.Read("fx-daily", id)!);
        Assert.EndsWith("[INF] Nid.Host.Jobs.FxDailyJob: done x", line);
        Assert.True(writer.HadLines);
    }

    [Fact]
    public void Other_categories_write_warnings_only_with_placeholders_unfilled()
    {
        using var loggers = Loggers();
        var (folder, id, writer) = Run();
        var person = Guid.NewGuid();
        Current.Value = writer;
        loggers.CreateLogger("Microsoft.EntityFrameworkCore.Database.Command").LogInformation("Executed {Sql}", "select secret");
        loggers.CreateLogger("Nid.Platform.Notifications.Notifier").LogWarning(new InvalidOperationException("bad"), "failed for person {Person}", person);
        Current.Value = null;
        writer.Dispose();

        var content = string.Join('\n', folder.Read("fx-daily", id)!);
        Assert.DoesNotContain("Executed", content);
        Assert.DoesNotContain(person.ToString(), content);
        Assert.Contains("[WRN] Nid.Platform.Notifications.Notifier: failed for person {Person} InvalidOperationException: bad", content);
    }

    [Fact]
    public async Task Concurrent_runs_keep_their_lines_apart()
    {
        using var loggers = Loggers();
        var a = Run("job-a");
        var b = Run("job-b");
        var logger = loggers.CreateLogger("Nid.Host.Jobs.Test");

        await Task.WhenAll(
            Task.Run(async () => { Current.Value = a.Writer; for (var i = 0; i < 50; i++) { logger.LogInformation("marker-a"); await Task.Yield(); } }, TestContext.Current.CancellationToken),
            Task.Run(async () => { Current.Value = b.Writer; for (var i = 0; i < 50; i++) { logger.LogInformation("marker-b"); await Task.Yield(); } }, TestContext.Current.CancellationToken));
        a.Writer.Dispose();
        b.Writer.Dispose();

        Assert.All(a.Folder.Read("job-a", a.Id)!, l => Assert.EndsWith("marker-a", l));
        Assert.All(b.Folder.Read("job-b", b.Id)!, l => Assert.EndsWith("marker-b", l));
        Assert.Equal(50, a.Folder.Read("job-a", a.Id)!.Count);
    }

    [Fact]
    public void A_run_that_logs_nothing_has_no_file()
    {
        var (folder, id, writer) = Run();
        writer.Dispose();

        Assert.False(writer.HadLines);
        Assert.Null(folder.Read("fx-daily", id));
    }

    [Fact]
    public void The_file_is_cut_at_1_MB()
    {
        using var loggers = Loggers();
        var (folder, id, writer) = Run();
        var line = new string('x', 1024);
        Current.Value = writer;
        var logger = loggers.CreateLogger("Nid.Host.Jobs.Test");
        for (var i = 0; i < 1100; i++) logger.LogInformation("{Line}", line);
        Current.Value = null;
        writer.Dispose();

        var path = Path.Combine(_root, "jobs", "fx-daily", $"{id}.log");
        Assert.True(new FileInfo(path).Length <= RunLogWriter.MaxBytes + 64);
        Assert.Equal("… log cut at 1 MB", folder.Read("fx-daily", id)![^1]);
    }

    [Fact]
    public void Without_file_logs_there_is_no_folder_and_no_writer()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();
        using var services = new ServiceCollection().AddLogging(l => l.AddNidFileLogs(configuration)).BuildServiceProvider();

        Assert.Null(services.GetService<RunLogFolder>());
        var off = new RunLogFolder(null);
        Assert.False(off.Available);
        Assert.Null(off.Open("fx-daily", Guid.NewGuid()));
    }

    [Fact]
    public void A_key_with_path_characters_reads_and_writes_nothing()
    {
        var folder = new RunLogFolder(_root);

        Assert.Null(folder.Open("../x", Guid.NewGuid()));
        Assert.Null(folder.Read("../x", Guid.NewGuid()));
        Assert.Null(folder.Read("FX", Guid.NewGuid()));
    }
}
