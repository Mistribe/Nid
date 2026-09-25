using System.Security.Cryptography;
using Nid.Subscriptions.Contracts;
using SkiaSharp;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;

namespace Nid.Subscriptions.Infrastructure.Catalog;

/// <summary>
/// 1C2c design §4, a pure function over SkiaSharp: bytes from a third party in, a lossless WebP within 256 px without
/// metadata out, or a stable reason (<see cref="IconCodes"/>). The codec is chosen from the content; only four of its
/// formats are accepted; the header's dimensions are checked before any pixel buffer exists; only the first frame is
/// decoded; the output is encoded from pixels, so nothing of the source's metadata can travel with it.
/// </summary>
public static class IconProcessor
{
    /// <summary>
    /// Trilinear (linear filtering between linear mipmap levels): a logo shrinks up to 16 times (4096 → 256), and the
    /// mipmaps pre-filter it so thin strokes and small text do not alias, which a cubic or bilinear sample of a 4 × 4
    /// or 2 × 2 neighbourhood alone would do at that ratio.
    /// </summary>
    private static readonly SKSamplingOptions Sampling = new(SKFilterMode.Linear, SKMipmapMode.Linear);

    private static readonly SKWebpEncoderOptions Lossless = new(SKWebpEncoderCompression.Lossless, 100);

