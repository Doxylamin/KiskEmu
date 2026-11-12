using System.Text.Json;
using Aion.Host.Worlds;
using Xunit;

namespace Aion.Host.Tests;

/// <summary>
/// The realm's side of a character move to another world (CharacterWorldMoves, docs/STAFF_CHARACTERS.md): the steps run in
/// order on the right worlds, a refused import cancels the move at the source, and a world that stops answering leaves the
/// move incomplete (the character stays blocked) instead of cancelling or deleting anything.
/// </summary>
public sealed class CharacterWorldMoveTests
{
    const int Source = 1204, Target = 1205;
    static readonly Guid Actor = Guid.NewGuid(), Character = Guid.NewGuid(), Account = Guid.NewGuid(), Moved = Guid.NewGuid();

    sealed class FakeLink(Func<int, string, JsonElement, WorldMoveStep> answer) : IWorldMoveLink
    {
        public readonly List<(int World, string Step, JsonElement Body)> Calls = [];
        public Task<WorldMoveStep> MoveStepAsync(int worldId, string step, object body, CancellationToken ct)
        {
            var json = JsonSerializer.SerializeToElement(body, JsonSerializerOptions.Web);
            Calls.Add((worldId, step, json));
            return Task.FromResult(answer(worldId, step, json));
        }
    }

    static WorldMoveStep Ok(object body) => new(200, JsonSerializer.SerializeToElement(body, JsonSerializerOptions.Web));
    static WorldMoveStep Error(int status, string code) => new(status, JsonSerializer.SerializeToElement(new { error = code }));
    static readonly WorldMoveStep Package = Ok(new { requestId = Guid.Empty, sourceWorld = Source, name = "Doxy" });
    static readonly WorldMoveStep Imported = Ok(new { characterId = Moved, name = "Doxy", accountId = Account, worldId = Target, lobbySlot = 0 });
    static WorldMoveRequest Request() => new(Guid.NewGuid(), Character, Source, Target, Account, null, "merge");

    static string ErrorOf(object body) => JsonSerializer.SerializeToElement(body).GetProperty("error").GetString()!;

    [Fact]
    public async Task A_move_exports_imports_and_finishes_on_the_right_worlds()
    {
        var link = new FakeLink((_, step, _) => step switch { "export" => Package, "import" => Imported, _ => Ok(new { done = true }) });
        var (status, body) = await CharacterWorldMoves.RunAsync(link, Request(), Actor, default);
        Assert.Equal(200, status);
        Assert.Equal(Moved, JsonSerializer.SerializeToElement(body).GetProperty("characterId").GetGuid());
        Assert.Equal([(Source, "export"), (Target, "import"), (Source, "finish")], link.Calls.Select(c => (c.World, c.Step)));
        // The package travels to the target as it came; the source deletes the original for the new character.
        Assert.Equal("Doxy", link.Calls[1].Body.GetProperty("package").GetProperty("name").GetString());
        Assert.Equal(Account, link.Calls[1].Body.GetProperty("targetAccount").GetGuid());
        Assert.Equal(Moved, link.Calls[2].Body.GetProperty("movedTo").GetGuid());
        Assert.Equal(Actor, link.Calls[2].Body.GetProperty("actor").GetGuid());
    }

    [Fact]
    public async Task A_refused_export_stops_before_the_target()
    {
        var link = new FakeLink((_, _, _) => Error(409, "character_online"));
        var (status, body) = await CharacterWorldMoves.RunAsync(link, Request(), Actor, default);
        Assert.Equal((409, "character_online"), (status, ErrorOf(body)));
        Assert.Single(link.Calls);
    }

    [Fact]
    public async Task A_refused_import_cancels_the_move_at_the_source()
    {
        var link = new FakeLink((_, step, _) => step switch { "export" => Package, "import" => Error(400, "name_unavailable"), _ => Ok(new { done = true }) });
        var (status, body) = await CharacterWorldMoves.RunAsync(link, Request(), Actor, default);
        Assert.Equal((400, "name_unavailable"), (status, ErrorOf(body)));
        Assert.Equal((Source, "finish"), (link.Calls[2].World, link.Calls[2].Step));
        Assert.Equal(JsonValueKind.Null, link.Calls[2].Body.GetProperty("movedTo").ValueKind);
    }

    [Fact]
    public async Task A_target_that_does_not_answer_leaves_the_move_incomplete()
    {
        // Neither cancelled nor finished: the import may have committed, so the same request id has to run again.
        var link = new FakeLink((_, step, _) => step == "export" ? Package : new WorldMoveStep(0, null));
        var request = Request();
        var (status, body) = await CharacterWorldMoves.RunAsync(link, request, Actor, default);
        Assert.Equal((502, "move_incomplete"), (status, ErrorOf(body)));
        Assert.Equal(request.RequestId, JsonSerializer.SerializeToElement(body).GetProperty("requestId").GetGuid());
        Assert.DoesNotContain(link.Calls, c => c.Step == "finish");
    }

    [Fact]
    public async Task A_finish_that_fails_leaves_the_move_incomplete()
    {
        var link = new FakeLink((_, step, _) => step switch { "export" => Package, "import" => Imported, _ => new WorldMoveStep(500, null) });
        var (status, body) = await CharacterWorldMoves.RunAsync(link, Request(), Actor, default);
        Assert.Equal((502, "move_incomplete"), (status, ErrorOf(body)));
    }

    [Fact]
    public async Task An_offline_source_is_reported_and_bad_requests_reach_no_world()
    {
        var offline = new FakeLink((_, _, _) => Error(503, "world_offline"));
        Assert.Equal((503, "world_offline"), await Run(offline, Request()));
        var unreachable = new FakeLink((_, _, _) => new WorldMoveStep(0, null));
        Assert.Equal((502, "world_unreachable"), await Run(unreachable, Request()));
        var none = new FakeLink((_, _, _) => throw new InvalidOperationException("no world should be called"));
        Assert.Equal((400, "same_world"), await Run(none, Request() with { TargetWorld = Source }));
        Assert.Equal((400, "invalid_move"), await Run(none, Request() with { TargetAccount = Guid.Empty }));

        static async Task<(int, string)> Run(FakeLink link, WorldMoveRequest request)
        {
            var (status, body) = await CharacterWorldMoves.RunAsync(link, request, Actor, default);
            return (status, ErrorOf(body));
        }
    }
}
