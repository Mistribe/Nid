using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using SkiaSharp;

namespace Nid.TestSupport;

/// <summary>
/// Every image the icon tests need, generated in memory (1C2c plan, Global Constraints). Skia encodes PNG, JPEG and
/// WebP; GIF, BMP, animated WebP and the metadata Skia never writes (EXIF, XMP) are assembled here byte by byte, each
/// format's structure spelled out. The one exception is <see cref="CmykJpeg"/>: 352 bytes, since no encoder at hand
/// writes CMYK.
/// </summary>
public static class IconSamples
{
    public const string ExifSecret = "exif-secret-7f3a";
    public const string XmpSecret = "xmp-secret-9c1d";

    public static readonly SKColor Red = new(255, 0, 0);
    public static readonly SKColor Green = new(0, 255, 0);
    public static readonly SKColor Blue = new(0, 0, 255);
    public static readonly SKColor White = new(255, 255, 255);

    public static byte[] Png(int width, int height, string color = "#6400F6") => Encode(Solid(width, height, color), SKEncodedImageFormat.Png, 100);

    public static byte[] Jpeg(int width, int height) => Encode(Solid(width, height, "#6400F6"), SKEncodedImageFormat.Jpeg, 90);

    public static byte[] Webp(int width, int height)
    {
        using var bitmap = Solid(width, height, "#6400F6");
        using var pixels = bitmap.PeekPixels();
        return pixels.Encode(new SKWebpEncoderOptions(SKWebpEncoderCompression.Lossy, 80))!.ToArray();
    }

    /// <summary>A 16 × 8 JPEG in CMYK (Adobe APP14), #6400F6 once converted: written by an encoder that has a CMYK mode, kept as is.</summary>
    public static byte[] CmykJpeg { get; } = Convert.FromBase64String(
        "/9j/7gAOQWRvYmUAZAAAAAAA/8AAFAgACAAQBAERAAIRAAMRAAQRAP/EANIAAAEFAQEBAQEBAAAAAAAAAAABAgMEBQYHCAkKCxAAAgEDAwIEAwUFBAQAAAF9AQIDAAQRBRIhMUEGE1FhByJxFDKBkaEII0KxwRVS0fAkM2JyggkKFhcYGRolJicoKSo0NTY3ODk6Q0RFRkdISUpTVFVWV1hZWmNkZWZnaGlqc3R1dnd4eXqDhIWGh4iJipKTlJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5usLDxMXGx8jJytLT1NXW19jZ2uHi4+Tl5ufo6erx8vP09fb3+Pn6/9sAQwADAgIDAgIDAwMDBAMDBAUIBQUEBAUKBwcGCAwKDAwLCgsLDQ4SEA0OEQ4LCxAWEBETFBUVFQwPFxgWFBgSFBUU/9oADgQBAAIAAwAEAAA/APPq/Kqv1Tr9O6KKKK//2Q==");

    /// <summary>A still GIF of one colour (the red of its palette).</summary>
    public static byte[] Gif(int width, int height) => GifOf(width, height, [0]);

    /// <summary>An animated GIF: a red first frame, then blue ones.</summary>
    public static byte[] AnimatedGif(int frames) => GifOf(16, 16, [0, .. Enumerable.Repeat((byte)1, frames - 1)]);

    /// <summary>An animated WebP (VP8X, ANIM, one ANMF per frame): a red first frame, then blue ones.</summary>
    public static byte[] AnimatedWebp(int frames)
    {
        const int size = 16;
        byte[] Frame(SKColor color)
        {
            using var bitmap = new SKBitmap(size, size, SKColorType.Rgba8888, SKAlphaType.Premul);
            bitmap.Erase(color);
            using var pixels = bitmap.PeekPixels();
            var still = pixels.Encode(new SKWebpEncoderOptions(SKWebpEncoderCompression.Lossless, 100))!.ToArray();
            return still[12..]; // the 'VP8L' chunk of a simple lossless file, after 'RIFF', its size and 'WEBP'
        }

        using var body = new MemoryStream();
        var vp8x = new byte[10];
        vp8x[0] = 0x02; // animation
        Write24(vp8x.AsSpan(4), size - 1);
        Write24(vp8x.AsSpan(7), size - 1);
        RiffChunk(body, "VP8X", vp8x);
        RiffChunk(body, "ANIM", new byte[6]); // background 0, loop for ever
        for (var i = 0; i < frames; i++)
        {
            var image = Frame(i == 0 ? Red : Blue);
            var anmf = new byte[16 + image.Length];
            Write24(anmf.AsSpan(6), size - 1);
            Write24(anmf.AsSpan(9), size - 1);
            Write24(anmf.AsSpan(12), 100); // 100 ms
            anmf[15] = 0x02; // do not blend
            image.CopyTo(anmf, 16);
            RiffChunk(body, "ANMF", anmf);
        }

        using var file = new MemoryStream();
        file.Write("RIFF"u8);
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(length, 4 + (int)body.Length);
        file.Write(length);
        file.Write("WEBP"u8);
        body.WriteTo(file);
        return file.ToArray();
    }

