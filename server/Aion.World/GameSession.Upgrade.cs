using System.Text.Json;
using System.Text.Json.Serialization;
using Aion.Application.Gameplay;
using Aion.Protocol.Native;

namespace Aion.World;

/// <summary>Mana stone imprinting and soul binding from tools/data/item_enhance.json (tools/enhance_data.py).</summary>
public sealed class ItemEnhanceTables
{
    sealed record EnhanceFile(
        [property: JsonPropertyName("items")] Dictionary<string, JsonElement[]> Items,
        [property: JsonPropertyName("magicstones")] Dictionary<string, string[]> MagicStones,
        [property: JsonPropertyName("imprint_groups")] Dictionary<string, JsonElement[][]> ImprintGroups,
        [property: JsonPropertyName("soulbind_costs")] Dictionary<string, long[][]> SoulBindCosts,
        [property: JsonPropertyName("soulbind_reroll")] Dictionary<string, long[]> SoulBindReroll);

    /// <summary>Equipment: mana stone slots, the imprint Kina, the accepted mana stone groups and the soul bind cost row.</summary>
    public sealed record Equip(int Slots, long ImprintKina, IReadOnlyList<string> StoneGroups, string SoulBindCost);
    /// <summary>One stat a mana stone can roll: EStat wire id, weight (/10000) and the grades {grade, prob /10000, value}.</summary>
    public sealed record ImprintStat(int Stat, int Weight, IReadOnlyList<(int Grade, int Prob, int Value)> Grades);
    public sealed record Cost(long Kina, int ItemId, int Count);

    readonly EnhanceFile file;
    ItemEnhanceTables(EnhanceFile file) => this.file = file;

    static readonly Lazy<ItemEnhanceTables> embedded = new(() =>
    {
        using var stream = typeof(ItemEnhanceTables).Assembly.GetManifestResourceStream("item_enhance.json")
            ?? throw new InvalidOperationException("Missing embedded game data item_enhance.json");
        return new ItemEnhanceTables(JsonSerializer.Deserialize<EnhanceFile>(stream) ?? throw new InvalidOperationException("Empty item_enhance.json"));
    });

    public static ItemEnhanceTables Embedded => embedded.Value;
    static string Key(int id) => id.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public Equip? Item(int itemId) => file.Items.TryGetValue(Key(itemId), out var r)
        ? new Equip(r[0].GetInt32(), r[1].GetInt64(), r[2].EnumerateArray().Select(g => g.GetString()!).ToList(), r[3].GetString()!) : null;

    /// <summary>A mana stone: its group (must be one of the item's) and its imprint group.</summary>
    public (string Group, string Imprint)? MagicStone(int itemId) =>
        file.MagicStones.TryGetValue(Key(itemId), out var r) ? (r[0], r[1]) : null;

    public IReadOnlyList<ImprintStat> Imprint(string group) => file.ImprintGroups.TryGetValue(group, out var rows)
        ? rows.Select(r => new ImprintStat(r[0].GetInt32(), r[1].GetInt32(),
            r[2].EnumerateArray().Select(g => (g[0].GetInt32(), g[1].GetInt32(), g[2].GetInt32())).ToList())).ToList()
        : [];

    /// <summary>What a stone at a stat and grade adds (0 when the stone cannot roll it).</summary>
    public int StoneValue(int stoneId, int stat, int grade) => MagicStone(stoneId) is { } stone
        ? Imprint(stone.Imprint).FirstOrDefault(s => s.Stat == stat)?.Grades.FirstOrDefault(g => g.Grade == grade).Value ?? 0
        : 0;

    /// <summary>The step raising the bind count from <paramref name="count"/> to count + 1; null at the maximum (CANDIDATE indexing).</summary>
    public Cost? SoulBindStep(int itemId, int count) =>
        Item(itemId) is { } equip && file.SoulBindCosts.TryGetValue(equip.SoulBindCost, out var steps) && count >= 0 && count < steps.Length
            ? new Cost(steps[count][0], (int)steps[count][1], (int)steps[count][2]) : null;
}

