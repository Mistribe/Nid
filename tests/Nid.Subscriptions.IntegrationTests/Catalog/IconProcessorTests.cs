using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Nid.Subscriptions.Contracts;
using Nid.Subscriptions.Infrastructure.Catalog;
using Nid.TestSupport;
using SkiaSharp;

namespace Nid.Subscriptions.IntegrationTests.Catalog;

/// <summary>1C2c design §4, without HTTP or disk. Every input comes from <see cref="IconSamples"/>.</summary>
public class IconProcessorTests
{
    private static ProcessedIcon Processed(byte[] input) => IconProcessor.Process(input).Ok();

    private static string? Refusal(byte[] input) => IconProcessor.Process(input).FailureCode();

    /// <summary>The chunks of a RIFF/WebP file, in order.</summary>
    private static List<string> Chunks(byte[] webp)
    {
        Assert.Equal("RIFF", Encoding.ASCII.GetString(webp, 0, 4));
        Assert.Equal("WEBP", Encoding.ASCII.GetString(webp, 8, 4));
        var chunks = new List<string>();
        for (var at = 12; at + 8 <= webp.Length;)
        {
            chunks.Add(Encoding.ASCII.GetString(webp, at, 4));
            var size = BinaryPrimitives.ReadInt32LittleEndian(webp.AsSpan(at + 4));
            at += 8 + size + (size % 2);
        }
        return chunks;
    }

