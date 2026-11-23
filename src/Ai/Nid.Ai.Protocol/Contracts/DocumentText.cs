namespace Nid.Ai.Protocol.Contracts;

/// <summary>A document's text (<c>contracts/jobs/document-text/v1</c>, #167): the job's file is the document, its media type
/// the one Nid read from its bytes.</summary>
public sealed record DocumentTextInput(string MediaType) : IContract
{
    public static readonly string[] MediaTypes = ["application/pdf", "image/png", "image/jpeg", "message/rfc822"];

    public bool IsValid() => MediaTypes.Contains(MediaType);
}

/// <summary>Where a page's text comes from: the PDF's text layer, OCR (a scan, a photo), an email's body, or nowhere (a page
/// past the OCR bound, or one where OCR read nothing).</summary>
public enum TextSource
{
    TextLayer,
    Ocr,
    Email,
    None,
}

/// <summary>One page's text, numbered from 1. An image or an email is one page.</summary>
public sealed record DocumentTextPage(int Number, string Text, TextSource Source) : IContract
{
    /// <summary>A page holds at most this many characters; the rest is cut.</summary>
    public const int MaxChars = 200_000;

    public bool IsValid() => Number is >= 1 and <= DocumentTextOutput.MaxPages && Text.Length <= MaxChars;
}

public sealed record DocumentTextOutput(IReadOnlyList<DocumentTextPage> Pages) : IContract
{
    public const int MaxPages = 2000;

    public bool IsValid()
        => Pages.Count is >= 1 and <= MaxPages && Pages.All(p => p?.IsValid() == true)
           && Pages.Select(p => p.Number).SequenceEqual(Enumerable.Range(1, Pages.Count));
}
