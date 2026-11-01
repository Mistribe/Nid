using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Nid.Host.IntegrationTests.Host;

/// <summary>#267: rotated log files on a volume, with no more in them than the console has.</summary>
public class FileLogsTests
{
    private static ServiceProvider Logging(string? path, Dictionary<string, string?>? settings = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>(settings ?? []) { ["FileLogs:Path"] = path })
            .Build();
        return new ServiceCollection().AddLogging(logging =>
        {
            logging.AddFilter("Noisy", LogLevel.Warning);
            logging.AddNidFileLogs(configuration);
        }).BuildServiceProvider();
    }

    private static string NewFolder() => Directory.CreateTempSubdirectory("nid-logs-").FullName;

    [Fact]
    public void Files_follow_the_same_level_rules_as_the_console()
    {
        var folder = NewFolder();
        using (var services = Logging(folder))
        {
            var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("Noisy.Sql");
            logger.LogInformation("below the rule");
            logger.LogWarning("above the rule");
        }

        var content = File.ReadAllText(Assert.Single(Directory.GetFiles(folder, "nid-*.log")));
        Assert.Contains("[WRN] Noisy.Sql: above the rule", content);
        Assert.DoesNotContain("below the rule", content);
    }

    [Fact]
    public void A_file_past_the_size_limit_rolls_and_old_files_are_removed()
    {
        var folder = NewFolder();
        foreach (var day in new[] { "20200101", "20200102", "20200103" })
            File.WriteAllText(Path.Combine(folder, $"nid-{day}.log"), "old");

        using (var services = Logging(folder, new() { ["FileLogs:MaxFileSizeMb"] = "1", ["FileLogs:RetainedFiles"] = "2" }))
        {
            var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("Test");
            var line = new string('x', 1024);
            for (var i = 0; i < 1500; i++) logger.LogInformation("{Line}", line);
        }

        var files = Directory.GetFiles(folder, "nid-*.log");
        Assert.Equal(2, files.Length);
        Assert.DoesNotContain(files, f => Path.GetFileName(f).StartsWith("nid-2020", StringComparison.Ordinal));
    }

    [Fact]
    public void A_folder_that_cannot_be_written_does_not_stop_the_host()
    {
        var blocker = Path.Combine(NewFolder(), "a-file");
        File.WriteAllText(blocker, "");
        var error = new StringWriter();
        var console = Console.Error;
        Console.SetError(error);
        try
        {
            using var services = Logging(Path.Combine(blocker, "logs"));

            services.GetRequiredService<ILoggerFactory>().CreateLogger("Test").LogWarning("still running");
        }
        finally
        {
            Console.SetError(console);
        }

        Assert.Contains("File logs are off", error.ToString());
    }

    [Fact]
    public void No_path_means_no_file_provider()
    {
        using var services = Logging(null);

        Assert.Empty(services.GetServices<ILoggerProvider>());
    }
}
