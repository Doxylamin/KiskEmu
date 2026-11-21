using Aion.Application.Gameplay;
using Aion.Protocol.Native;

namespace Aion.World;

// Power Shards (the SealStone consumable, Item.dat UseEffect SealStone -> a SealStone.dat Name): a bag stack registered in
// the HUD auto slot left of B (RegisterSealStoneAutoUse 0x8d4c) while the toggle is on (ActivateSealStone 0x8d47) is spent
// by every damaging cast, Skill.dat / SkillLv.dat SealStoneConsumptionCount per cast, and the cast deals the stone's
// SealStone.dat StatList bonus (EStat SealStoneAddDamage 69, SealStoneAmplifyDamage 534, SealStonePvPIgnoreDefense 559;
// tools/data/sealstones.json, SealStoneTables). Without enough shards the cast is a plain one.
// OBSERVED (community recording 2026-11-06 234912, via the addiboii fork 2b93c15): the spent shards go out as the stack's
// 0x561b change with decrease reason 22 kItemModifiedInCombat; the registered stone's stats are on the stat sheet.
// INFERRED: the per-skill counts (absent from the export) and when the count is taken (once per cast that hits something,
// as ESealStoneECType ATTACK / HIT suggest). OPEN (capture #75 item 11): per cast or per landed hit. The fork spends one
// shard per damaging hit (its own CANDIDATE); we keep per cast until a capture settles it.
// CANDIDATE: the damage model (x(1 + AmplifyDamage / 10000), then + AddDamage, on hits with damage while on).
public sealed partial class GameSession
{
    /// <summary>Power Shard (Bound) 533460001 and the other two Power Shard stacks (034_item_stacks.sql: 100,000 each).</summary>
    public static readonly int[] PowerShardItems = [533460001, 533460004, 533460006];

    /// <summary>INFERRED SealStoneConsumptionCount when the skill export has none: a basic attack 1, any other damaging skill 2.</summary>
    public const int SealStoneBasicCount = 1, SealStoneSkillCount = 2;

    /// <summary>OBSERVED decrease reason of a spent shard (0x561b): kItemModifiedInCombat (2026-11-06 234912, 224 times).</summary>
    const byte DecreaseInCombat = 22;

    static SealStoneTables SealStones => SealStoneTables.Embedded;

    int sealStonePending;
    long sealStonePendingUid;
    int sealStonePendingItem;

    /// <summary>Shards the cast takes (0 for a cast that deals no damage).</summary>
    public int SealStoneConsumption(int skill) => data.Skill(skill) switch
    {
        null or { Damage.Length: 0 } => 0,
        { SealStone: { } count } => Math.Max(0, count),
        var d when SkillCooldown.IsBasicAutoAttack(d) => SealStoneBasicCount,
        _ => SealStoneSkillCount,
    };

    /// <summary>The slotted stack; once it is used up, the next seal stone stack in the bag (the slot refills itself).</summary>
    ItemState? SealStoneRow()
    {
        if (sealStoneItem == 0) return null;
        if (storedItems.TryGetValue(sealStoneItem, out var row) && row.Location == "bag" && SealStones.IsStone(row.TemplateId)) return row;
        return storedItems.Values.Where(i => i.Location == "bag" && i.Quantity > 0 && SealStones.IsStone(i.TemplateId))
            .OrderByDescending(i => i.Quantity).FirstOrDefault();
    }

    /// <summary>The registered stack while it is in the bag (no refill: what the stat sheet counts).</summary>
    ItemState? SealStoneRegistered => sealStoneItem != 0 && storedItems.TryGetValue(sealStoneItem, out var row) && row.Location == "bag"
        && row.Quantity > 0 && SealStones.IsStone(row.TemplateId) ? row : null;

    /// <summary>
    /// A stat (EStat name) the registered stone puts on the stat sheet. CANDIDATE (fork): it counts while registered, on or
    /// off; the recording sent it on registering, before the toggle.
    /// </summary>
    int SealStoneStat(string stat) => SealStoneRegistered is { } row ? SealStones.Stat(row.TemplateId, stat) : 0;

    /// <summary>The seal stone EStats on the stat sheet: (wire, name). OBSERVED 69 and 534 in 0x364a; 559 from SealStone.dat.</summary>
    static readonly (int Wire, string Name)[] SealStoneSheetStats =
        [(69, "SealStoneAddDamage"), (534, "SealStoneAmplifyDamage"), (559, "SealStonePvPIgnoreDefense")];

