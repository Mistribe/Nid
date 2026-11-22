using System.Net;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using NSign;
using NSign.Client;
using NSign.Http;
using NSign.Signatures;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace Nid.Ai.Protocol.Signing;

/// <summary>Signs one request with one key, as <see cref="WorkerSignatures.ConfigureSigning"/> describes. Nid signs each
/// worker's requests with the key that worker pinned, which a client-wide signing handler cannot do.</summary>
public static class WorkerRequestSigning
{
    private static readonly MessageSigningOptions Options = Configured();

    public static async Task SignAsync(HttpRequestMessage request, ECDsa key, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
        request.Content ??= new ByteArrayContent([]);
        request.Content.Headers.Remove("Content-Digest");
        request.Content.Headers.TryAddWithoutValidation("Content-Digest", $"sha-256=:{Convert.ToBase64String(SHA256.HashData(body))}:");

        // NSign signs inside a message handler only (its message context is internal): run its handler once, over an
        // inner handler that sends nothing.
        var signing = new SigningHandler(
            NullLogger<SigningHandler>.Instance,
            new DefaultMessageSigner(NullLogger<DefaultMessageSigner>.Instance, WorkerSignatures.Signer(key)),
            MsOptions.Create(new HttpFieldOptions()),
            MsOptions.Create(Options))
        {
            InnerHandler = new SendsNothing(),
        };
        using var invoker = new HttpMessageInvoker(signing);
        using var _ = await invoker.SendAsync(request, cancellationToken);
    }

    private static MessageSigningOptions Configured()
    {
        var options = new MessageSigningOptions();
        WorkerSignatures.ConfigureSigning(options);
        return options;
    }

    private sealed class SendsNothing : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
    }
}
