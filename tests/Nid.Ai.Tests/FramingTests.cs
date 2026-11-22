using System.Buffers.Binary;
using System.Text;
using Nid.Ai.Protocol;
using Nid.Ai.Protocol.Framing;
using Nid.Ai.Protocol.Sealing;

namespace Nid.Ai.Tests;

public sealed class FramingTests
{
    private const int NonceLength = 16;

    [Fact]
    public async Task Data_frames_then_end_come_back_in_order()
    {
        var secret = Secret();
        var wire = await Write(secret, (FrameType.Data, "a"), (FrameType.Data, "b"), (FrameType.Data, "c"), (FrameType.End, "done"));

        var frames = await Read(secret, wire);

        Assert.Equal([(FrameType.Data, "a"), (FrameType.Data, "b"), (FrameType.Data, "c"), (FrameType.End, "done")], frames);
    }

    [Fact]
    public async Task An_error_frame_ends_the_stream()
    {
        var secret = Secret();
        var wire = await Write(secret, (FrameType.Data, "a"), (FrameType.Error, "worker_failed"));

        Assert.Equal([(FrameType.Data, "a"), (FrameType.Error, "worker_failed")], await Read(secret, wire));
    }

    [Fact]
    public async Task A_stream_without_end_is_truncated()
    {
        var secret = Secret();
        var wire = await Write(secret, (FrameType.Data, "a"), (FrameType.Data, "b"));
        var seen = new List<string>();

        var refused = await Assert.ThrowsAsync<ProtocolException>(async () =>
        {
            await foreach (var frame in ResponseReader.ReadAsync(secret, new MemoryStream(wire), FrameLimits.Default, TestContext.Current.CancellationToken))
                seen.Add(Encoding.UTF8.GetString(frame.Content.Span));
        });

        Assert.Equal("stream_truncated", refused.Code);
        Assert.Equal(["a", "b"], seen);
    }

    [Fact]
    public async Task A_stream_cut_inside_a_frame_is_truncated()
    {
        var secret = Secret();
        var wire = await Write(secret, (FrameType.Data, "a"), (FrameType.End, "done"));

        Assert.Equal("stream_truncated", (await Refusal(secret, wire[..^3])).Code);
    }

    [Fact]
    public async Task A_dropped_frame_is_refused()
    {
        var secret = Secret();
        var (nonce, frames) = Split(await Write(secret, (FrameType.Data, "a"), (FrameType.Data, "b"), (FrameType.End, "")));

        Assert.Equal("frame_invalid", (await Refusal(secret, Join(nonce, frames[0], frames[2]))).Code);
    }

    [Fact]
    public async Task A_repeated_frame_is_refused()
    {
        var secret = Secret();
        var (nonce, frames) = Split(await Write(secret, (FrameType.Data, "a"), (FrameType.End, "")));

        Assert.Equal("frame_invalid", (await Refusal(secret, Join(nonce, frames[0], frames[0], frames[1]))).Code);
    }

    [Fact]
    public async Task Swapped_frames_are_refused()
    {
        var secret = Secret();
        var (nonce, frames) = Split(await Write(secret, (FrameType.Data, "a"), (FrameType.Data, "b"), (FrameType.End, "")));

        Assert.Equal("frame_invalid", (await Refusal(secret, Join(nonce, frames[1], frames[0], frames[2]))).Code);
    }

    [Fact]
    public async Task Bytes_after_end_are_refused()
    {
        var secret = Secret();
        var (nonce, frames) = Split(await Write(secret, (FrameType.Data, "a"), (FrameType.End, "")));

        Assert.Equal("frame_invalid", (await Refusal(secret, Join(nonce, frames[0], frames[1], frames[0]))).Code);
    }

    [Fact]
    public async Task Another_requests_secret_cannot_read_it()
    {
        var wire = await Write(Secret(), (FrameType.Data, "a"), (FrameType.End, ""));

        Assert.Equal("frame_invalid", (await Refusal(Secret(), wire)).Code);
    }

    [Fact]
    public async Task A_declared_length_over_the_limit_is_refused_before_reading()
    {
        var limits = FrameLimits.Default;
        var header = new byte[NonceLength + 4];
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(NonceLength), limits.MaxFramePlaintext + 1 + 16 + 1);
        var input = new GuardedStream(header, readable: header.Length);

