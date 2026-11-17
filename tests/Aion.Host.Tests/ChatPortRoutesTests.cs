using System.Net;
using Aion.Host.Chat;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Aion.Host.Tests;

public sealed class ChatPortRoutesTests
{
    static readonly IPAddress Outside = IPAddress.Parse("203.0.113.7");

    [Theory]
    [InlineData("/gameLogin/loginWithToken")]
    [InlineData("/stomp")]
    [InlineData("/internal/online")]
    public void Chat_routes_answer_from_anywhere(string path) =>
        Assert.True(ChatPortRoutes.Allows(new PathString(path), Outside));

    [Theory]
    [InlineData("/api/status")]
    [InlineData("/api/auth/login")]
    [InlineData("/")]
    public void Account_routes_never_answer(string path)
    {
        Assert.False(ChatPortRoutes.Allows(new PathString(path), Outside));
        Assert.False(ChatPortRoutes.Allows(new PathString(path), IPAddress.Loopback));
    }

    [Theory]
    [InlineData("/internal/realm/state")]
    [InlineData("/internal/realm/socket")]
    public void Realm_link_answers_managed_worlds_on_loopback_only(string path)
    {
        // A managed world server reaches the realm at http://127.0.0.1:{chat port} (WorldSupervisor).
        Assert.True(ChatPortRoutes.Allows(new PathString(path), IPAddress.Loopback));
        Assert.True(ChatPortRoutes.Allows(new PathString(path), IPAddress.IPv6Loopback));
        Assert.True(ChatPortRoutes.Allows(new PathString(path), IPAddress.Parse("::ffff:127.0.0.1")));
        Assert.False(ChatPortRoutes.Allows(new PathString(path), Outside));
        Assert.False(ChatPortRoutes.Allows(new PathString(path), null));
    }
}