    /// <summary>
    /// A simple lossless WebP — a single <c>VP8L</c> chunk, hence no <c>VP8X</c> header, which EXIF, XMP and ICC chunks
    /// require — of the dimensions announced, named by its SHA-256.
    /// </summary>
    private static void AssertStoredForm(ProcessedIcon icon)
    {
        Assert.Equal(["VP8L"], Chunks(icon.Webp));
        using var codec = SKCodec.Create(new SKMemoryStream(icon.Webp));
        Assert.Equal(SKEncodedImageFormat.Webp, codec.EncodedFormat);
        Assert.Equal((icon.Width, icon.Height), (codec.Info.Width, codec.Info.Height));
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(icon.Webp)), icon.Hash);
    }

    private static SKColor PixelOf(ProcessedIcon icon, int x, int y)
    {
        using var bitmap = SKBitmap.Decode(icon.Webp);
        return bitmap.GetPixel(x, y);
    }

    /// <summary>The nearest of the four quadrant colours: JPEG blurs them a little.</summary>
    private static SKColor Nearest(SKColor color)
        => new[] { IconSamples.Red, IconSamples.Green, IconSamples.Blue, IconSamples.White }
            .MinBy(c => Math.Abs(c.Red - color.Red) + Math.Abs(c.Green - color.Green) + Math.Abs(c.Blue - color.Blue));

    public static TheoryData<string> Accepted => new() { "png", "jpeg", "webp", "gif", "cmyk-jpeg" };

    private static byte[] Sample(string format) => format switch
    {
        "png" => IconSamples.Png(200, 100),
        "jpeg" => IconSamples.Jpeg(200, 100),
        "webp" => IconSamples.Webp(200, 100),
        "gif" => IconSamples.Gif(200, 100),
        "cmyk-jpeg" => IconSamples.CmykJpeg, // Review Focus 2: a print logo is converted, not refused
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };

    [Theory]
    [MemberData(nameof(Accepted))]
    public void Png_jpeg_webp_and_gif_become_a_lossless_webp(string format)
    {
        var icon = Processed(Sample(format));

        AssertStoredForm(icon);
        Assert.Equal(format == "cmyk-jpeg" ? (16, 8) : (200, 100), (icon.Width, icon.Height));
    }

    [Fact]
    public void A_cmyk_jpeg_comes_out_in_its_colour()
    {
        var pixel = PixelOf(Processed(IconSamples.CmykJpeg), 8, 4);

        Assert.InRange(pixel.Red, 0x5A, 0x6E); // #6400F6, within JPEG's rounding
        Assert.InRange(pixel.Green, 0x00, 0x0A);
        Assert.InRange(pixel.Blue, 0xEC, 0xFF);
    }

    public static TheoryData<string, string> Refused => new()
    {
        { "svg", IconCodes.UnsupportedFormat },       // Skia has no SVG codec
        { "html", IconCodes.UnsupportedFormat },
        { "bmp", IconCodes.UnsupportedFormat },       // Skia reads BMP; Nid does not
        { "header-only", IconCodes.Undecodable },
        { "truncated", IconCodes.Undecodable },
        { "empty", IconCodes.Undecodable },
    };

    private static byte[] Half(byte[] bytes) => bytes[..(bytes.Length / 2)];

    private static byte[] Unwanted(string name) => name switch
    {
        "svg" => IconSamples.Svg,
        "html" => IconSamples.Html,
        "bmp" => IconSamples.Bmp(10, 10),
        "header-only" => IconSamples.Png(300, 300)[..40],
        "truncated" => Half(IconSamples.Png(300, 300)),
        "empty" => [],
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    [Theory]
    [MemberData(nameof(Refused))]
    public void Anything_but_the_four_formats_or_a_whole_image_is_refused(string name, string reason)
        => Assert.Equal(reason, Refusal(Unwanted(name)));

    [Fact]
    public void More_than_512_KB_is_refused_before_anything_is_read()
        => Assert.Equal(IconCodes.TooLarge, Refusal([.. IconSamples.Png(64, 64), .. new byte[IconLimits.MaxBytes]]));

    [Theory]
    [InlineData(5000, 5000)]
    [InlineData(4097, 1)]
    [InlineData(1, 4097)]
    public void An_image_declaring_more_than_4096_on_a_side_is_refused(int width, int height)
        => Assert.Equal(IconCodes.TooManyPixels, Refusal(IconSamples.DeclaredPng(width, height)));

    /// <summary>
    /// Review Focus 3: 1.6 GB of pixels declared in under a kilobyte. The file holds one row, so had a single pixel
    /// buffer been allocated and decoding attempted, the answer would be <c>undecodable</c> (an incomplete image):
    /// <c>too_many_pixels</c> proves the refusal came from the header alone.
    /// </summary>
    [Fact]
    public void A_decompression_bomb_is_refused_from_its_header_before_any_pixel_buffer()
    {
        var bomb = IconSamples.DeclaredPng(20_000, 20_000);
        Assert.True(bomb.Length < 1024);

        Assert.Equal(IconCodes.TooManyPixels, Refusal(bomb));
        Assert.Equal(IconCodes.Undecodable, Refusal(IconSamples.DeclaredPng(4096, 4096))); // the control: allowed, hence decoded, hence incomplete
    }

    [Fact]
    public void Exactly_4096_on_a_side_is_accepted_and_brought_to_256()
    {
        var icon = Processed(IconSamples.Png(4096, 4096));
        Assert.Equal((256, 256), (icon.Width, icon.Height));
    }

    [Fact]
    public void A_large_image_is_brought_within_256_keeping_its_ratio()
    {
        var icon = Processed(IconSamples.Png(1024, 512));
        AssertStoredForm(icon);
        Assert.Equal((256, 128), (icon.Width, icon.Height));
    }

    [Fact]
    public void A_tall_strip_keeps_its_ratio()
    {
        var icon = Processed(IconSamples.Png(64, 1024));
        Assert.Equal((16, 256), (icon.Width, icon.Height));
    }

    [Fact]
    public void A_small_image_is_never_enlarged()
    {
        var icon = Processed(IconSamples.Png(64, 64));
        Assert.Equal((64, 64), (icon.Width, icon.Height));
    }

    [Fact]
    public void A_resized_logo_keeps_its_colour()
        => Assert.Equal(SKColor.Parse("#6400F6"), PixelOf(Processed(IconSamples.Png(1536, 864)), 128, 72));

    /// <summary>Review Focus 2: the output is encoded from pixels, so no metadata can follow.</summary>
    [Fact]
    public void Exif_and_xmp_are_gone()
    {
        var input = IconSamples.PngWithMetadata(64, 64);
        var carried = Encoding.Latin1.GetString(input);
        Assert.Contains(IconSamples.ExifSecret, carried, StringComparison.Ordinal); // positive control: the input has both
        Assert.Contains(IconSamples.XmpSecret, carried, StringComparison.Ordinal);

        var icon = Processed(input);

        AssertStoredForm(icon);
        var stored = Encoding.Latin1.GetString(icon.Webp);
        Assert.DoesNotContain(IconSamples.ExifSecret, stored, StringComparison.Ordinal);
        Assert.DoesNotContain(IconSamples.XmpSecret, stored, StringComparison.Ordinal);
    }

    /// <summary>
    /// Review Focus 2: the eight EXIF orientations of a 64 × 32 image of four quadrants (red, green / blue, white),
    /// told by the colours of the displayed top-left and top-right corners.
    /// </summary>
    [Theory]
    [InlineData(1, 64, 32, "red", "green")]
    [InlineData(2, 64, 32, "green", "red")]
    [InlineData(3, 64, 32, "white", "blue")]
    [InlineData(4, 64, 32, "blue", "white")]
    [InlineData(5, 32, 64, "red", "blue")]
    [InlineData(6, 32, 64, "blue", "red")]
    [InlineData(7, 32, 64, "white", "green")]
    [InlineData(8, 32, 64, "green", "white")]
    public void A_jpeg_oriented_by_exif_comes_out_as_displayed(ushort orientation, int width, int height, string topLeft, string topRight)
    {
        var icon = Processed(IconSamples.OrientedJpeg(orientation));

        AssertStoredForm(icon);
        Assert.Equal((width, height), (icon.Width, icon.Height));
        Assert.Equal(Named(topLeft), Nearest(PixelOf(icon, 4, 4)));
        Assert.Equal(Named(topRight), Nearest(PixelOf(icon, width - 5, 4)));
    }

    private static SKColor Named(string name) => name switch
    {
        "red" => IconSamples.Red,
        "green" => IconSamples.Green,
        "blue" => IconSamples.Blue,
        _ => IconSamples.White,
    };

    /// <summary>Review Focus 1.</summary>
    [Theory]
    [InlineData("gif")]
    [InlineData("webp")]
    public void An_animation_keeps_its_first_frame_only(string format)
    {
        var input = format == "gif" ? IconSamples.AnimatedGif(50) : IconSamples.AnimatedWebp(20);
        using (var codec = SKCodec.Create(new SKMemoryStream(input)))
            Assert.True(codec.FrameCount > 1); // positive control: the input is an animation

        var icon = Processed(input);

        AssertStoredForm(icon); // one VP8L chunk: a still image
        Assert.Equal(IconSamples.Red, PixelOf(icon, 8, 8)); // the red first frame, not a blue one
    }

    [Fact]
    public void The_same_image_gives_the_same_hash_and_another_image_another()
    {
        var first = Processed(IconSamples.Png(64, 64));
        var again = Processed(IconSamples.Png(64, 64));
        var other = Processed(IconSamples.Png(64, 64, "#F06292"));

        Assert.Equal(first.Hash, again.Hash);
        Assert.Equal(first.Webp, again.Webp);
        Assert.NotEqual(first.Hash, other.Hash);
    }
}
