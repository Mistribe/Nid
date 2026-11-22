using System.Buffers.Binary;
using System.Security.Cryptography;
using Nid.Ai.Protocol.Sealing;

namespace Nid.Ai.Protocol.Framing;

/// <summary>The worker's side of a response: a random response nonce, then each frame as
/// <c>length (4 bytes, big-endian) || sealed frame</c>. The framing lives inside what is sealed, so a proxy that re-chunks
/// the HTTP body changes nothing (<c>docs/ai/README.md</c> §4.2).</summary>
public sealed class ResponseWriter(ResponseSecret secret, Stream output)
{
    private FrameCipher? _cipher;
    private long _counter;
    private bool _ended;

    public async Task WriteAsync(FrameType type, ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(content.Length, FrameLimits.Default.MaxFramePlaintext);
        if (_ended)
            throw new InvalidOperationException("The response already ended.");

        if (_cipher is null)
        {
            var nonce = RandomNumberGenerator.GetBytes(FrameCipher.ResponseNonceLength);
            _cipher = new FrameCipher(secret, nonce);
            await output.WriteAsync(nonce, cancellationToken);
        }

        var sealedFrame = _cipher.Seal(_counter++, type, content.Span);
        var length = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, sealedFrame.Length);
        await output.WriteAsync(length, cancellationToken);
        await output.WriteAsync(sealedFrame, cancellationToken);
        await output.FlushAsync(cancellationToken);

        if (type is FrameType.End or FrameType.Error)
        {
            _ended = true;
            _cipher.Dispose();
        }
    }
}
