using System.Net;
using Microsoft.AspNetCore.Http;

namespace Aion.Host.Chat;

/// <summary>
/// What the chat port answers (#80). It is plain HTTP on the bind address with no rate limiter, so it serves the chat's
/// own routes only, never the account site, sign-in, registration or the staff API. The realm link (/internal/realm)
/// is answered from loopback only: a managed world server reaches the realm over 127.0.0.1 on this port
/// (WorldSupervisor sets Aion__Realm__Url). OURS.
/// </summary>
public static class ChatPortRoutes
{
    static readonly string[] Chat = ["/gameLogin", "/gameClient", "/stomp", "/internal/online", "/internal/world", "/internal/announce"];
    const string RealmLink = "/internal/realm";

    public static bool Allows(PathString path, IPAddress? remote) =>
        Chat.Any(route => path.StartsWithSegments(route))
        || path.StartsWithSegments(RealmLink) && remote is not null && IPAddress.IsLoopback(remote);
}
