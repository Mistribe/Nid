using System.Text;

namespace Nid.Documents.Domain;

/// <summary>Decides the media type from the bytes, never from what the client claims.</summary>
public static class ContentSniffer
{
    private static readonly string[] SecondHeaders = ["Date:", "Received:", "MIME-Version:", "Message-ID:"];

    public static string? MediaTypeOf(ReadOnlySpan<byte> content)
    {
        if (content.StartsWith("%PDF-"u8)) return "application/pdf";
        if (content.StartsWith(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A })) return "image/png";
        if (content.StartsWith(new byte[] { 0xFF, 0xD8, 0xFF })) return "image/jpeg";
        return LooksLikeEml(content[..Math.Min(content.Length, HeaderWindow)]) ? "message/rfc822" : null;
    }

    // Real mail carries many Received/ARC/DKIM lines before From:; only the header block is read, never the body.
    private const int HeaderWindow = 64 * 1024;

    private static bool LooksLikeEml(ReadOnlySpan<byte> window)
    {
        var end = window.IndexOf("\n\n"u8);
        var crlf = window.IndexOf("\r\n\r\n"u8);
        var block = window[..(end < 0 ? crlf < 0 ? window.Length : crlf : crlf < 0 ? end : Math.Min(end, crlf))];
        if (block.IsEmpty) return false;
        foreach (var b in block)
            if (b < 0x20 && b is not ((byte)'\t' or (byte)'\r' or (byte)'\n') || b == 0x7F) return false;

        // Latin-1 keeps every byte one char, so 8-bit header values pass through.
        var lines = Encoding.Latin1.GetString(block).Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        var colon = lines[0].IndexOf(':');
        if (colon < 1 || !lines[0][..colon].All(c => char.IsAsciiLetterOrDigit(c) || c == '-')) return false;

        return lines.Any(l => l.StartsWith("From:", StringComparison.OrdinalIgnoreCase))
            && lines.Any(l => SecondHeaders.Any(h => l.StartsWith(h, StringComparison.OrdinalIgnoreCase)));
    }
}
