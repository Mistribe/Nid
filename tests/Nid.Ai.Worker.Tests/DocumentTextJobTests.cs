using System.Text.Json;
using Nid.Ai.Protocol;
using Nid.Ai.Protocol.Contracts;

namespace Nid.Ai.Worker.Tests;

/// <summary>#167, against the real tools (poppler, Tesseract fra+eng): installed in CI and in the worker's image; skipped
/// where they are missing.</summary>
public sealed class DocumentTextJobTests
{
    private readonly DocumentTextJob _job = new();

    [Fact]
    public async Task A_pdf_with_a_text_layer_is_read_page_by_page_without_ocr()
    {
        var pages = await RunAsync("text.pdf", "application/pdf");

        Assert.Equal([1, 2], pages.Select(p => p.Number));
        Assert.All(pages, p => Assert.Equal(TextSource.TextLayer, p.Source));
        Assert.Contains("Cotisation annuelle 240 euros", pages[0].Text);
        Assert.Contains("Page deux du contrat", pages[1].Text);
    }

    [Fact]
    public async Task A_scanned_pdf_is_read_by_ocr()
    {
        var page = Assert.Single(await RunAsync("scan.pdf", "application/pdf"));

        Assert.Equal(TextSource.Ocr, page.Source);
        Assert.Contains("Alice Martin", page.Text);
        Assert.Contains("120 euros", page.Text);
    }

    [Fact]
    public async Task A_photo_is_read_by_ocr()
    {
        var page = Assert.Single(await RunAsync("photo.jpg", "image/jpeg"));

        Assert.Equal(TextSource.Ocr, page.Source);
        Assert.Contains("Bob Martin", page.Text);
    }

    [Fact]
    public async Task An_email_gives_its_subject_and_its_body_without_markup()
    {
        var page = Assert.Single(await RunAsync("mail.eml", "message/rfc822"));

        Assert.Equal(TextSource.Email, page.Source);
        Assert.StartsWith("Votre quittance de janvier", page.Text);
        Assert.Contains("Votre loyer de 900 euros est bien reçu.", page.Text);
        Assert.DoesNotContain("color", page.Text);
        Assert.DoesNotContain("<p>", page.Text);
    }

    [Fact]
    public async Task A_file_the_tools_cannot_read_fails_as_an_invalid_input()
    {
        Assert.SkipUnless(_job.Available, "poppler and Tesseract are not installed here");

        await Assert.ThrowsAsync<InputInvalidException>(() => _job.RunAsync(
            Input("application/pdf"), "%PDF-1.4 not really"u8.ToArray(), TestContext.Current.CancellationToken));
    }

    private async Task<IReadOnlyList<DocumentTextPage>> RunAsync(string fixture, string mediaType)
    {
        Assert.SkipUnless(_job.Available || mediaType == "message/rfc822", "poppler and Tesseract are not installed here");
        var file = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", fixture), TestContext.Current.CancellationToken);

        var output = await _job.RunAsync(Input(mediaType), file, TestContext.Current.CancellationToken);

        return ContractJson.Read<DocumentTextOutput>(output).Pages;
    }

    private static JsonElement Input(string mediaType)
        => JsonSerializer.SerializeToElement(new DocumentTextInput(mediaType), ContractJson.Strict);
}
