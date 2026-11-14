using Aion.Application.Gameplay;
using Aion.Protocol.Native;

namespace Aion.World;

// Empyrean Traces, ported from zaraza87/A2Core (GameSession.EmpyreanTraces.cs, #72); the Monoliths they feed are in
// GameSession.Monolith.cs.
//
// WHAT THEY ARE (client tables, via the fork's tools/data/monoliths.json): EnvObjData Usage Normal objects with bGetRewardOnce and a
// RewardData row reward_manual_Monolith_<env>: Verteron E_L1_Verteron_fragment_Ground/Air/Water (559 on 1010), Eltnen
// E_L2_Godfragment_* (140 on 1011), Altgard E_D1_GodFragment_01_* (559 on 1110), Morheim E_D2_GodFragment_* (140 on 1111),
// Lower / Middle Reshanta E_AR1/AR2_Fragment_Ground/Air (200 on 20, 100 on 22). They are appended to world-objects.json per map
// (tools/data/monolith-placements.json). Every placed trace has its own UseEnvObjSpawnerName achievement (Achievement.dat
// Group_Unlock_MonolithFragment_<region>_NN, one step of count 1): that achievement is the "collected" mark, for good and per
// character (stored with the other achievements).
//
// SPAWN, OBSERVED (fork, 86 trace spawns): 0x3634 with mask 0x0c and the spawn's achievement (GamePackets.TraceSpawn), state 1 /
// can interact 1 also when collected: the client greys a collected one out from the achievement.
//
// COLLECTING, OBSERVED (fork, 15 pickups in 13 recordings): the reply 0x3637 and 0x3638 started at once; InteractTime 2500 ms later
// 0x363a completed, 0x8d6d finish, 0x363b available (the trace stays for the others); the region count (UseEnvObj, e.g.
// 420100700 "Collect Empyrean Trace: Altgard", and the battle pass missions), 0xe33a the reward at the trace (Altgard / Verteron 3
// tokens, Eltnen / Morheim / Reshanta 1; 631900007 x3 OBSERVED), the items (0x561b reason 49), then the spawn's own achievement.
// A second use (the client does not offer it) is refused (CANDIDATE).
public sealed partial class GameSession
{
    /// <summary>An Empyrean Trace: a placed object with its spawn's achievement (world-objects.json "achievement").</summary>
    static bool IsTrace(WorldObject o) => o.Achievement != 0 && MonolithData.Traces.ContainsKey(o.Env);

    /// <summary>A trace this character has collected for good: its spawn achievement counted.</summary>
    bool TraceCollected(WorldObject o) => o.Achievement != 0 && (achievements.GetValueOrDefault(o.Achievement)?.Count ?? 0) > 0;

    /// <summary>A trace used: the reply and the start together (OBSERVED), the collection after its InteractTime.</summary>
    List<byte[]> StartTraceUse(int index, WorldObject o, int key, long now)
    {
        if (TraceCollected(o)) return [GamePackets.InteractEnvObjReply(key, 1), GamePackets.InteractEnvObjAvailableState(key)];
        pendingUse = (index, key, now + Math.Max(1, o.InteractTime));
        return [GamePackets.InteractEnvObjReply(key), GamePackets.InteractEnvObjStarted(key, playerKey)];
    }

    /// <summary>
    /// A trace collected (its 2.5 s use done), in the OBSERVED order: 0x363a, 0x8d6d, 0x363b, the region / battle pass counts
    /// (UseEnvObj), 0xe33a with the tokens, the items (0x561b reason 49), the spawn's own achievement (UseEnvObjSpawnerName).
    /// </summary>
    async Task<List<byte[]>> CollectTraceAsync(int index, WorldObject o, long now, CancellationToken ct)
    {
        var key = WorldObjects.FirstKey + index;
        var replies = new List<byte[]>
        {
            GamePackets.InteractEnvObjCompleted(key, playerKey), GamePackets.InteractEnvObjFinishState(key), GamePackets.InteractEnvObjAvailableState(key),
        };
        if (TraceCollected(o)) return replies;
        replies.AddRange(Achieve("UseEnvObj", 1, e => e.Envs?.Contains(o.Env) == true, now));
        var items = LootTables.Embedded.RollEnv(o, profileRace, rng);
        if (items.Count > 0 && Store is { } store && CharacterId is { } character)
        {
            var granted = await Stored(async () => await store.GrantLootAsync(character, items, ct) ? [[]] : []);
            if (granted.Count > 0)
            {
                replies.Add(GamePackets.EnvObjReward(key, o.At, items));
                replies.AddRange(await InventoryChangesAsync(ct, GamePackets.IncreaseEnvObjReward, 0));
            }
        }
        replies.AddRange(Achieve("UseEnvObjSpawnerName", 1, e => e.Id == o.Achievement || e.Has(o.Name), now));
        replies.AddRange(await CreditUseEnvObjQuestAsync(o.Env, ct));
        Note($"Empyrean Trace collected on map {worldMap}: {o.Name} (achievement {o.Achievement}) gave {string.Join(", ", items.Select(i => $"{i.ItemId} x{i.Count}"))}");
        return replies;
    }

    /// <summary>
    /// An express voucher (UseEffect MonolithAllClear [monolith, map]): every trace of that Monolith's groups on the map this character
    /// has not collected, at once: their spawn achievements, the region counts and their tokens (CANDIDATE: no recording). Refused
    /// when nothing is left ("Has no effect if all Empyrean Traces in the region have already been obtained").
    /// </summary>
    async Task<List<byte[]>?> UseMonolithVoucherAsync(INativeCharacterStore store, Guid character, ItemState row, ItemUse use, bool reply, long now, CancellationToken ct)
    {
        var (monolithId, map) = MonolithData.Vouchers.TryGetValue(row.TemplateId, out var v) ? v
            : (MonolithData.Monoliths.Values.FirstOrDefault(m => use.Values is [var name, ..] && m.Name == name)?.Id ?? 0,
               use.Values is [_, var mapText, ..] && int.TryParse(mapText, out var parsed) ? parsed : 0);
        if (MonolithData.Of(monolithId) is not { } monolith || monolith.Race != MonolithRace) return null;
        var groups = monolith.Groups.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var left = WorldObjects.ObjectsOn(map).Where(o => IsTrace(o) && !TraceCollected(o)
            && Achievements.Of(o.Achievement) is { } entry && groups.Contains(entry.Group)).ToList();
        if (left.Count == 0) return null;
        var rewards = left.SelectMany(o => LootTables.Embedded.RollEnv(o, profileRace, rng)).GroupBy(i => i.ItemId)
            .Select(g => (g.Key, g.Sum(i => i.Count))).ToList();
        var done = await Stored(async () => await store.ConsumeAndGrantAsync(character, row.InstanceId, row.Revision, 1, rewards, ct) ? [[]] : []);
        if (done.Count == 0) return null;
        var replies = Used(row, reply);
        replies.AddRange(await InventoryChangesAsync(ct, GamePackets.IncreaseEnvObjReward, DecreaseItemUse));
        foreach (var env in left.Select(o => o.Env).Distinct())
        {
            var n = left.Count(o => o.Env == env);
            replies.AddRange(Achieve("UseEnvObj", n, e => e.Envs?.Contains(env) == true, now));
        }
        var ids = left.Select(o => o.Achievement).ToHashSet();
        replies.AddRange(Achieve("UseEnvObjSpawnerName", 1, e => ids.Contains(e.Id), now));
        Note($"Monolith voucher {row.TemplateId}: {left.Count} traces of {monolith.Name} on map {map} collected");
        return replies;
    }
}
