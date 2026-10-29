using Microsoft.Extensions.DependencyInjection;
using Nid.Documents.Application;
using Nid.Documents.Infrastructure.Storage;
using Nid.TestSupport;

namespace Nid.Documents.IntegrationTests;

public class DocumentStorageWiringTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task Default_kind_resolves_the_file_storage()
    {
        await using var app = await NidApp.StartAsync(fixture);

        Assert.IsType<FileDocumentStorage>(app.Services.GetRequiredService<IDocumentStorage>());
    }

    [Fact]
    public async Task S3_kind_resolves_the_s3_storage()
    {
        await using var app = await NidApp.StartAsync(fixture, settings: new Dictionary<string, string>
        {
            ["Documents:Storage:Kind"] = "s3",
            ["Documents:Storage:ServiceUrl"] = "http://localhost:9",
            ["Documents:Storage:Bucket"] = "docs",
            ["Documents:Storage:AccessKey"] = "k",
            ["Documents:Storage:SecretKey"] = "s",
        });

        Assert.IsType<S3DocumentStorage>(app.Services.GetRequiredService<IDocumentStorage>());
    }

    [Fact]
    public async Task Unknown_kind_stops_the_host_at_startup()
    {
        var ex = await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await using var app = await NidApp.StartAsync(fixture, settings: new Dictionary<string, string> { ["Documents:Storage:Kind"] = "ftp" });
            _ = app.Services; // the host builds lazily
        });

        Assert.Contains("Documents:Storage:Kind 'ftp' is unknown", ex.ToString());
    }
}
