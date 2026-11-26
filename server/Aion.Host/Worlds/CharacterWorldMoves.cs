using System.Text.Json;

namespace Aion.Host.Worlds;

/// <summary>A staff move of a character to another world (POST /api/admin/world-moves on the auth host).</summary>
public sealed record WorldMoveRequest(Guid RequestId, Guid CharacterId, int SourceWorld, int TargetWorld, Guid TargetAccount,
    string? NewName = null, string? Reason = null);

/// <summary>One world's answer to a move step: its HTTP status (0: not reached) and JSON body.</summary>
public readonly record struct WorldMoveStep(int Status, JsonElement? Body)
{
    public bool Ok => Status is >= 200 and < 300;
    /// <summary>The world refused the step for a reason of its own (a 4xx with an error code), as opposed to not answering.</summary>
    public bool Refused => Status is >= 400 and < 500 && Body is { ValueKind: JsonValueKind.Object } body && body.TryGetProperty("error", out _);
}

/// <summary>The worlds' move steps (/internal/world/characters/{export,import,finish}), over WorldLinks.</summary>
public interface IWorldMoveLink
{
    Task<WorldMoveStep> MoveStepAsync(int worldId, string step, object body, CancellationToken ct);
}

/// <summary>
/// Runs a move across two world servers (docs/STAFF_CHARACTERS.md): export from the source (the character is blocked there),
/// import into the target (a new character with new ids), finish on the source (the original is deleted). A refused import
/// cancels the move (the source unblocks the character). A world that stops answering part-way leaves the move incomplete:
/// the character stays blocked at the source and the same request id finishes it later (every step is repeatable).
/// </summary>
public static class CharacterWorldMoves
{
    public static async Task<(int Status, object Body)> RunAsync(IWorldMoveLink link, WorldMoveRequest input, Guid actor, CancellationToken ct)
    {
        if (input.RequestId == Guid.Empty || input.CharacterId == Guid.Empty || input.TargetAccount == Guid.Empty
            || input.SourceWorld <= 0 || input.TargetWorld <= 0)
            return (400, new { error = "invalid_move" });
        if (input.SourceWorld == input.TargetWorld) return (400, new { error = "same_world" });

        var export = await link.MoveStepAsync(input.SourceWorld, "export",
            new { input.RequestId, Actor = actor, input.CharacterId, input.TargetWorld, input.Reason }, ct);
        if (!export.Ok) return Failed(export, input.SourceWorld);

        var import = await link.MoveStepAsync(input.TargetWorld, "import",
            new { input.RequestId, Actor = actor, input.TargetAccount, input.NewName, input.Reason, Package = export.Body }, ct);
        if (import.Refused)
        {
            var cancel = await link.MoveStepAsync(input.SourceWorld, "finish",
                new { input.RequestId, Actor = actor, input.CharacterId, MovedTo = (Guid?)null }, ct);
            return cancel.Ok ? (import.Status, import.Body!.Value) : Incomplete(input, "cancel");
        }
        if (!import.Ok || import.Body?.TryGetProperty("characterId", out var moved) != true) return Incomplete(input, "import");

        var finish = await link.MoveStepAsync(input.SourceWorld, "finish",
            new { input.RequestId, Actor = actor, input.CharacterId, MovedTo = moved.GetGuid() }, ct);
        return finish.Ok ? (200, import.Body!.Value) : Incomplete(input, "finish");
    }

    static (int, object) Failed(WorldMoveStep step, int world) =>
        step.Refused || step.Status == 503 && step.Body is not null ? (step.Status, step.Body!.Value) : (502, new { error = "world_unreachable", world });

    // The character stays blocked at the source until the same request id runs again.
    static (int, object) Incomplete(WorldMoveRequest input, string step) =>
        (502, new { error = "move_incomplete", step, requestId = input.RequestId });
}