    /// <summary>A 24-bit BMP: a format Skia reads and Nid refuses.</summary>
    public static byte[] Bmp(int width, int height)
    {
        var row = ((width * 3) + 3) & ~3;
        var bmp = new byte[54 + (row * height)];
        "BM"u8.CopyTo(bmp);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(2), bmp.Length);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(10), 54);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(14), 40);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(18), width);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(22), height);
        BinaryPrimitives.WriteInt16LittleEndian(bmp.AsSpan(26), 1);
        BinaryPrimitives.WriteInt16LittleEndian(bmp.AsSpan(28), 24);
        return bmp;
    }

    /// <summary>A PNG carrying an EXIF copyright (eXIf) and an XMP packet (iTXt), both with a text the output must not contain.</summary>
    public static byte[] PngWithMetadata(int width, int height)
    {
        var png = Png(width, height);
        var xmp = Encoding.UTF8.GetBytes("XML:com.adobe.xmp\0\0\0\0\0" + $"<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><s>{XmpSecret}</s></x:xmpmeta>");
        return InsertBeforeIdat(png, [("eXIf", Exif(orientation: null, copyright: ExifSecret)), ("iTXt", xmp)]);
    }

    /// <summary>
    /// A 64 × 32 JPEG of four quadrants — red top left, green top right, blue bottom left, white bottom right — tagged
    /// with EXIF orientation <paramref name="orientation"/> (1 to 8) in an APP1 segment.
    /// </summary>
    public static byte[] OrientedJpeg(ushort orientation)
    {
        using var bitmap = new SKBitmap(64, 32, SKColorType.Rgba8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(bitmap))
        {
            using var paint = new SKPaint();
            foreach (var (color, x, y) in new[] { (Red, 0, 0), (Green, 32, 0), (Blue, 0, 16), (White, 32, 16) })
            {
                paint.Color = color;
                canvas.DrawRect(x, y, 32, 16, paint);
            }
        }

        var jpeg = Encode(bitmap.Copy(), SKEncodedImageFormat.Jpeg, 100);
        var exif = Exif(orientation, copyright: null);
        var app1 = new byte[4 + 6 + exif.Length];
        app1[0] = 0xFF;
        app1[1] = 0xE1;
        BinaryPrimitives.WriteUInt16BigEndian(app1.AsSpan(2), (ushort)(app1.Length - 2));
        "Exif\0\0"u8.CopyTo(app1.AsSpan(4));
        exif.CopyTo(app1, 10);
        return [.. jpeg[..2], .. app1, .. jpeg[2..]]; // right after SOI
    }

    /// <summary>
    /// A PNG whose header declares <paramref name="width"/> × <paramref name="height"/> RGBA pixels, followed by one
    /// compressed row: what a decompression bomb looks like to a reader of headers (Review Focus 3).
    /// </summary>
    public static byte[] DeclaredPng(int width, int height)
    {
        using var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8; // bits per channel
        header[9] = 6; // RGBA
        PngChunk(png, "IHDR", header);
        using (var row = new MemoryStream())
        {
            using (var zlib = new ZLibStream(row, CompressionLevel.SmallestSize, leaveOpen: true))
                zlib.Write(new byte[1 + (width * 4)]);
            PngChunk(png, "IDAT", row.ToArray());
        }
        PngChunk(png, "IEND", []);
        return png.ToArray();
    }

    public static byte[] Svg { get; } = Encoding.UTF8.GetBytes("""<svg xmlns="http://www.w3.org/2000/svg" width="10" height="10"><rect width="10" height="10"/></svg>""");

    /// <summary>What a host answers with a 200 when the logo moved: its own error page.</summary>
    public static byte[] Html { get; } = Encoding.UTF8.GetBytes("<!doctype html><html><body>Not found</body></html>");

    private static SKBitmap Solid(int width, int height, string color)
    {
        var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        bitmap.Erase(SKColor.Parse(color));
        return bitmap;
    }

    private static byte[] Encode(SKBitmap bitmap, SKEncodedImageFormat format, int quality)
    {
        using (bitmap)
        using (var data = bitmap.Encode(format, quality))
            return data!.ToArray();
    }

    /// <summary>
    /// A GIF89a of <paramref name="frames"/> (one palette index each: 0 red, 1 blue), with a four-colour global palette
    /// and a Netscape loop when animated. The LZW stream emits a clear code before every pixel, so its codes stay 3 bits
    /// wide and no table is ever built: valid, if not small.
    /// </summary>
    private static byte[] GifOf(int width, int height, byte[] frames)
    {
        using var gif = new MemoryStream();
        gif.Write("GIF89a"u8);
        void Word(int value)
        {
            gif.WriteByte((byte)value);
            gif.WriteByte((byte)(value >> 8));
        }

        Word(width);
        Word(height);
        gif.Write([0x91, 0, 0]); // global palette of 4 colours, 2 bits per primary
        gif.Write([255, 0, 0, 0, 0, 255, 0, 0, 0, 255, 255, 255]); // red, blue, black, white
        if (frames.Length > 1)
            gif.Write([0x21, 0xFF, 0x0B, .. "NETSCAPE2.0"u8, 0x03, 0x01, 0x00, 0x00, 0x00]);

        foreach (var index in frames)
        {
            gif.Write([0x21, 0xF9, 0x04, 0x00, 10, 0, 0, 0]); // graphic control: 100 ms, no transparency
            gif.WriteByte(0x2C);
            Word(0);
            Word(0);
            Word(width);
            Word(height);
            gif.WriteByte(0); // no local palette, not interlaced
            gif.WriteByte(2); // LZW minimum code size: clear = 4, end = 5, codes of 3 bits

            var bits = new List<bool>();
            void Code(int code)
            {
                for (var bit = 0; bit < 3; bit++)
                    bits.Add(((code >> bit) & 1) == 1);
            }

            for (var pixel = 0; pixel < width * height; pixel++)
            {
                Code(4);
                Code(index);
            }
            Code(5);

            var data = new byte[(bits.Count + 7) / 8];
            for (var i = 0; i < bits.Count; i++)
                if (bits[i])
                    data[i / 8] |= (byte)(1 << (i % 8));
            for (var offset = 0; offset < data.Length; offset += 255)
            {
                var block = data.AsSpan(offset, Math.Min(255, data.Length - offset));
                gif.WriteByte((byte)block.Length);
                gif.Write(block);
            }
            gif.WriteByte(0);
        }

        gif.WriteByte(0x3B);
        return gif.ToArray();
    }

    /// <summary>A little-endian TIFF block with an orientation (SHORT) and/or a copyright (ASCII): what EXIF is.</summary>
    private static byte[] Exif(ushort? orientation, string? copyright)
    {
        var entries = (orientation is null ? 0 : 1) + (copyright is null ? 0 : 1);
        var text = copyright is null ? [] : Encoding.ASCII.GetBytes(copyright + "\0");
        var tiff = new byte[8 + 2 + (entries * 12) + 4 + text.Length];
        "II"u8.CopyTo(tiff);
        BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(2), 42);
        BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(4), 8);
        BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(8), (ushort)entries);
        var at = 10;
        if (orientation is { } value)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(at), 0x0112);
            BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(at + 2), 3);
            BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(at + 4), 1);
            BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(at + 8), value);
            at += 12;
        }
        if (copyright is not null)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(at), 0x8298);
            BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(at + 2), 2);
            BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(at + 4), (uint)text.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(at + 8), (uint)(tiff.Length - text.Length));
        }
        text.CopyTo(tiff, tiff.Length - text.Length);
        return tiff;
    }

    private static byte[] InsertBeforeIdat(byte[] png, (string Type, byte[] Data)[] chunks)
    {
        var idat = png.AsSpan().IndexOf("IDAT"u8) - 4; // the chunk starts with its length
        using var output = new MemoryStream();
        output.Write(png, 0, idat);
        foreach (var (type, data) in chunks)
            PngChunk(output, type, data);
        output.Write(png, idat, png.Length - idat);
        return output.ToArray();
    }

    private static void PngChunk(Stream png, string type, byte[] data)
    {
        Span<byte> field = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(field, data.Length);
        png.Write(field);
        var typed = Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
        png.Write(typed);
        BinaryPrimitives.WriteUInt32BigEndian(field, Crc32(typed));
        png.Write(field);
    }

    private static void RiffChunk(Stream riff, string type, byte[] data)
    {
        riff.Write(Encoding.ASCII.GetBytes(type));
        Span<byte> field = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(field, data.Length);
        riff.Write(field);
        riff.Write(data);
        if (data.Length % 2 == 1)
            riff.WriteByte(0);
    }

    private static void Write24(Span<byte> at, int value)
    {
        at[0] = (byte)value;
        at[1] = (byte)(value >> 8);
        at[2] = (byte)(value >> 16);
    }

    /// <summary>The PNG chunk checksum (ISO 3309), written out: a few lines are cheaper than a package for a test helper.</summary>
    private static uint Crc32(byte[] bytes)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in bytes)
        {
            crc ^= b;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }

        return ~crc;
    }
}