// Mana stones (Manastone tab) and soul binding (Soul Binding tab). Live opcodes in the 0x56 run, usmap layouts:
// ItemImprint_RQ 0x5629 {_equip_uid i64, _magic_stone_id i32, _magic_stone_uid i64} -> RS 0x562a (OBSERVED 2026-11-12:
// result 0, then every slot rewritten from the stone's ImprintMagicStoneProb group; 0x561b dec 96 for the stone and the
// Kina, upd 2 for the item). Item.dat MagicStoneSlotCount / ImprintMagicStoneCostGold / MagicStoneGroups. Stored as
// upgrades.magicstones = [{id, stat (EStat wire), grade, value}] per slot; imported official items keep
// upgrades.official_magicstones [{id, stat, grade}] and are read the same way (values from the stone's group).
// ItemSoulBindStart_RQ 0x562b {_uid, _cost_item_uid, _cost_item_id} checks the step's cost (SoulBindCost.dat by the
// item's SoulbindCostName: Kina + Soul Codex 535400001), ItemSoulBindComplete_RQ 0x562f {_uid} pays it and raises
// upgrades.soul_bind. A start without a cost item on an unbound item is the free bind on equip (as before).
// No official soul bind capture yet: the success result 0 (no kItem_SoulBind_Success in EResult; failures 849..860)
// and the cost row index (step n = count n -> n + 1) are CANDIDATE. The bound sub-stat roll is not in the exported
// tables (official bound items show DEX/CON/WIS/HPRegen lines that are not in Item.dat SubStats): stats are kept as
// they are and echoed in 0x5630 (CANDIDATE gap).
public sealed partial class GameSession
{
    public const ushort ItemImprintRq = 0x5629, ItemImprintRs = 0x562a;
    public const ushort SoulBindStartRq = 0x562b, SoulBindCompleteRq = 0x562f;
    const byte DecreaseItemSoulBind = 63, DecreaseItemImprintMagicStone = 96;   // EDecreaseReason
    const byte UpdateMagicStone = 2, UpdateSoulBind = 3;   // EUpdateReason
    /// <summary>EResult kItem_Imprint_*: 843 Fail, 845 Fail_Slot, 846 Internal_Error, 847 NotImprintable, 848 InvalidMagicStoneItem.</summary>
    public const int ImprintFail = 843, ImprintInternalError = 846, ImprintNotImprintable = 847, ImprintInvalidStone = 848;
    /// <summary>EResult kItem_SoulBind_Fail_*: 853 InvalidData, 854 OverCount, 855 NotEnoughCost, 860 InternalError.</summary>
    public const int SoulBindInvalidData = 853, SoulBindOverCount = 854, SoulBindNotEnoughCost = 855, SoulBindInternalError = 860;
    static ItemEnhanceTables Enhance => ItemEnhanceTables.Embedded;

    /// <summary>The roll for mana stone and soul bind draws: 0..max-1 (replaceable in checks).</summary>
    public Func<int, int> UpgradeRoll { get; set; } = max => Random.Shared.Next(max);

    /// <summary>Soul bind starts waiting for their completion: item key -> the cost item uid the client named.</summary>
    readonly Dictionary<long, long> soulBindStarts = [];

    /// <summary>
    /// Material plan for one upgrade: stacks are taken from the ones the client listed first (bag or warehouse), then
    /// from bag stacks, largest first, then from warehouse stacks. One entry per stack, so a revision is spent once.
    /// </summary>
    sealed class MaterialPlan(GameSession session, Guid exclude)
    {
        readonly Dictionary<Guid, (long Revision, long Count)> taken = [];
        public IReadOnlyList<(Guid Item, long Revision, int Count)> Costs => taken.Select(t => (t.Key, t.Value.Revision, checked((int)t.Value.Count))).ToList();

        public bool Take(int itemId, long count, IEnumerable<long>? listed = null)
        {
            if (count <= 0) return true;
            var stacks = (listed ?? []).Select(k => session.storedItems.GetValueOrDefault(k)).OfType<ItemState>()
                .Concat(session.storedItems.Values.Where(i => i.Location == "bag").OrderByDescending(i => i.Quantity))
                .Concat(session.storedItems.Values.Where(i => i.Location == "storage").OrderByDescending(i => i.Quantity));
            foreach (var stack in stacks)
            {
                if (count <= 0) break;
                if (stack.TemplateId != itemId || stack.InstanceId == exclude || stack.Location is not ("bag" or "storage")) continue;
                var already = taken.GetValueOrDefault(stack.InstanceId).Count;
                var n = Math.Min(stack.Quantity - already, count);
                if (n <= 0) continue;
                taken[stack.InstanceId] = (stack.Revision, already + n);
                count -= n;
            }
            return count <= 0;
        }
    }

