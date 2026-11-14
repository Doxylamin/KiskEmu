using Aion.Protocol.Native;

namespace Aion.World;

// Daily dungeons (Dungeon.dat type Daily, DailyDungeons.cs): a countdown, points per kill and a reward by points when the
// countdown ends. OBSERVED in the official capture of 2026-11-12 (900001, a level-45 character, solo):
//   entry      0x6100 state 1, 0x6104 `dungeon 00` (points 0), buff choice (0x6105-0x6107, not modelled here)
//   Process    0x6100 state 2 {start, end 0}, 0x6014 {WaveData id 85, end = start + 90 s, 00}, the map events' 0x922e
//   each kill  0x922d, 0x6104 {dungeon i32, total points varint}: +40/+50 per kill as DungeonPoint.dat says
//   at the end 0x6102 WaveDungeonCompleteResult_NT `03 dungeon 01 [90 i64] [1730 varint] {930100023: 4300}`, 0x6104 again,
//              0x6100 state 3 {now, now + 30 s (CompleteStateHoldingTime)}, the living monsters' 0x3642 (act 7), the wave
//              event's 0x922d status 3, then 0x561b with the reward (EIncreaseReason 58 kDungeonReward)
//   + 30 s     0x6100 state 4 for 600 s (DungeonEndTick); the player left by hand later.
// Ours starts the countdown at the world entry (official: after the buff choice, about a minute later, CANDIDATE #77).

public sealed partial class GameSession
{
    /// <summary>OBSERVED live opcodes (official 2026-11-12): the countdown (layout of usmap GroupWaveSpawnStart_NT {wave id i32,
    /// expire time i64, is_enter_map bool}), WaveDungeonCompleteResult_NT and DungeonPoint_NT (0x5ec3 + index).</summary>
    public const ushort WaveCountdownNt = 0x6014, WaveDungeonCompleteResultNt = 0x6102, DungeonPointNt = 0x6104;
    /// <summary>EIncreaseReason kDungeonReward (usmap; OBSERVED 58 on the official reward).</summary>
    const byte IncreaseDungeonReward = 58;
    /// <summary>The result's grade: 1 in the one official result (1730 points, the 1500 tier). CANDIDATE #77: what sets it.</summary>
    public const byte WaveDungeonGrade = 1;

    /// <summary>0x6014 {wave id i32, end i64, is_enter_map u8}: OBSERVED `55000000 [end] 00` when the Process state began.
    /// CANDIDATE: 1 when it is re-sent at a later map entry.</summary>
    internal static byte[] WaveCountdown(int wave, long endMs, bool enterMap) =>
        PacketWriter.Opcode(WaveCountdownNt).I32(wave).I64(endMs).U8((byte)(enterMap ? 1 : 0)).ToArray();

    /// <summary>0x6104 DungeonPoint_NT {dungeon i32, point varint}: OBSERVED `a1bb0d00 00` at entry, `… c20d` for 1730.</summary>
    internal static byte[] DungeonPoint(int dungeonId, int points) =>
        PacketWriter.Opcode(DungeonPointNt).I32(dungeonId).Varint((uint)Math.Max(0, points)).ToArray();

    /// <summary>
    /// 0x6102 WaveDungeonCompleteResult_NT (usmap {dungeon, grade u8, duration i64, total_point, bonus_point, rewards
    /// map&lt;i32, i64&gt;, sweep_count}): OBSERVED `03 a1bb0d00 01 5a00000000000000 c20d 01 37337037 cc10000000000000`
    /// (mask 03, no bonus or sweep count on a played run). CANDIDATE: the mask bits' meaning.
    /// </summary>
    internal static byte[] WaveDungeonCompleteResult(int dungeonId, byte grade, long durationSec, int points, IReadOnlyList<(int ItemId, long Count)> rewards)
    {
        var w = PacketWriter.Opcode(WaveDungeonCompleteResultNt).U8(0x03).I32(dungeonId).U8(grade).I64(durationSec)
            .Varint((uint)Math.Max(0, points)).Varint((uint)rewards.Count);
        foreach (var (item, count) in rewards) w.I32(item).I64(count);
        return w.ToArray();
    }

    readonly List<List<(int ItemId, long Count)>> pendingDungeonRewards = [];

    /// <summary>The daily dungeon of this copy, if it is one.</summary>
    DailyDungeon? CopyDaily => copy is { } c ? DailyDungeon.Of(c.DungeonId) : null;

