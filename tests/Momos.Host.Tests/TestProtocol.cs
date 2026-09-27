using Momos.Host.Endpoints;

namespace Momos.Host.Tests;

/// <summary>The protocol version a current Worker speaks — read from the Host's own minimum rather
/// than written as a number, so a protocol bump does not have to find every test that means "current".</summary>
internal static class TestProtocol
{
    public static int Current => new WorkerCompatibilityOptions().MinSupportedProtocolVersion;
}