        var refused = await Assert.ThrowsAsync<ProtocolException>(async () =>
        {
            await foreach (var _ in ResponseReader.ReadAsync(Secret(), input, limits, TestContext.Current.CancellationToken)) { }
        });

        Assert.Equal("frame_too_large", refused.Code);
    }

    [Fact]
    public async Task Too_many_frames_are_refused()
    {
        var secret = Secret();
        var wire = await Write(secret, (FrameType.Data, "a"), (FrameType.Data, "b"), (FrameType.Data, "c"), (FrameType.End, ""));

        Assert.Equal("stream_too_long", (await Refusal(secret, wire, FrameLimits.Default with { MaxFrames = 2 })).Code);
    }

    [Fact]
    public async Task Too_many_bytes_are_refused()
    {
        var secret = Secret();
        var wire = await Write(secret, (FrameType.Data, "abc"), (FrameType.Data, "def"), (FrameType.End, ""));

        Assert.Equal("stream_too_long", (await Refusal(secret, wire, FrameLimits.Default with { MaxTotalPlaintext = 5 })).Code);
    }

    [Fact]
    public async Task A_frame_over_the_limit_cannot_be_written()
    {
        var writer = new ResponseWriter(Secret(), new MemoryStream());

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => writer.WriteAsync(FrameType.Data, new byte[FrameLimits.Default.MaxFramePlaintext + 1], TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Writing_after_end_throws()
    {
        var writer = new ResponseWriter(Secret(), new MemoryStream());
        await writer.WriteAsync(FrameType.End, ReadOnlyMemory<byte>.Empty, TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => writer.WriteAsync(FrameType.Data, "a"u8.ToArray(), TestContext.Current.CancellationToken));
    }

    private static ResponseSecret Secret()
    {
        using var worker = WorkerKeyPair.Generate();
        return SealedRequest.Seal(worker.PublicKey, "request"u8).Secret;
    }

    private static async Task<byte[]> Write(ResponseSecret secret, params (FrameType Type, string Text)[] frames)
    {
        var output = new MemoryStream();
        var writer = new ResponseWriter(secret, output);
        foreach (var (type, text) in frames)
            await writer.WriteAsync(type, Encoding.UTF8.GetBytes(text), TestContext.Current.CancellationToken);
        return output.ToArray();
    }

    private static async Task<List<(FrameType, string)>> Read(ResponseSecret secret, byte[] wire)
    {
        var frames = new List<(FrameType, string)>();
        await foreach (var frame in ResponseReader.ReadAsync(secret, new MemoryStream(wire), FrameLimits.Default, TestContext.Current.CancellationToken))
            frames.Add((frame.Type, Encoding.UTF8.GetString(frame.Content.Span)));
        return frames;
    }

    private static Task<ProtocolException> Refusal(ResponseSecret secret, byte[] wire, FrameLimits? limits = null)
        => Assert.ThrowsAsync<ProtocolException>(async () =>
        {
            await foreach (var _ in ResponseReader.ReadAsync(secret, new MemoryStream(wire), limits ?? FrameLimits.Default, TestContext.Current.CancellationToken)) { }
        });

    private static (byte[] Nonce, List<byte[]> Frames) Split(byte[] wire)
    {
        var frames = new List<byte[]>();
        for (var at = NonceLength; at < wire.Length;)
        {
            var length = 4 + BinaryPrimitives.ReadInt32BigEndian(wire.AsSpan(at));
            frames.Add(wire[at..(at + length)]);
            at += length;
        }

        return (wire[..NonceLength], frames);
    }

    private static byte[] Join(byte[] nonce, params byte[][] frames) => [.. nonce, .. frames.SelectMany(f => f)];

    /// <summary>A stream that fails the test if the reader asks for more than <paramref name="readable"/> bytes.</summary>
    private sealed class GuardedStream(byte[] content, int readable) : MemoryStream(content)
    {
        public override int Read(byte[] buffer, int offset, int count) => Guard(base.Read(buffer, offset, count));

        public override int Read(Span<byte> buffer) => Guard(base.Read(buffer));

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(Guard(base.Read(buffer.Span)));

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => Task.FromResult(Guard(base.Read(buffer, offset, count)));

        private int Guard(int read)
            => Position > readable || (read == 0 && Position == readable)
                ? throw new InvalidOperationException("Read past the frame header.")
                : read;
    }
}
