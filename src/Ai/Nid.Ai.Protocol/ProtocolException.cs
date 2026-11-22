namespace Nid.Ai.Protocol;

/// <summary>A message the protocol refuses. <see cref="Code"/> says why, never what the message held:
/// <c>sealed_invalid</c>, <c>frame_invalid</c>, <c>frame_too_large</c>, <c>stream_truncated</c>, <c>stream_too_long</c>,
/// <c>contract_invalid</c>.</summary>
public sealed class ProtocolException(string code) : Exception($"Protocol message refused: {code}.")
{
    public string Code { get; } = code;
}
