using Aion.Application.Gameplay;
using Aion.Protocol.Native;

namespace Aion.World;

public sealed partial class GameSession
{
    /// <summary>Legacy name used by Native.Checks when <see cref="GameSessionOptions.AutoResurrectMsOverride"/> is unset.</summary>
    public const int ReviveDelayMs = 60_000;

    /// <summary>EAppearReasonType on respawn (OBSERVED with 0x3647 / 0x3621 after CharRespawn_RQ).</summary>
    const byte RespawnAppearReason = 4;

    /// <summary>The revive's abnormal at every revive: after the respawn's load request, or in place (OBSERVED 3 s).</summary>
    const int RespawnProtectAbnormal = 10003, RespawnProtectMs = 3000;

    /// <summary>Invulnerability right after the own appearance on respawn load (OBSERVED 5 s).</summary>
    const int RespawnLoadProtectAbnormal = 10002, RespawnLoadProtectMs = 5000;

    bool dead;
    long reviveAt;
    long defaultResurrectAllowedAt;
    long reloadStartedMs;
    int lastKillerKey;
    int lastNpcHitSkill;
    bool pendingRespawnLoadProtect;
    Vector3d? diedAt;
    public bool Dead => dead;
    public ConnectionPacketTrace PacketTrace { get; } = new();

    int ResolveAutoResurrectMs() => options.AutoResurrectMsOverride ?? data.AutoResurrectMs(level);
    public int ResurrectAutoDelayMs => ResolveAutoResurrectMs();

    int NpcDataIdForKey(int entityKey)
    {
        if (targets?.TryGetValue(entityKey, out var t) == true) return t.NpcId;
        return 0;
    }

    /// <summary>The player died: 0x8d04 (DiePvE), combat off, rebirth buff kept; auto-rez from Resurrect.dat.</summary>
    List<byte[]> Die(int killerKey, long now)
    {
        dead = true;
        LogDeath(killerKey);
        diving = false;
        lastKillerKey = killerKey;
        List<byte[]> deathLimit = [];
        if (copy is { } dungeonCopy) { dungeonCopy.Deaths++; deathLimit = NotifyDungeonDeathCount(); }
        reviveAt = now + ResolveAutoResurrectMs();
        defaultResurrectAllowedAt = now + data.DefaultResurrectWaitMs(level);
        diedAt = position;
        inCombat = false;
        var npcDataId = NpcDataIdForKey(killerKey);
        var skill = lastNpcHitSkill;
        lastNpcHitSkill = 0;
        var kill = GamePackets.Kill(playerKey, skill, killerKey, 0, "", dieType: 2, npcCasterDataId: npcDataId,
            npcDieActId: 0, reason: 1);
        worldPlayer?.Map?.Broadcast(worldPlayer, [kill]);
        return [.. CancelPendingSkillCharge(sendStopReply: true), .. deathLimit, .. ForceDismount(), .. ClearBuffs(), kill, GamePackets.CombatState(playerKey, false)];
    }

    /// <summary>0x8d22 CharRespawn_RQ {ERespawnType u8, item uid i64}.</summary>
    async Task<List<byte[]>> RespawnRequestAsync(byte type, long itemUid, long now, CancellationToken ct)
    {
        if (!dead) return [RidePackets.RespawnReply(1)];
        if (now < defaultResurrectAllowedAt && type != RidePackets.RespawnItem)
            return [RidePackets.RespawnReply(1)];
        if (type == RidePackets.RespawnItem)
        {
            if (!TryResolveRebirthItem(itemUid, out var row)) return [RidePackets.RespawnReply(1)];
            if (DungeonRevivesLeft() == 0) return [RidePackets.RespawnReply(1)];
            if (Store is not { } store || CharacterId is not { } character)
                return [RidePackets.RespawnReply(1)];
            var used = await Stored(async () => await store.ConsumeItemAsync(character, row.InstanceId, row.Revision, 1, ct) ? [[]] : []);
            if (used.Count == 0) return [RidePackets.RespawnReply(1)];
            // OBSERVED by the community fork (090856 #96729-#96741, three times): 0x8d23, abnormal 10003, 0x8d26 (self and
            // nearby), 0x8d00, then the item change. Not sent: the system skill 8047 (0x3802/0x3806/0x3804) before the item.
            var revived = Revive(now, inPlace: true);
            return [RidePackets.RespawnReply(0), .. revived, .. await InventoryChangesAsync(ct, 0, DecreaseUseRebirthItem)];
        }
        return [RidePackets.RespawnReply(0), .. Revive(now)];
    }

    bool TryResolveRebirthItem(long itemUid, out ItemState row)
    {
        row = default!;
        if (storedItems.TryGetValue(itemUid, out var found) && found.Location == "bag" && IsRebirthItem(found.TemplateId))
        {
            row = found;
            return true;
        }
        return false;
    }

