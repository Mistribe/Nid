extern alias worker;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nid.Ai.Application;
using Nid.Ai.Contracts;
using Nid.Ai.Infrastructure.Persistence;
using Nid.Ai.Protocol;
using Nid.Ai.Protocol.Contracts;
using Nid.Documents.Infrastructure.Persistence;
using DocumentTextBackfill = Nid.Documents.Application.DocumentTextBackfill;
using IDocumentTextStore = Nid.Documents.Application.IDocumentTextStore;
using Nid.Platform.Contracts;
using Nid.TestSupport;
using NodaTime;
using WorkerJobHandler = worker::Nid.Ai.Worker.IJobHandler;
using static Nid.TestSupport.DocumentApi;

namespace Nid.Ai.IntegrationTests;

/// <summary>#167 through Nid and a paired worker: the worker's tools are replaced by a handler that answers what it is told
/// (the real extraction is <c>Nid.Ai.Worker.Tests.DocumentTextJobTests</c>).</summary>
public sealed class DocumentTextTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task An_uploaded_document_gets_its_text_stored_page_by_page()
    {
        var reader = new Reader(file => new DocumentTextOutput([
            new DocumentTextPage(1, $"Avis d'échéance, {file.Length} octets", TextSource.TextLayer),
            new DocumentTextPage(2, "Cotisation annuelle 240 euros", TextSource.Ocr),
        ]));
        await using var world = await StartAsync(reader);
        await world.PairAsync();

        var id = await UploadedAsync(world.Nid, world.Nid.AdultA, Pdf, Filed);
        await RunAsync(world);

        var pages = await PagesAsync(world, id);
        Assert.Equal([1, 2], pages.Select(p => p.Number));
        Assert.Equal($"Avis d'échéance, {Pdf.Length} octets", pages[0].Text); // the worker got the document's bytes
        Assert.Equal(["text-layer", "ocr"], pages.Select(p => p.Source));
        Assert.Equal("application/pdf", reader.MediaTypes.Single());
    }

    [Fact]
    public async Task A_malformed_output_is_refused_and_nothing_is_stored()
    {
        // Pages numbered 2, 3: not what the contract reads.
        await using var world = await StartAsync(new Reader(_ => new DocumentTextOutput([
            new DocumentTextPage(2, "deux", TextSource.Ocr), new DocumentTextPage(3, "trois", TextSource.Ocr)])));
        await world.PairAsync();

        var id = await UploadedAsync(world.Nid, world.Nid.AdultA, Pdf, Filed);
        await RunAsync(world);

        Assert.Empty(await PagesAsync(world, id));
        var job = await JobOfAsync(world, id);
        Assert.Equal(JobCodes.ResultInvalid, job.Error);
        Assert.Null(job.Result);
    }

    [Fact]
    public async Task It_leaves_the_premises_only_once_each_adult_concerned_allows_it()
    {
        await using var world = await StartAsync(new Reader(_ => new DocumentTextOutput([new DocumentTextPage(1, "Quittance", TextSource.Ocr)])));
        await world.PairAsync(placement: "remote");
        await world.Nid.RawAsync(world.Nid.AdultA, HttpMethod.Put, "/api/v1/ai/settings/remote-workers", new { allowed = true });

        // Linked to the household: both adults are concerned.
        var id = await UploadedAsync(world.Nid, world.Nid.AdultA, Pdf, "fileName=quittance.pdf&targetKind=core.household&role=other");
        Assert.Equal(new[] { world.Nid.AdultA.Value, world.Nid.AdultB.Value }.Order(), (await JobOfAsync(world, id)).Concerned.Order());

        await PassAsync(world);
        Assert.Equal(JobCodes.OffsiteNotAllowed, (await JobOfAsync(world, id)).WaitingReason);

        await AllowAsync(world, world.Nid.AdultA);
        world.Clock.Now += Duration.FromSeconds(31);
        await PassAsync(world);
        Assert.Equal(JobCodes.OffsiteNotAllowed, (await JobOfAsync(world, id)).WaitingReason); // Bob still has « ask »

        await AllowAsync(world, world.Nid.AdultB);
        world.Clock.Now += Duration.FromSeconds(31);
        await RunAsync(world);
        Assert.Equal("Quittance", Assert.Single(await PagesAsync(world, id)).Text);
    }

    [Fact]
    public async Task A_payslips_text_waits_for_a_safe_worker()
    {
        // #598: a payslip stays on the premises (llm-providers.md §2.7), its text extraction as much as its embeddings.
        await using var world = await StartAsync(new Reader(_ => new DocumentTextOutput([new DocumentTextPage(1, "Salaire net", TextSource.Ocr)])));
        var worker = await world.PairAsync();

        var id = await UploadedAsync(world.Nid, world.Nid.AdultA, Pdf, "fileName=bulletin.pdf&targetKind=core.household&role=payslip");
        await PassAsync(world);

        var job = await JobOfAsync(world, id);
        Assert.Equal(DataClasses.LocalOnly, job.DataClass);
        Assert.Equal(JobCodes.NoSafeWorker, job.WaitingReason);
        Assert.Empty(await PagesAsync(world, id));

        Assert.Equal(HttpStatusCode.OK, (await world.Nid.RawAsync(world.Nid.AdultA, HttpMethod.Put, $"/api/v1/ai/workers/{worker.Id}/safe", new { safe = true })).StatusCode);
        world.Clock.Now += Duration.FromSeconds(31);
        await RunAsync(world);
        Assert.Equal("Salaire net", Assert.Single(await PagesAsync(world, id)).Text);
    }

    [Fact]
    public async Task An_unfiled_documents_text_waits_for_a_safe_worker_until_it_is_filed()
    {
        // #599: « À classer » may be a payslip until it is filed; filed as another document, the waiting job becomes the household's.
        await using var world = await StartAsync(new Reader(_ => new DocumentTextOutput([new DocumentTextPage(1, "Facture d'eau", TextSource.Ocr)])));
        await world.PairAsync();

        var id = await UploadedAsync(world.Nid, world.Nid.AdultA, Pdf, "fileName=scan.pdf");
        await PassAsync(world);
        var job = await JobOfAsync(world, id);
        Assert.Equal(DataClasses.LocalOnly, job.DataClass);
        Assert.Equal(JobCodes.NoSafeWorker, job.WaitingReason);

        await LinkAsync(world, id, "other");
        job = await JobOfAsync(world, id);
        Assert.Equal(DataClasses.Documents, job.DataClass);
        Assert.Equal(new[] { world.Nid.AdultA.Value, world.Nid.AdultB.Value }.Order(), job.Concerned.Order()); // the household's link

        world.Clock.Now += Duration.FromSeconds(31);
        await RunAsync(world);
        Assert.Equal("Facture d'eau", Assert.Single(await PagesAsync(world, id)).Text);
    }

    [Fact]
    public async Task An_unfiled_document_filed_as_a_payslip_stays_on_a_safe_worker()
    {
        await using var world = await StartAsync(new Reader(_ => new DocumentTextOutput([new DocumentTextPage(1, "Salaire net", TextSource.Ocr)])));
        await world.PairAsync();
        var id = await UploadedAsync(world.Nid, world.Nid.AdultA, Pdf, "fileName=scan.pdf");

        await LinkAsync(world, id, "payslip");
        Assert.Equal(DataClasses.LocalOnly, (await JobOfAsync(world, id)).DataClass);

        // Filed first as another document, then as a payslip in a filing round: the waiting job follows both.
        var other = await UploadedAsync(world.Nid, world.Nid.AdultA, Encoding.ASCII.GetBytes("%PDF-1.4\n% b\n%%EOF\n"), "fileName=b.pdf");
        await LinkAsync(world, other, "other");
        Assert.Equal(DataClasses.Documents, (await JobOfAsync(world, other)).DataClass);
        Assert.Equal(HttpStatusCode.OK, (await world.Nid.RawAsync(world.Nid.AdultA, HttpMethod.Post, $"{DocumentApi.Documents}/filing",
            new { roles = new[] { new { id = other, targetKind = "core.household", role = "payslip" } } })).StatusCode);
        Assert.Equal(DataClasses.LocalOnly, (await JobOfAsync(world, other)).DataClass);

        await RunAsync(world);
        Assert.Empty(await PagesAsync(world, id));
        Assert.Empty(await PagesAsync(world, other));
        Assert.Equal(JobCodes.NoSafeWorker, (await JobOfAsync(world, other)).WaitingReason);
    }

    [Fact]
    public async Task A_finished_job_keeps_its_class()
    {
        await using var world = await StartAsync(new Reader(_ => new DocumentTextOutput([new DocumentTextPage(1, "Quittance", TextSource.Ocr)])));
        await world.PairAsync();
        var id = await UploadedAsync(world.Nid, world.Nid.AdultA, Pdf, Filed);
        await RunAsync(world);

        Assert.Equal(HttpStatusCode.OK, (await world.Nid.RawAsync(world.Nid.AdultA, HttpMethod.Put, $"{DocumentApi.Documents}/{id}/links/core.household/{Guid.Empty}",
            new { role = "payslip" })).StatusCode);

        var job = await JobOfAsync(world, id);
        Assert.Equal(JobState.Succeeded, job.Status);
        Assert.Equal(DataClasses.Documents, job.DataClass);
        await using var scope = Scope(world);
        var jobs = scope.ServiceProvider.GetRequiredService<IModelJobs>();
        Assert.False(await jobs.ReclassAsync($"documents:{id}", DataClasses.LocalOnly, [], TestContext.Current.CancellationToken).OrThrow());
        var refused = await Assert.ThrowsAsync<ModelRefused>(() => jobs.ReclassAsync($"documents:{id}", DataClasses.None, [], TestContext.Current.CancellationToken).OrThrow());
        Assert.Equal(JobCodes.UnknownDataClass, refused.Code);
    }

    [Fact]
    public async Task The_backfill_asks_once_for_the_documents_stored_before_it()
    {
        await using var world = await StartAsync(new Reader(_ => new DocumentTextOutput([new DocumentTextPage(1, "x", TextSource.Ocr)])));
        var first = await UploadedAsync(world.Nid, world.Nid.AdultA, Pdf, "fileName=a.pdf");
        var second = await UploadedAsync(world.Nid, world.Nid.AdultB, Encoding.ASCII.GetBytes("%PDF-1.4\n% b\n%%EOF\n"), "fileName=b.pdf");
        await BackfillAsync(world); // whatever the golden case stored
        await using (var scope = Scope(world))
            await scope.ServiceProvider.GetRequiredService<DocumentsDbContext>().TextJobs.Where(j => j.DocumentId == first || j.DocumentId == second)
                .ExecuteDeleteAsync(TestContext.Current.CancellationToken);

        var queued = await BackfillAsync(world);
        var again = await BackfillAsync(world);

        Assert.Equal(2, queued);
        Assert.Equal(0, again);
        Assert.Equal([world.Nid.AdultB.Value], (await JobOfAsync(world, second)).Concerned);
        Assert.Equal(JobState.Queued, (await JobOfAsync(world, first)).Status);
    }

    [Fact]
    public async Task Deleting_a_document_deletes_its_text_and_its_jobs_result()
    {
        await using var world = await StartAsync(new Reader(_ => new DocumentTextOutput([new DocumentTextPage(1, "Bail", TextSource.TextLayer)])));
        await world.PairAsync();
        var id = await UploadedAsync(world.Nid, world.Nid.AdultA, Pdf, Filed);
        await RunAsync(world);
        Assert.Single(await PagesAsync(world, id));
        Assert.Equal("Bail", JsonDocument.Parse((await JobOfAsync(world, id)).Result!).RootElement.GetProperty("pages")[0].GetProperty("text").GetString());

        await DeletedAsync(world.Nid, world.Nid.AdultA, id);

        Assert.Empty(await PagesAsync(world, id));
        await using var check = Scope(world);
        Assert.False(await check.ServiceProvider.GetRequiredService<DocumentsDbContext>().TextJobs.AnyAsync(j => j.DocumentId == id, TestContext.Current.CancellationToken));
        // #600: the job's result (the text itself) goes at once, not at the purge.
        Assert.False(await check.ServiceProvider.GetRequiredService<AiDbContext>().Jobs.AnyAsync(j => j.FileRef == $"documents:{id}", TestContext.Current.CancellationToken));
    }

    /// <summary>Filed with the household (any role but a payslip): the household's <c>documents</c>.</summary>
    private const string Filed = "fileName=bail.pdf&targetKind=core.household&role=other";

    private static async Task LinkAsync(PairedWorld world, Guid document, string role)
        => Assert.Equal(HttpStatusCode.OK, (await world.Nid.RawAsync(world.Nid.AdultA, HttpMethod.Post, $"{DocumentApi.Documents}/{document}/links",
            new { targetKind = "core.household", role })).StatusCode);

    private Task<PairedWorld> StartAsync(Reader reader)
        => PairedWorld.StartAsync(fixture, workerServices: s => s.AddSingleton<WorkerJobHandler>(reader));

    /// <summary>Sends, then polls past the first 5 seconds: the job is done on the worker by then (a few milliseconds).</summary>
    private static async Task RunAsync(PairedWorld world)
    {
        for (var i = 0; i < 5; i++)
        {
            await PassAsync(world);
            await Task.Delay(50, TestContext.Current.CancellationToken);
            world.Clock.Now += Duration.FromSeconds(6);
        }
    }

    private static async Task PassAsync(PairedWorld world)
    {
        await using var scope = Scope(world);
        await scope.ServiceProvider.GetRequiredService<IJobDispatcher>().RunOnceAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<int> BackfillAsync(PairedWorld world)
    {
        await using var scope = Scope(world);
        return await scope.ServiceProvider.GetRequiredService<DocumentTextBackfill>().RunAsync(TestContext.Current.CancellationToken);
    }

    private static async Task AllowAsync(PairedWorld world, Core.Contracts.PersonId adult)
        => Assert.Equal(HttpStatusCode.OK, (await world.Nid.RawAsync(adult, HttpMethod.Put, "/api/v1/ai/assistant/me/offsite", new { preference = "allow" })).StatusCode);

    private static async Task<IReadOnlyList<Documents.Application.DocumentTextPage>> PagesAsync(PairedWorld world, Guid document)
    {
        await using var scope = Scope(world);
        return await scope.ServiceProvider.GetRequiredService<IDocumentTextStore>().PagesAsync(document, TestContext.Current.CancellationToken);
    }

    private static async Task<JobRecord> JobOfAsync(PairedWorld world, Guid document)
    {
        await using var scope = Scope(world);
        var row = await scope.ServiceProvider.GetRequiredService<AiDbContext>().Jobs.AsNoTracking()
            .SingleAsync(j => j.FileRef == $"documents:{document}", TestContext.Current.CancellationToken);
        return (await scope.ServiceProvider.GetRequiredService<IJobStore>().FindAsync(row.Id, TestContext.Current.CancellationToken))!;
    }

    private static AsyncServiceScope Scope(PairedWorld world)
    {
        var scope = world.Nid.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
        return scope;
    }

    /// <summary>The worker's <c>document-text</c>, answering <paramref name="answer"/> for the file it got.</summary>
    private sealed class Reader(Func<byte[], DocumentTextOutput> answer) : WorkerJobHandler
    {
        public List<string> MediaTypes { get; } = [];

        public string Type => "document-text";

        public int Version => 1;

        public Task<byte[]> RunAsync(JsonElement input, byte[]? file, CancellationToken ct)
        {
            lock (MediaTypes)
                MediaTypes.Add(input.GetProperty("mediaType").GetString()!);
            // Written as is, not through ContractJson.Write, which would refuse the malformed case.
            return Task.FromResult(JsonSerializer.SerializeToUtf8Bytes(answer(file!), ContractJson.Strict));
        }
    }
}
