using System.Text;
using Nid.Documents.Domain;

namespace Nid.Documents.Tests;

public class ContentSnifferTests
{
    private static byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s);

    [Fact]
    public void Pdf() => Assert.Equal("application/pdf", ContentSniffer.MediaTypeOf(Ascii("%PDF-1.7\n...")));

    [Fact]
    public void Png() =>
        Assert.Equal("image/png", ContentSniffer.MediaTypeOf(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0 }));

    [Fact]
    public void Jpeg() => Assert.Equal("image/jpeg", ContentSniffer.MediaTypeOf(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }));

    [Fact]
    public void Eml() => Assert.Equal("message/rfc822", ContentSniffer.MediaTypeOf(Ascii(
        "Return-Path: <a@b.c>\r\nFrom: a@b.c\r\nDate: Mon, 26 Oct 2026 10:00:00 +0000\r\nSubject: Hi\r\n\r\nBody")));

    [Theory]
    [InlineData("MZ\u0090\u0000")]
    [InlineData("")]
    [InlineData("hello")]
    [InlineData("From: x\nSubject: y\n\nbody")]
    [InlineData("hello\nFrom: x\nDate: y\n")]
    public void Rejected(string text) => Assert.Null(ContentSniffer.MediaTypeOf(Encoding.Latin1.GetBytes(text)));

    [Fact]
    public void EmlWithBinaryInHeadIsRejected() => Assert.Null(ContentSniffer.MediaTypeOf(
        Encoding.Latin1.GetBytes("Return-Path: <a@b.c>\nFrom: a\nDate: b\n\u0001")));

    [Fact]
    public void EmlWithFromAfterMoreThan4KbOfReceivedHeaders()
    {
        var received = string.Concat(Enumerable.Range(0, 60).Select(i => $"Received: from mx{i}.example.org by mx.example.net with ESMTPS id {i}; Mon, 26 Oct 2026 10:00:00 +0000\r\n"));
        var eml = Ascii(received + "DKIM-Signature: v=1; a=rsa-sha256\r\nFrom: a@b.c\r\nSubject: Hi\r\n\r\nBody");
        Assert.True(received.Length > 4096);
        Assert.Equal("message/rfc822", ContentSniffer.MediaTypeOf(eml));
    }

    [Fact]
    public void EmlWithAn8BitUtf8Body() => Assert.Equal("message/rfc822", ContentSniffer.MediaTypeOf(
        Encoding.UTF8.GetBytes("Return-Path: <a@b.c>\nFrom: a@b.c\nDate: Mon, 26 Oct 2026 10:00:00 +0000\nContent-Type: text/plain; charset=utf-8\n\nBonjour, voilà le relevé")));

    [Fact]
    public void EmlWithNulInTheHeaderBlockIsRejected() => Assert.Null(ContentSniffer.MediaTypeOf(
        Encoding.Latin1.GetBytes("Return-Path: <a@b.c>\nFrom: a\u0000b\nDate: x\n\nBody")));
}
