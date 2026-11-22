using System.Text;
using Nid.Ai.Protocol;
using Nid.Ai.Protocol.Sealing;

namespace Nid.Ai.Tests;

public sealed class SealedRequestTests
{
    private static readonly byte[] Hello = Encoding.UTF8.GetBytes("hello");

    [Fact]
    public void A_sealed_request_opens_with_the_worker_key()
    {
        using var worker = WorkerKeyPair.Generate();

        var (message, sent) = SealedRequest.Seal(worker.PublicKey, Hello);
        var (plaintext, received) = SealedRequest.Open(worker, message);

        Assert.Equal(Hello, plaintext);
        Assert.Equal(sent.ToBytes(), received.ToBytes());
    }

    [Fact]
    public void Another_key_cannot_open_it()
    {
        using var worker = WorkerKeyPair.Generate();
        using var other = WorkerKeyPair.Generate();
        var (message, _) = SealedRequest.Seal(worker.PublicKey, Hello);

        Assert.Equal("sealed_invalid", Assert.Throws<ProtocolException>(() => SealedRequest.Open(other, message)).Code);
    }

    [Theory]
    [InlineData(-1)] // last byte of the ciphertext
    [InlineData(10)] // inside enc
    public void A_flipped_byte_is_refused(int position)
    {
        using var worker = WorkerKeyPair.Generate();
        var (message, _) = SealedRequest.Seal(worker.PublicKey, Hello);
        message[position < 0 ? message.Length + position : position] ^= 0x01;

        Assert.Equal("sealed_invalid", Assert.Throws<ProtocolException>(() => SealedRequest.Open(worker, message)).Code);
    }

    [Fact]
    public void A_message_shorter_than_enc_is_refused()
    {
        using var worker = WorkerKeyPair.Generate();

        Assert.Equal("sealed_invalid", Assert.Throws<ProtocolException>(() => SealedRequest.Open(worker, new byte[10])).Code);
    }

    [Fact]
    public void Two_seals_of_the_same_text_differ()
    {
        using var worker = WorkerKeyPair.Generate();
        var (first, firstSecret) = SealedRequest.Seal(worker.PublicKey, Hello);
        var (second, secondSecret) = SealedRequest.Seal(worker.PublicKey, Hello);

        Assert.NotEqual(first, second);
        Assert.NotEqual(firstSecret.ToBytes(), secondSecret.ToBytes());
    }

    [Fact]
    public void A_secret_round_trips_and_a_bad_length_is_refused()
    {
        using var worker = WorkerKeyPair.Generate();
        var (_, secret) = SealedRequest.Seal(worker.PublicKey, Hello);

        Assert.Equal(secret.ToBytes(), ResponseSecret.FromBytes(secret.ToBytes()).ToBytes());
        Assert.Equal("sealed_invalid", Assert.Throws<ProtocolException>(() => ResponseSecret.FromBytes(new byte[80])).Code);
    }

    [Fact]
    public void A_key_pair_round_trips_through_its_export()
    {
        using var worker = WorkerKeyPair.Generate();
        using var imported = WorkerKeyPair.Import(worker.ExportPrivateKey());

        Assert.Equal(worker.PublicKey, imported.PublicKey);
        var (message, _) = SealedRequest.Seal(worker.PublicKey, Hello);
        Assert.Equal(Hello, SealedRequest.Open(imported, message).Plaintext);
    }

    [Fact]
    public void A_bad_private_key_is_refused()
        => Assert.Equal("sealed_invalid", Assert.Throws<ProtocolException>(() => WorkerKeyPair.Import(new byte[5])).Code);
}