    /// <summary>The item's mana stone slots: stored stones, else the imported official ones; padded to the item's slot count.</summary>
    static IReadOnlyList<(MagicStoneSlot Slot, int Value)> MagicStonesOf(ItemState item)
    {
        var slots = Enhance.Item(item.TemplateId)?.Slots ?? 0;
        var list = new List<(MagicStoneSlot, int)>();
        if (item.Upgrades.ValueKind == JsonValueKind.Object
            && (item.Upgrades.TryGetProperty("magicstones", out var stones) || item.Upgrades.TryGetProperty("official_magicstones", out stones))
            && stones.ValueKind == JsonValueKind.Array)
            foreach (var stone in stones.EnumerateArray())
            {
                if (stone.ValueKind != JsonValueKind.Object || !stone.TryGetProperty("id", out var id) || !id.TryGetInt32(out var stoneId)
                    || !stone.TryGetProperty("stat", out var stat) || !stat.TryGetUInt16(out var wire)
                    || !stone.TryGetProperty("grade", out var grade) || !grade.TryGetByte(out var g)) continue;
                var value = stone.TryGetProperty("value", out var v) && v.TryGetInt32(out var n) ? n : Enhance.StoneValue(stoneId, wire, g);
                list.Add((new MagicStoneSlot(stoneId, wire, g), value));
            }
        while (list.Count < slots) list.Add((default, 0));
        return list;
    }

    /// <summary>The mana stone slots for the item detail on the wire; null keeps the legacy single empty slot.</summary>
    static IReadOnlyList<MagicStoneSlot>? WireMagicStones(ItemState item) =>
        MagicStonesOf(item) is { Count: > 0 } stones ? stones.Select(s => s.Slot).ToList() : null;

    /// <summary>What the equipped items' mana stones add to one stat.</summary>
    int MagicStoneStat(string stat)
    {
        if (ItemStats.WireOf(stat) is not int wire) return 0;
        var total = 0;
        foreach (var item in inventory.Values.Where(i => i.Container == InventoryItem.Equipped))
            if (storedItems.TryGetValue(item.Key, out var row))
                total += MagicStonesOf(row).Where(s => s.Slot.Stat == wire && s.Slot.ItemId != 0).Sum(s => s.Value);
        return total;
    }

    /// <summary>One draw from weighted entries (weights out of their sum).</summary>
    T? Draw<T>(IReadOnlyList<T> entries, Func<T, int> weight) where T : class
    {
        var sum = entries.Sum(weight);
        if (sum <= 0) return null;
        var roll = UpgradeRoll(sum);
        foreach (var entry in entries)
            if ((roll -= weight(entry)) < 0) return entry;
        return entries[^1];
    }

    async Task<List<byte[]>> ImprintAsync(byte[] plain, CancellationToken ct)
    {
        if (plain.Length < 22) return [];
        var key = BitConverter.ToInt64(plain, 2);
        var stoneId = BitConverter.ToInt32(plain, 10);
        var stoneKey = BitConverter.ToInt64(plain, 14);
        byte[] Reply(int result, IReadOnlyList<MagicStoneSlot> slots) => GamePackets.ItemImprint(result, key, stoneId, stoneKey, slots);
        if (Store is not { } store || CharacterId is not { } character || !storedItems.TryGetValue(key, out var row) || row.Location is not ("bag" or "equipment"))
            return [Reply(ImprintInternalError, [])];
        var current = MagicStonesOf(row).Select(s => s.Slot).ToList();
        if (Enhance.Item(row.TemplateId) is not { Slots: > 0 } equip) return [Reply(ImprintNotImprintable, current)];
        if (Enhance.MagicStone(stoneId) is not { } stone || !equip.StoneGroups.Contains(stone.Group)
            || !storedItems.TryGetValue(stoneKey, out var stoneRow) || stoneRow.TemplateId != stoneId)
            return [Reply(ImprintInvalidStone, current)];
        var plan = new MaterialPlan(this, row.InstanceId);
        if (!plan.Take(KinaItemId, equip.ImprintKina) || !plan.Take(stoneId, 1, [stoneKey])) return [Reply(ImprintFail, current)];
        // Every slot is rolled again (official: all four slots rewritten by one stone).
        var stats = Enhance.Imprint(stone.Imprint);
        var rolled = new List<(MagicStoneSlot Slot, int Value)>();
        for (var i = 0; i < equip.Slots; i++)
        {
            if (Draw(stats, s => s.Weight) is not { } stat) return [Reply(ImprintInternalError, current)];
            var grades = stat.Grades.Select(g => new Tuple<int, int, int>(g.Grade, g.Prob, g.Value)).ToList();
            if (Draw(grades, g => g.Item2) is not { } grade) return [Reply(ImprintInternalError, current)];
            rolled.Add((new MagicStoneSlot(stoneId, (ushort)stat.Stat, (byte)grade.Item1), grade.Item3));
        }
        var patch = JsonSerializer.SerializeToElement(new
        {
            magicstones = rolled.Select(s => new { id = s.Slot.ItemId, stat = (int)s.Slot.Stat, grade = (int)s.Slot.Grade, value = s.Value }),
        });
        var done = await Stored(async () => await store.UpgradeItemAsync(character, row.InstanceId, row.Revision, plan.Costs, patch, ct) ? [[]] : []);
        if (done.Count == 0) return [Reply(ImprintInternalError, current)];
        // Official order (2026-11-12): 0x561b (stone and Kina dec 96, item upd 2), then 0x562a.
        var replies = await InventoryChangesAsync(ct, 0, DecreaseItemImprintMagicStone, update: UpdateMagicStone);
        replies.Add(Reply(0, rolled.Select(s => s.Slot).ToList()));
        if (row.Location == "equipment") replies.AddRange(RefreshStats());
        return replies;
    }