    List<byte[]> UseRebirth(bool use, long now)
    {
        if (!use) return [RidePackets.UseRebirthReply(0, false)];
        if (!dead || !CanRebirth || DungeonRevivesLeft() == 0) return [RidePackets.UseRebirthReply(1, false)];
        // CANDIDATE: as the item revive (the rebirth buff's path is unrecorded); Revive sends 0x8d26 in place.
        return [RidePackets.UseRebirthReply(0, true), .. UseRebirthBuff(), .. Revive(now, inPlace: true)];
    }

    const byte DecreaseUseRebirthItem = 85;

    /// <summary>The key of this character's revive count in a run: the character id, or the player key for a development login.</summary>
    string ReviveKey => CharacterId?.ToString() ?? playerKey.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// Revives this character may still take in the dungeon run (<see cref="Patch20261028.RevivesPerPlayer"/>: Dungeon.dat
    /// RebirthLimitCount, 3 for the party _Hard dungeons, 2 for the raids); -1 without a limit. CANDIDATE: every revive counts
    /// (respawn, item, rebirth buff, the automatic one), per character and run, so a reconnect does not refill it. Staff
    /// revives do not count (OURS).
    /// </summary>
    int DungeonRevivesLeft()
    {
        if (copy is not { } c || MapEventTables.Embedded.Dungeon(c.DungeonId) is not { } room) return -1;
        var limit = Patch20261028.RevivesPerPlayer(room);
        return limit > 0 ? Math.Max(0, limit - c.Revives.GetValueOrDefault(ReviveKey)) : -1;
    }

    Vector3d RespawnPoint()
    {
        if (DungeonRevivalPoint() is { } dungeon) return dungeon;
        if (data.MapRespawn(worldMap, profileRace) is { } map) return map;
        return data.MapStart(worldMap)?.Position ?? position ?? spawn?.Position ?? GamePackets.SpawnPosition;
    }

    List<byte[]> Revive(long now, bool inPlace = false, bool counted = true)
    {
        if (counted && DungeonRevivesLeft() is var left && left >= 0 && copy is { } run)
        {
            if (left == 0) { inPlace = false; instanceExitAt = now; }
            else run.Revives[ReviveKey] = run.Revives.GetValueOrDefault(ReviveKey) + 1;
        }
        dead = false;
        LogResurrect(inPlace ? "in_place" : "respawn");
        defaultResurrectAllowedAt = 0;
        reviveAt = 0;
        if (resources is null) return [];
        (resources.Hp, resources.Mp) = (resources.HpMax, resources.MpMax);
        // In place: no map load, abnormal 10003, 0x8d26 to self and nearby, every bar in one 0x8d00 (OBSERVED by the community
        // fork, 090856 #96729-#96741). Ours used to reload the map where the player fell.
        if (inPlace && diedAt is { } fell)
        {
            position = fell;
            var revival = new List<byte[]>();
            AddBuff(RespawnProtectAbnormal, RespawnProtectMs, 0, now, revival, path: SelfAbnormalPath.DeathProtection);
            var reborn = RidePackets.Reborn(playerKey);
            worldPlayer?.Map?.Broadcast(worldPlayer, [reborn]);
            return [.. revival, reborn, resources.FullUpdate()];
        }
        var at = RespawnPoint();
        var replies = new List<byte[]> { PlayerPackets.CharDisappear(playerKey, RespawnAppearReason) };
        replies.AddRange(Teleport(worldMap, at, now, RespawnAppearReason));
        AddBuff(RespawnProtectAbnormal, RespawnProtectMs, 0, now, replies, path: SelfAbnormalPath.DeathProtection);
        pendingRespawnLoadProtect = true;
        replies.Add(resources.Update());
        return replies;
    }

    /// <summary>Timer-driven auto-rez at the respawn point (same replies as an accepted CharRespawn_RQ).</summary>
    List<byte[]> AutoRevive(long now) => [RidePackets.RespawnReply(0), .. Revive(now)];

    List<byte[]> EnsureAliveAfterLoad()
    {
        if (resources is null || dead || resources.Hp > 0) return [];
        resources.Hp = resources.HpMax;
        resources.Mp = resources.MpMax;
        return [resources.Update()];
    }

    List<byte[]> RespawnLoadProtectAfterAppear(long now)
    {
        if (!pendingRespawnLoadProtect) return [];
        pendingRespawnLoadProtect = false;
        var replies = new List<byte[]>();
        AddBuff(RespawnLoadProtectAbnormal, RespawnLoadProtectMs, 0, now, replies, path: SelfAbnormalPath.DeathProtection);
        return replies;
    }

    void NoteReloadStarted(long now) => reloadStartedMs = now;

    void WarnStuckReload(long now)
    {
        if (!reloading || reloadStartedMs == 0 || now < reloadStartedMs + 45_000) return;
        MapEventLog?.Invoke($"Death/reload: still waiting for 0x3622 after {(now - reloadStartedMs) / 1000}s (dead={dead}, map={worldMap})");
        reloadStartedMs = now;
    }
}
