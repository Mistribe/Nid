using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Nid.Ai.Protocol.Sealing;

namespace Nid.Ai.Protocol.Framing;

/// <summary>Nid's side of a response: opens each frame of <see cref="ResponseWriter"/> in order. It yields the data frames,
/// then the final <see cref="FrameType.End"/> or <see cref="FrameType.Error"/>, and throws <see cref="ProtocolException"/>
/// for anything else: a stream that stops before its last frame is never a response.</summary>
public static class ResponseReader
{
    public static async IAsyncEnumerable<Frame> ReadAsync(
        ResponseSecret secret, Stream input, FrameLimits limits, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var nonce = new byte[FrameCipher.ResponseNonceLength];
        await ReadExactly(input, nonce, cancellationToken);

        using var cipher = new FrameCipher(secret, nonce);
        var length = new byte[4];
        long total = 0;
        for (long counter = 0; ; counter++)
        {
            await ReadExactly(input, length, cancellationToken);
            var sealedLength = BinaryPrimitives.ReadInt32BigEndian(length);
            if (sealedLength > limits.MaxFramePlaintext + 1 + FrameCipher.TagLength)
                throw new ProtocolException("frame_too_large");
            if (sealedLength < 1 + FrameCipher.TagLength)
                throw new ProtocolException("frame_invalid");
            if (counter >= limits.MaxFrames)
                throw new ProtocolException("stream_too_long");

            var sealedFrame = new byte[sealedLength];
            await ReadExactly(input, sealedFrame, cancellationToken);
            var frame = Open(cipher, counter, sealedFrame);

            total += frame.Content.Length;
            if (total > limits.MaxTotalPlaintext)
                throw new ProtocolException("stream_too_long");

            if (frame.Type is FrameType.Data)
            {
                yield return frame;
                continue;
            }

            if (await input.ReadAsync(new byte[1], cancellationToken) != 0)
                throw new ProtocolException("frame_invalid");

            yield return frame;
            yield break;
        }
    }

    private static Frame Open(FrameCipher cipher, long counter, byte[] sealedFrame)
    {
        byte[] plaintext;
        try
        {
            plaintext = cipher.Open(counter, sealedFrame);
        }
        catch (CryptographicException)
        {
            throw new ProtocolException("frame_invalid");
        }

        var type = (FrameType)plaintext[0];
        return Enum.IsDefined(type) ? new Frame(type, plaintext.AsMemory(1)) : throw new ProtocolException("frame_invalid");
    }

    private static async Task ReadExactly(Stream input, byte[] buffer, CancellationToken cancellationToken)
    {
        if (await input.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, cancellationToken) < buffer.Length)
            throw new ProtocolException("stream_truncated");
    }
}