    /// <summary>0x562b: an unbound item without a cost item binds for free on equip; otherwise the step's cost is checked.</summary>
    List<byte[]> SoulBindStart(byte[] plain)
    {
        var key = BitConverter.ToInt64(plain, 2);
        var costKey = BitConverter.ToInt64(plain, 10);
        var costId = BitConverter.ToInt32(plain, 18);
        byte[] Reply(int result) => GamePackets.SoulBindStart(key, costKey, costId, result);
        soulBindStarts.Remove(key);
        if (!storedItems.TryGetValue(key, out var row)) return [Reply(SoulBindInvalidData)];
        var count = SoulBindCount(row);
        if (costKey == 0 && costId == 0)
            return [Reply(count == 0 ? 0 : SoulBindInvalidData)];
        if (Enhance.SoulBindStep(row.TemplateId, count) is not { } step) return [Reply(SoulBindOverCount)];
        var plan = new MaterialPlan(this, row.InstanceId);
        if (costId != step.ItemId || !plan.Take(KinaItemId, step.Kina) || !plan.Take(step.ItemId, step.Count, [costKey]))
            return [Reply(SoulBindNotEnoughCost)];
        soulBindStarts[key] = costKey;
        return [Reply(0)];
    }

    /// <summary>
    /// 0x562f ItemSoulBindComplete_RQ: a started step pays its cost and raises the bind count; without a start only the
    /// free first bind is allowed. Answered 0x5630 with the new count and the item update (0x561b) carrying the bind.
    /// </summary>
    async Task<List<byte[]>> SoulBindCompleteAsync(long key, CancellationToken ct)
    {
        if (Store is not { } store || CharacterId is not { } character || !storedItems.TryGetValue(key, out var row))
            return [GamePackets.SoulBindComplete(key, 0, SoulBindInvalidData)];
        var count = SoulBindCount(row);
        var stats = RolledWireStats(row.RolledStats);
        if (!soulBindStarts.Remove(key, out var costKey))
        {
            if (count > 0) return [GamePackets.SoulBindComplete(key, count, SoulBindInvalidData, stats)];
            var bound = await Stored(async () => await store.SoulBindItemAsync(character, row.InstanceId, 1, ct) ? [Array.Empty<byte>()] : []);
            if (bound.Count != 1) return [GamePackets.SoulBindComplete(key, 0, SoulBindInternalError)];
            return [GamePackets.SoulBindComplete(key, 1, 0, stats), .. await InventoryChangesAsync(ct, 0, 0, update: UpdateSoulBind), .. Achieve("SoulbindTry")];
        }
        if (Enhance.SoulBindStep(row.TemplateId, count) is not { } step) return [GamePackets.SoulBindComplete(key, count, SoulBindOverCount, stats)];
        var plan = new MaterialPlan(this, row.InstanceId);
        if (!plan.Take(KinaItemId, step.Kina) || !plan.Take(step.ItemId, step.Count, [costKey]))
            return [GamePackets.SoulBindComplete(key, count, SoulBindNotEnoughCost, stats)];
        var after = (byte)(count + 1);
        var patch = JsonSerializer.SerializeToElement(new { soul_bind = (int)after });
        var done = await Stored(async () => await store.UpgradeItemAsync(character, row.InstanceId, row.Revision, plan.Costs, patch, ct) ? [[]] : []);
        if (done.Count == 0) return [GamePackets.SoulBindComplete(key, count, SoulBindInternalError, stats)];
        var replies = await InventoryChangesAsync(ct, 0, DecreaseItemSoulBind, update: UpdateSoulBind);
        replies.Insert(0, GamePackets.SoulBindComplete(key, after, 0, stats));
        replies.AddRange(Achieve("SoulbindTry"));
        if (row.Location == "equipment") replies.AddRange(RefreshStats());
        return replies;
    }
}
