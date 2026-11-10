using Aion.Host.NcGuard;
using Xunit;

namespace Aion.Host.Tests;

public sealed class NcGuardBuiltinResponsesTests
{
    /// <summary>The bladnoch telemetry POST gets the official 200 text/html with an empty body, not a 404 (A2Core-ChanAge).</summary>
    [Fact]
    public void Bladnoch_post_is_answered_with_an_empty_html_body()
    {
        Assert.True(NcGuardBuiltinResponses.TryReply("bladnoch.ncsoft.com", "POST", "/", out var body, out var contentType));
        Assert.Empty(body);
        Assert.Equal("text/html", contentType);
        Assert.False(NcGuardBuiltinResponses.TryReply("bladnoch.ncsoft.com", "GET", "/other", out _, out _));
    }
}