    /// <summary>Takes this cast's shards (stored by <see cref="SealStoneConsumedAsync"/> after the cast) when enough are left.</summary>
    bool TryUseSealStone(int skill)
    {
        var need = SealStoneConsumption(skill);
        if (!sealStoneOn || equippedWeapon is null || need <= 0) return false;
        if (SealStoneRow() is not { } row) return false;
        var uid = WireKey(row);
        if (sealStonePending > 0 && uid != sealStonePendingUid) return false;
        if (row.Quantity - sealStonePending < need) return false;
        sealStonePendingUid = uid;
        sealStonePendingItem = row.TemplateId;
        sealStonePending += need;
        return true;
    }

    /// <summary>The stone's bonus on the hits with damage (SealStone.dat values; CANDIDATE order: amplify, then add).</summary>
    IReadOnlyList<Hit> SealStoneBoost(IReadOnlyList<Hit> hits)
    {
        var add = SealStones.Stat(sealStonePendingItem, "SealStoneAddDamage");
        var amplify = SealStones.Stat(sealStonePendingItem, "SealStoneAmplifyDamage");
        return hits.Select(h => h.Amount > 0 ? h with { Amount = (int)Math.Min(int.MaxValue, (long)h.Amount * (10_000 + amplify) / 10_000 + add) } : h)
            .ToList();
    }

    int SealStonePvpDefense(int defense) =>
        (int)((long)defense * (10_000 - SealStones.Stat(sealStonePendingItem, "SealStonePvPIgnoreDefense")) / 10_000);

    /// <summary>
    /// Stores the shards the last casts took: the stack's 0x561b (decrease reason 22). A used-up slot moves to the next seal
    /// stone stack, or empties (BroadcastSealStoneChange_NT item 0) when there is none; the stat sheet follows.
    /// </summary>
    async Task<List<byte[]>> SealStoneConsumedAsync(CancellationToken ct)
    {
        if (sealStonePending == 0) return [];
        var (uid, count) = (sealStonePendingUid, sealStonePending);
        sealStonePending = 0;
        if (Store is not { } store || CharacterId is not { } character || !storedItems.TryGetValue(uid, out var row)) return [];
        var used = await Stored(async () => await store.ConsumeItemAsync(character, row.InstanceId, row.Revision, count, ct) ? [[]] : []);
        if (used.Count == 0) return [];
        var replies = await InventoryChangesAsync(ct, 0, DecreaseInCombat);
        if (uid != sealStoneItem) replies.AddRange(await SealStoneSlotAsync(uid, row.TemplateId, ct));
        else if (!storedItems.ContainsKey(uid) && SealStoneRow() is { } next) replies.AddRange(await SealStoneSlotAsync(WireKey(next), next.TemplateId, ct));
        else if (!storedItems.ContainsKey(uid)) replies.AddRange(SealStoneSlotShown(0));
        else return replies;
        replies.AddRange(RefreshStats());   // the slot changed: its stats follow
        return replies;
    }

    async Task<List<byte[]>> SealStoneSlotAsync(long uid, int itemId, CancellationToken ct)
    {
        sealStoneItem = uid;
        await SaveSettingAsync("seal_stone", new SealStoneSetting(sealStoneOn, uid), ct);
        return SealStoneSlotShown(itemId);
    }

    /// <summary>
    /// After every map load (world entry and each teleport), to the player only: BroadcastSealStoneChange_NT with the slotted
    /// stack, then SealStoneStatusChange_NT with the toggle. The own appearance carries only the toggle flag, so without these
    /// the HUD slot came back empty after a teleport (community fork ChanAge 9962b83, from a player report on 3533).
    /// CANDIDATE: no recording has a map load with a slotted stack, so the official order is unknown. Nothing is sent when no
    /// stack is slotted, since item 0 would clear the slot.
    /// </summary>
    internal List<byte[]> SealStoneAtLoad()
    {
        if (SealStoneRow() is not { } row) return [];
        return [PlayerPackets.SealstoneChange(playerKey, row.TemplateId),
            PacketWriter.Opcode(SealStoneStatusNt).Varint(playerKey).Bool(sealStoneOn).ToArray()];
    }

    List<byte[]> SealStoneSlotShown(int itemId)
    {
        var change = PlayerPackets.SealstoneChange(playerKey, itemId);
        worldPlayer?.Map?.Broadcast(worldPlayer, [change]);
        return [change];
    }
}