    public static Result<ProcessedIcon> Process(byte[] bytes)
    {
        if (bytes.Length > IconLimits.MaxBytes)
            return Refuse(IconCodes.TooLarge, $"The icon is larger than {IconLimits.MaxBytes} bytes.");
        if (bytes.Length == 0)
            return Refuse(IconCodes.Undecodable, "The icon is empty.");

        // The codec takes the stream and is chosen from the content: never from the URL nor the Content-Type.
        using var codec = SKCodec.Create(new SKMemoryStream(bytes), out var opened);
        if (codec is null)
            return opened == SKCodecResult.Unimplemented
                ? Refuse(IconCodes.UnsupportedFormat, "The icon is not in a format Skia reads (an SVG, a web page…).")
                : Refuse(IconCodes.Undecodable, $"The icon's header could not be read ({opened}).");
        if (codec.EncodedFormat is not (SKEncodedImageFormat.Png or SKEncodedImageFormat.Jpeg or SKEncodedImageFormat.Webp or SKEncodedImageFormat.Gif))
            return Refuse(IconCodes.UnsupportedFormat, $"The icon is {codec.EncodedFormat}, not PNG, JPEG, WebP or GIF.");

        var header = codec.Info; // read from the header: no pixel is decoded yet (Review Focus 3)
        if (header.Width > IconLimits.MaxDimension || header.Height > IconLimits.MaxDimension)
            return Refuse(IconCodes.TooManyPixels, $"The icon declares {header.Width} × {header.Height} pixels.");

        // At most 4096 × 4096 × 4 bytes = 64 MB for the output bitmap, allocated only now, plus whatever the decoder
        // itself needs as working memory for that frame (e.g. the coefficient buffers of a progressive JPEG). Decoded
        // into sRGB: a CMYK or tagged image is converted, never refused (Review Focus 2).
        //
        // SKBitmap's allocating constructor throws a plain Exception (not a typed one) when it cannot allocate pixels,
        // and Orient below allocates the same way: this whole decode-to-encode section never lets Process throw for
        // bad input, only an out-of-memory condition (which is not this icon's fault) or a cancellation propagate.
        try
        {
            using var decoded = new SKBitmap(new SKImageInfo(header.Width, header.Height, SKColorType.Rgba8888, SKAlphaType.Premul, SKColorSpace.CreateSrgb()));
            var result = codec.GetPixels(decoded.Info, decoded.GetPixels(), new SKCodecOptions(0)); // the first frame only (Review Focus 1)
            if (result != SKCodecResult.Success)
                return Refuse(IconCodes.Undecodable, $"The icon could not be decoded ({result}).");

            using var fitted = Fit(decoded);
            if (fitted is null)
                return Refuse(IconCodes.Undecodable, "The icon could not be resized.");
            using var oriented = Orient(fitted, codec.EncodedOrigin); // EXIF orientation, applied (Review Focus 2)

            // Encoded from bare pixels without a colour space: no EXIF, XMP or ICC chunk can be written, and the file
            // is a plain lossless 'VP8L' WebP.
            using var pixels = new SKPixmap(oriented.Info.WithColorSpace(null), oriented.GetPixels());
            using var encoded = pixels.Encode(Lossless);
            if (encoded is null)
                return Refuse(IconCodes.Undecodable, "The icon could not be encoded.");

            var webp = encoded.ToArray();
            return Result.Success(new ProcessedIcon(webp, Convert.ToHexStringLower(SHA256.HashData(webp)), oriented.Width, oriented.Height));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
        {
            return Refuse(IconCodes.Undecodable, $"The icon could not be processed ({ex.Message}).");
        }
    }

    /// <summary>Within a 256 px square, ratio kept, never enlarged; a copy either way, so the caller owns what it gets.</summary>
    private static SKBitmap? Fit(SKBitmap source)
    {
        var longest = Math.Max(source.Width, source.Height);
        if (longest <= IconLimits.Size)
            return source.Copy();

        var scale = (double)IconLimits.Size / longest;
        var width = Math.Max(1, (int)Math.Round(source.Width * scale));
        var height = Math.Max(1, (int)Math.Round(source.Height * scale));
        return source.Resize(source.Info.WithSize(width, height), Sampling);
    }

    /// <summary>
    /// The eight EXIF orientations as exact pixel moves (at most 256 × 256 of them, after <see cref="Fit"/>): the
    /// stored pixel (x, y) of a w × h image lands at the position below in the displayed one.
    /// </summary>
    private static SKBitmap Orient(SKBitmap source, SKEncodedOrigin origin)
    {
        int w = source.Width, h = source.Height;
        var turned = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        var target = new SKBitmap(source.Info.WithSize(turned ? h : w, turned ? w : h));
        try
        {
            using var fromPixels = source.PeekPixels();
            using var toPixels = target.PeekPixels();
            var from = fromPixels.GetPixelSpan<uint>();
            var to = toPixels.GetPixelSpan<uint>();
            // A row may be padded beyond width * 4 bytes: read the stride SkiaSharp reports rather than assume the
            // pixels are packed tightly.
            var fromStride = fromPixels.RowBytes / 4;
            var toStride = toPixels.RowBytes / 4;
            for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var (tx, ty) = origin switch
                {
                    SKEncodedOrigin.TopRight => (w - 1 - x, y),              // 2: mirrored
                    SKEncodedOrigin.BottomRight => (w - 1 - x, h - 1 - y),   // 3: turned 180°
                    SKEncodedOrigin.BottomLeft => (x, h - 1 - y),            // 4: flipped
                    SKEncodedOrigin.LeftTop => (y, x),                       // 5: transposed
                    SKEncodedOrigin.RightTop => (h - 1 - y, x),              // 6: turned 90° clockwise
                    SKEncodedOrigin.RightBottom => (h - 1 - y, w - 1 - x),   // 7: transversed
                    SKEncodedOrigin.LeftBottom => (y, w - 1 - x),            // 8: turned 90° counter-clockwise
                    _ => (x, y),                                             // 1: as stored
                };
                to[(ty * toStride) + tx] = from[(y * fromStride) + x];
            }

            return target;
        }
        catch
        {
            // Ownership of `target` has not been handed to the caller yet: anything thrown after it was allocated
            // (PeekPixels included) must dispose it here, or it leaks.
            target.Dispose();
            throw;
        }
    }

    private static Result<ProcessedIcon> Refuse(string reason, string message)
        => Result.Failure<ProcessedIcon>(new Failure(reason, message, null));
}
