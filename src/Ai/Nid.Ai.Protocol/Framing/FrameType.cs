namespace Nid.Ai.Protocol.Framing;

/// <summary>The type byte sealed at the head of every frame. A stream ends with exactly one <see cref="End"/> or
/// <see cref="Error"/>.</summary>
public enum FrameType : byte
{
    Data = 1,
    End = 2,
    Error = 3,
}

/// <summary>One opened frame.</summary>
public sealed record Frame(FrameType Type, ReadOnlyMemory<byte> Content);

/// <summary>What a reader accepts: plaintext per frame, frames per stream, plaintext per stream.</summary>
public sealed record FrameLimits(int MaxFramePlaintext, int MaxFrames, long MaxTotalPlaintext)
{
    public static FrameLimits Default { get; } = new(64 * 1024, 10_000, 16L * 1024 * 1024);
}
