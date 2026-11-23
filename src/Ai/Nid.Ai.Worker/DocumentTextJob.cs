using System.Diagnostics;
using System.Text;
using System.Text.Json;
using MimeKit;
using MimeKit.Text;
using Nid.Ai.Protocol;
using Nid.Ai.Protocol.Contracts;

namespace Nid.Ai.Worker;

/// <summary>A document's text (<c>contracts/jobs/document-text/v1</c>, #167): a PDF's text layer page by page (poppler's
/// <c>pdftotext</c>), Tesseract (<c>fra+eng</c>) for a page without one (rendered by <c>pdftoppm</c>) and for an image, an
/// email's subject and body. Declared only where the tools are installed (the worker's image has them).</summary>
public sealed class DocumentTextJob : IJobHandler
{
    /// <summary>A page whose text layer holds fewer letters or digits than this is read by OCR.</summary>
    public const int MinLayerChars = 20;

    /// <summary>OCR reads this many pages at most (about 5 s each at 200 dpi): a job must end within its 10-minute lease.</summary>
    public const int MaxOcrPages = 50;

    /// <summary>The whole text, past which pages are left empty: the result must fit the protocol's 24 MiB.</summary>
    public const int MaxTotalChars = 8_000_000;

    private static readonly TimeSpan ToolTimeout = TimeSpan.FromMinutes(2);
    private static readonly string[] Tools = ["pdftotext", "pdftoppm", "tesseract"];

    public string Type => "document-text";

    public int Version => 1;

    public bool Available { get; } = Tools.All(OnPath);

    public async Task<byte[]> RunAsync(JsonElement input, byte[]? file, CancellationToken ct)
    {
        var request = EchoJob.Read<DocumentTextInput>(JsonSerializer.SerializeToUtf8Bytes(input));
        if (file is null)
            throw new InputInvalidException();

        var work = Directory.CreateTempSubdirectory("nid-text-");
        try
        {
            var path = Path.Combine(work.FullName, "document");
            await File.WriteAllBytesAsync(path, file, ct);
            var pages = request.MediaType switch
            {
                "application/pdf" => await PdfAsync(path, work.FullName, ct),
                "message/rfc822" => [Email(path)],
                _ => [await OcrPageAsync(1, path, ct)],
            };
            return ContractJson.Write(new DocumentTextOutput(Bounded(pages)));
        }
        finally
        {
            work.Delete(recursive: true);
        }
    }

    private static async Task<List<DocumentTextPage>> PdfAsync(string path, string work, CancellationToken ct)
    {
        // One form feed after each page: the text of page n is the n-th piece.
        var layer = (await RunToolAsync("pdftotext", ["-enc", "UTF-8", "-layout", path, "-"], ct)).Split('\f');
        var count = Math.Min(layer.Length - 1, DocumentTextOutput.MaxPages);
        if (count < 1)
            throw new InputInvalidException();

        var pages = new List<DocumentTextPage>(count);
        var ocr = 0;
        for (var n = 1; n <= count; n++)
        {
            var text = layer[n - 1].Trim();
            if (text.Count(char.IsLetterOrDigit) >= MinLayerChars)
                pages.Add(new DocumentTextPage(n, text, TextSource.TextLayer));
            else if (ocr++ < MaxOcrPages)
            {
                var image = Path.Combine(work, "page");
                await RunToolAsync("pdftoppm", ["-r", "200", "-gray", "-f", $"{n}", "-l", $"{n}", "-singlefile", "-png", path, image], ct);
                pages.Add(await OcrPageAsync(n, image + ".png", ct));
            }
            else
                pages.Add(new DocumentTextPage(n, "", TextSource.None));
        }

        return pages;
    }

    private static async Task<DocumentTextPage> OcrPageAsync(int number, string image, CancellationToken ct)
    {
        var text = (await RunToolAsync("tesseract", [image, "stdout", "-l", "fra+eng"], ct)).Trim();
        return new DocumentTextPage(number, text, text.Length == 0 ? TextSource.None : TextSource.Ocr);
    }

    private static DocumentTextPage Email(string path)
    {
        try
        {
            using var message = MimeMessage.Load(path);
            var body = message.TextBody ?? (message.HtmlBody is { } html ? HtmlText(html) : "");
            return new DocumentTextPage(1, $"{message.Subject}\n\n{body}".Trim(), TextSource.Email);
        }
        catch (FormatException)
        {
            throw new InputInvalidException();
        }
    }

    /// <summary>An HTML body's text: its data, a line break at each block, never a script's or a style's.</summary>
    private static string HtmlText(string html)
    {
        var text = new StringBuilder();
        var skipped = false;
        var tokenizer = new HtmlTokenizer(new StringReader(html));
        while (tokenizer.ReadNextToken(out var token))
        {
            switch (token)
            {
                case HtmlDataToken data when !skipped:
                    text.Append(data.Data);
                    break;
                case HtmlTagToken tag when tag.Id is HtmlTagId.Script or HtmlTagId.Style:
                    skipped = !tag.IsEndTag;
                    break;
                case HtmlTagToken tag when tag.Id is HtmlTagId.Br or HtmlTagId.P or HtmlTagId.Div or HtmlTagId.TR or HtmlTagId.LI:
                    text.Append('\n');
                    break;
            }
        }

        return text.ToString();
    }

    /// <summary>Each page cut to its bound, and pages left empty once the whole text reaches its own.</summary>
    private static List<DocumentTextPage> Bounded(List<DocumentTextPage> pages)
    {
        var total = 0;
        return pages.Select(p =>
        {
            var text = p.Text.Length > DocumentTextPage.MaxChars ? p.Text[..DocumentTextPage.MaxChars] : p.Text;
            if (total + text.Length > MaxTotalChars)
                return p with { Text = "", Source = TextSource.None };
            total += text.Length;
            return p with { Text = text };
        }).ToList();
    }

    /// <summary>The tool's standard output; a tool that fails or runs past its timeout fails the job (the file is the problem:
    /// <c>input_invalid</c>, never retried).</summary>
    private static async Task<string> RunToolAsync(string tool, string[] arguments, CancellationToken ct)
    {
        var start = new ProcessStartInfo(tool)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        // Tesseract's own threads on top of the worker's jobs only slow a small box down.
        start.Environment["OMP_THREAD_LIMIT"] = "1";

        using var process = Process.Start(start) ?? throw new InvalidOperationException($"{tool} did not start.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ToolTimeout);
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            _ = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            if (process.ExitCode != 0)
                throw new InputInvalidException();
            return await output;
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            ct.ThrowIfCancellationRequested();
            throw new InputInvalidException();
        }
    }

    private static bool OnPath(string tool)
        => (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Any(dir => dir.Length > 0 && File.Exists(Path.Combine(dir, tool)));
}
