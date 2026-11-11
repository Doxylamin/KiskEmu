using Aion.Application.Authentication;
using Aion.Host.Native;
using Aion.Protocol.Native;
using Aion.World;
using Aion.World.Tests;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static Aion.World.Tests.GameSessionTestHelpers;

namespace Aion.Host.Tests;

/// <summary>What a game connection reports as playing (the launcher's Discord status, the account page's "In game").</summary>
public sealed class PlayingReporterTests
{
    const long Now = 1792000000000L;

    /// <summary>Records the reports a connection sends to its world session.</summary>
    sealed class RecordingSessions : IWorldSessions
    {
        public readonly List<(string Token, PlayingCharacter? Character)> Reports = [];
        public bool Fail;
        public Task<WorldSession?> OpenAsync(string ticket, long wireAccountId, string grant, int worldId, CancellationToken ct = default) => Task.FromResult<WorldSession?>(null);
        public Task<WorldIdentity?> ValidateAsync(string token, CancellationToken ct = default) => Task.FromResult<WorldIdentity?>(null);
        public Task CloseAsync(string token, CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> ReportPlayingAsync(string token, PlayingCharacter? character, CancellationToken ct = default)
        {
            if (Fail) throw new HttpRequestException("realm away");
            lock (Reports) Reports.Add((token, character));
            return Task.FromResult(true);
        }
    }

    static GameSession Lobby(FakeCharacterStore store) =>
        new(GameData.Embedded, new GameSessionOptions { CharacterLevel = 45, WorldId = 1204 }, new Random(3)) { Store = store };

    static async Task<GameSession> EnterAsync(FakeCharacterStore store)
    {
        var session = Lobby(store);
        await session.HandleAsync(Login(), Now);
        await session.HandleAsync(LightCreate(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)store.States[0].WireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
        return session;
    }

    static async Task Until(Func<bool> condition, string what)
    {
        var until = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException("Waited in vain for: " + what);
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task The_character_in_the_world_is_what_the_connection_plays()
    {
        var store = new FakeCharacterStore(Guid.NewGuid());
        Assert.Null(PlayingReporter.Of(Lobby(store)));
        var session = await EnterAsync(store);
        var playing = PlayingReporter.Of(session);
        Assert.NotNull(playing);
        Assert.Equal(store.States[0].Summary.CharacterId, playing.CharacterId);
        Assert.Equal(session.CharacterName, playing.Name);
        Assert.Equal(session.Level, playing.Level);
        Assert.Equal(session.ClassName, playing.ClassName);
        Assert.False(string.IsNullOrEmpty(playing.ClassName));
        Assert.Equal(1, playing.Race);
    }

    [Fact]
    public async Task Reports_go_on_change_and_as_a_heartbeat_under_the_session_token()
    {
        var sessions = new RecordingSessions();
        var reporter = new PlayingReporter(sessions, "token-a", NullLogger.Instance);
        var store = new FakeCharacterStore(Guid.NewGuid());
        var lobby = Lobby(store);

        reporter.Tick(lobby, Now, default);
        await Until(() => sessions.Reports.Count == 1, "the first report (the lobby)");
        Assert.Equal(("token-a", (PlayingCharacter?)null), sessions.Reports[0]);
        reporter.Tick(lobby, Now + 1000, default);
        await Task.Delay(50);
        Assert.Single(sessions.Reports);

        var session = await EnterAsync(store);
        reporter.Tick(session, Now + 2000, default);
        await Until(() => sessions.Reports.Count == 2, "the report on entering the world");
        Assert.Equal(session.CharacterName, sessions.Reports[1].Character?.Name);

        var heartbeat = (long)PlayingReporter.Heartbeat.TotalMilliseconds;
        reporter.Tick(session, Now + 2000 + heartbeat - 1, default);
        await Task.Delay(50);
        Assert.Equal(2, sessions.Reports.Count);
        reporter.Tick(session, Now + 2000 + heartbeat, default);
        await Until(() => sessions.Reports.Count == 3, "the heartbeat");
        Assert.All(sessions.Reports, r => Assert.Equal("token-a", r.Token));
    }

    [Fact]
    public async Task A_failed_report_is_tried_again_and_never_throws_into_the_connection()
    {
        var sessions = new RecordingSessions { Fail = true };
        var reporter = new PlayingReporter(sessions, "token-b", NullLogger.Instance);
        var session = await EnterAsync(new FakeCharacterStore(Guid.NewGuid()));
        reporter.Tick(session, Now, default);
        await Task.Delay(50);
        Assert.Empty(sessions.Reports);
        Assert.True(reporter.Due(PlayingReporter.Of(session), Now + 1), "still due after a failure");
        sessions.Fail = false;
        reporter.Tick(session, Now + (long)PlayingReporter.Heartbeat.TotalMilliseconds, default);
        await Until(() => sessions.Reports.Count == 1, "the retried report");
    }
}
