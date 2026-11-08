namespace Nid.Host.Auth;

/// <summary>
/// The two literals that gate every integration-test-only bypass in the host. Both this project and the
/// integration-test harness (<c>NidApp</c>, a different assembly) reference this type directly instead of
/// repeating either string, so a rename here is enforced everywhere at compile time rather than left to be
/// applied consistently by hand across several files.
/// </summary>
public static class IntegrationTestGate
{
    /// <summary>
    /// The host environment name that activates <see cref="IntegrationTestAuthentication"/> (see
    /// <c>Program.cs</c>). Production never sets it — confirmed by
    /// <c>Nid.Core.IntegrationTests.AuthTests</c>, which runs the host under the default environment and
    /// asserts the bypass below has no effect there.
    /// </summary>
    public const string Environment = "IntegrationTest";

    /// <summary>
    /// Names the acting person for a request. Honoured only by
    /// <see cref="IntegrationTestAuthenticationHandler"/>, which itself is only ever the active authentication
    /// scheme when the host environment is <see cref="Environment"/>.
    /// </summary>
    public const string ActorHeader = "X-Nid-Test-Actor";
}