    /// <summary>
    /// At the world entry of a daily dungeon with a countdown: Process since the countdown began (end 0, as official), the
    /// usual limits, the points so far and the countdown (begun at the first entry). After the end: its cleared state.
    /// </summary>
    List<byte[]>? DailyStateAtEntry(DungeonCopy c, long now)
    {
        if (DailyDungeon.Of(c.DungeonId) is not { Seconds: > 0 } daily) return null;
        if (c.Cleared) return [DungeonClearedState(c), DungeonPoint(c.DungeonId, c.Points)];
        var again = c.WaveEndsMs != 0;
        if (!again) { c.WaveStartedMs = now; c.WaveEndsMs = now + daily.Seconds * 1000L; }
        return [DungeonState(c.DungeonId, DungeonStateProcess, c.WaveStartedMs, 0), .. DungeonLimitsAtEntry(c.Deaths),
            DungeonPoint(c.DungeonId, c.Points), WaveCountdown(daily.Wave, c.WaveEndsMs, again)];
    }

    /// <summary>A kill in a daily dungeon (the host's): DungeonPoint.dat's points, 0x6104 with the total to the copy.</summary>
    void DailyKillPoints(Target target, long now)
    {
        if (copy is not { Cleared: false, Failed: false } c || DailyDungeon.Of(c.DungeonId) is not { } daily) return;
        if (c.WaveEndsMs != 0 && now >= c.WaveEndsMs) return;
        var points = daily.KillPoint(target.NpcId, target.SubType);
        if (points <= 0) return;
        c.Points += points;
        SendToCopy([DungeonPoint(c.DungeonId, c.Points)]);
    }

    /// <summary>The host's tick: the countdown ended, the run is over with the reward of its points.</summary>
    void DailyCountdownTick(long now)
    {
        if (copy is not { Cleared: false, Failed: false, WaveEndsMs: > 0 } c || c.Host != this || now < c.WaveEndsMs) return;
        if (DailyDungeon.Of(c.DungeonId) is not { } daily || MapEventData.Dungeon(c.DungeonId) is not { } room) return;
        c.Cleared = true;
        c.ClearedMs = now;
        c.CompleteEndsMs = now + room.Hold * 1000L;
        if (c.CompleteEndsMs == c.ClearedMs) c.EndSent = true;
        var seconds = (c.WaveEndsMs - c.WaveStartedMs) / 1000;
        Note($"dungeon {c.DungeonId}: the countdown ended with {c.Points} points");
        foreach (var session in DungeonCopySessions())
        {
            var reward = daily.Reward(c.Points, session.profileRace == 2);
            var packets = new List<byte[]>
            {
                WaveDungeonCompleteResult(c.DungeonId, WaveDungeonGrade, seconds, c.Points, reward),
                DungeonPoint(c.DungeonId, c.Points),
                DungeonState(c.DungeonId, DungeonStateComplete, c.ClearedMs, c.CompleteEndsMs),
            };
            if (c.EndSent) packets.Add(DungeonClearedState(c));
            if (reward.Count > 0) session.pendingDungeonRewards.Add(reward);
            session.SpendContentTickets(c.DungeonId, "Clear", now);
            // Official: the player stays through Complete and End and leaves by hand. Ours sends them out when End runs out (OURS).
            session.instanceExitAt ??= c.CompleteEndsMs + DungeonEndStateMs;
            if (session == this) mapEventOut.AddRange(packets);
            else session.worldPlayer?.Send(packets);
        }
        // The waves stop and the monsters still standing go (OBSERVED: 0x3642 act 7 for the living, 3 for corpses; 0x922d status 3).
        foreach (var run in mapEventRuns) run.Stop();
        pendingSpawns.RemoveAll(p => p.Spawn);   // a next order still on its 200 ms delay
        DespawnHostiles();
    }

    /// <summary>Every hostile NPC still on the map goes: living ones out of view (act 7), corpses removed (act 3).</summary>
    void DespawnHostiles()
    {
        foreach (var t in targets?.Values.Where(t => t.Hostile && !t.CorpseRemoved && t.Key != Combat.TrainingDummyKey).ToList() ?? [])
        {
            var wasDead = t.Dead;
            t.Hp = 0;
            t.CorpseRemoved = true;
            respawnAt.Remove(t.Key);
            endedSpawners.Add(t.Name);
            if (!wasDead) mapEventOut.AddRange(ai.Killed(t));
            mapEventOut.Add(GamePackets.Death(t.Key, wasDead ? GamePackets.DespawnCorpse : GamePackets.DespawnOutOfView));
            seenNpcs.Remove(t.Key);
        }
    }

    void SendToCopy(List<byte[]> packets)
    {
        foreach (var session in DungeonCopySessions())
            if (session == this) mapEventOut.AddRange(packets);
            else session.worldPlayer?.Send(packets);
    }

    /// <summary>Dungeon.dat ResurrectionType Restart (the daily dungeons): the respawn leaves the dungeon; the item's revive
    /// (the resurrection stone) stays in place. From the user's report (2026-11-14); the wire is not recorded (CANDIDATE #77).</summary>
    bool RespawnLeavesDungeon => instanceKey != 0 && CopyDaily is { RespawnLeaves: true };
}
