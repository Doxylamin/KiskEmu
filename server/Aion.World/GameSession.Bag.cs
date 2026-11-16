using Aion.Application.Gameplay;
using Aion.Protocol.Native;

namespace Aion.World;

// Bag operations and the hotbar. Opcodes are catalogue index + run base: ItemMove/Remove/Sort 0x563a/0x563c/
// 0x563e (+ 0x54ca, the 0x56 run around the OBSERVED 0x5610/0x5612/0x5646), QuickSlotSet/Reset 0x5601/0x5603
// (+ 0x54c5 below 0x5610: the live client's unanswered 0x5601 is 27 bytes = QuickSlotInfo, OBSERVED; the hotbar
// list 0x5600 is QuickSlotList_NT). Layouts are the usmap structs; result u16 as every observed RS.
public sealed partial class GameSession
{
    public const ushort ItemMoveRq = 0x563a, ItemMoveRs = 0x563b, ItemRemoveRq = 0x563c, ItemRemoveRs = 0x563d, ItemSortRq = 0x563e, ItemSortRs = 0x563f,
        ItemStoreRq = 0x5642, ItemStoreRs = 0x5643, ItemTakeOutRq = 0x5644, ItemTakeOutRs = 0x5645,
        QuickSlotSetRq = 0x5601, QuickSlotSetRs = 0x5602, QuickSlotResetRq = 0x5603, QuickSlotResetRs = 0x5604,
        QuickSlotContextSkillResetRq = 0x5609, QuickSlotContextSkillResetRs = 0x560a;
    const byte DecreaseItemRemove = 13;   // EDecreaseReason kItemRemove
    readonly SortedDictionary<int, QuickSlotRecord> quickSlots = [];
    public IReadOnlyDictionary<int, QuickSlotRecord> QuickSlots => quickSlots;

    async Task LoadQuickSlotsAsync(INativeCharacterStore store, Guid character, CancellationToken ct)
    {
        quickSlots.Clear();
        var repairs = new List<QuickSlotRecord>();
        var saved = await store.QuickSlotsAsync(character, ct);
        // Free placement (0x5607) moves a skill between the bar's slots (the class's default bar under the saved ones), so a
        // row may hold any skill of the bar's stacks; only skills foreign to the whole bar are repaired.
        var barSkills = saved.Where(s => s.Type == 0 && s.SkillId > 0).Select(s => s.SkillId)
            .Concat(Profile()?.DefaultBar().Select(d => d.SkillId) ?? []).Where(id => id > 0)
            .SelectMany(AllowedContextSkillIds).ToHashSet();
        foreach (var slot in saved)
        {
            if (slot.Type == 0 && slot.SkillId != 0 && !ContextStackValid(slot.SkillId, ContextSkillsForSlot(slot), barSkills))
            {
                var fixedSlot = slot with { ContextSkills = data.DefaultContextSkills(slot.SkillId).ToArray() };
                quickSlots[slot.Slot] = fixedSlot;
                repairs.Add(fixedSlot);
            }
            else quickSlots[slot.Slot] = slot;
        }
        if (repairs.Count == 0) return;
        foreach (var slot in repairs)
            await store.SaveQuickSlotAsync(character, slot.Slot, slot, ct);
    }

    /// <summary>The hotbar at world entry: the player's saved slots, else the class's default bar.</summary>
    /// <summary>The bar at world entry: the class's default bar with the player's saved slots over it.</summary>
    byte[] Hotbar(IEnumerable<(int Slot, int SkillId)> defaults)
    {
        var bar = new SortedDictionary<int, (int Slot, int Type, long ItemUid, int ItemId, int SkillId, IReadOnlyList<int>?)>();
        foreach (var (slot, skill) in defaults)
            bar[slot] = (slot, 0, 0, 0, skill, data.DefaultContextSkills(skill));
        // PCLevelQuickSlot.dat (Steam 2026-10-28 client): every class starts with its potions on the consumable bar
        // (item slots 11-18 = F1-F8, QuickSlotData bEditable); the official level-1 entry sends these item records with
        // the 8 skills (capture 20261013 #376: 14 records). Only items the player holds in the bag; saved slots win.
        foreach (var (slot, itemId) in GamePackets.LevelQuickSlotItems)
            if (storedItems.Values.Where(i => i.Location == "bag" && i.TemplateId == itemId).Select(WireKey).FirstOrDefault() is var uid and not 0)
                bar[slot] = (slot, 1, uid, itemId, 0, null);
        foreach (var s in quickSlots.Values)
        {
            if (IsClearedQuickSlot(s)) bar.Remove(s.Slot);
            else bar[s.Slot] = (s.Slot, s.Type, s.ItemUid, s.ItemId, s.SkillId, ContextSkillsForSlot(s));
        }
        return GamePackets.QuickSlots(bar.Values.ToList());
    }

    /// <summary>A stored slot with nothing in it: how a slot of the default bar stays empty after the player emptied it (OURS).</summary>
    static bool IsClearedQuickSlot(QuickSlotRecord slot) => slot.SkillId == 0 && slot.ItemId == 0 && slot.ItemUid == 0;

    bool HasDefaultQuickSlot(int number) =>
        Profile()?.DefaultBar().Any(s => s.Slot == number) == true || GamePackets.LevelQuickSlotItems.Any(i => i.Slot == number);

    /// <summary>
    /// 0x563a ItemMove_RQ {npc key i32, EItemContainerType u8, item key i64, slot pos i32}: a bag or warehouse item to a
    /// slot of the bag (1) or the warehouse (2, kStorage: the Storage NPCs' depot; kept per character, OURS — the
    /// official one is account-wide). Equipped items go through ItemEquip.
    /// </summary>
    async Task<List<byte[]>> MoveItemInBagAsync(byte[] plain, CancellationToken ct)
    {
        int npc = BitConverter.ToInt32(plain, 2), slot = BitConverter.ToInt32(plain, 15);
        byte container = plain[6];
        var key = BitConverter.ToInt64(plain, 7);
        byte[] Reply(int result) => PacketWriter.Opcode(ItemMoveRs).U16(result).I32(npc).U8(container).I64(key).I32(slot).ToArray();
        var target = container switch { InventoryItem.Bag => "bag", InventoryItem.Storage => "storage", InventoryItem.AccountStorage => "account_storage", _ => null };
        if (target is null || Store is not { } store || CharacterId is not { } character || !storedItems.TryGetValue(key, out var row) || row.Location is not ("bag" or "storage" or "account_storage") || slot < 0)
            return [Reply(1)];
        var moved = await Stored(async () => (target == "bag" && row.Location == "bag"
            ? await store.MoveBagItemAsync(character, row.InstanceId, row.Revision, slot, ct)
            : await store.MoveItemToContainerAsync(character, row.InstanceId, row.Revision, target, slot, ct)) ? [[]] : []);
        if (moved.Count == 0) return [Reply(1)];
        return [Reply(0), .. await InventoryChangesAsync(ct, 0, 0), .. target == "storage" && row.Location == "bag" ? Achieve("StorageStore") : []];
    }

    static string? StoreLocation(byte container) => container switch { InventoryItem.Storage => "storage", InventoryItem.AccountStorage => "account_storage", _ => null };

    /// <summary>0x5642/0x5644 {npc key i32, item map (uid i64 -> count i64), EItemContainerType u8}: the store/take-out request.</summary>
    static (int Npc, List<(long Key, long Count)>? Map, byte Container) ReadItemStoreRequest(byte[] plain)
    {
        try
        {
            var r = new PacketReader(plain) { Offset = 2 };
            var npc = (int)r.U32();
            var count = r.Varint();
            var map = new List<(long, long)>();
            for (var i = 0; i < count && i < 128; i++) map.Add(((long)r.U64(), (long)r.U64()));
            return (npc, map, r.U8());
        }
        catch (ProtocolException) { return (0, null, 0); }
    }

    // Store/take-out reply: u16 EResult (1024 stored, 1040 taken out), the npc key i32, the item map and the container echoed,
    // AFTER the item changes (0x561b). OBSERVED: official 2026-11-12 r1 (i=47255 `4356 0004 00000000 01 ... 0f`, i=47502
    // `4556 1004 ...`) and 2026-11-15 (i=68424, 71191, 122081, 308111; each right after its 0x561b), as the A2Core fork reads it
    // (#70). Our earlier flag u8 / npc / result u8 reading matched those bytes only with npc 4. Refusal code: CANDIDATE.
    public const ushort ItemStored = 1024, ItemTakenOut = 1040;
    const ushort ItemStoreRefused = 1;
    static byte[] ItemStoreReply(ushort opcode, ushort result, int npc, List<(long Key, long Count)>? map, byte container)
    {
        var w = PacketWriter.Opcode(opcode).U16(result).I32(npc).Varint(map?.Count ?? 0);
        foreach (var (key, count) in map ?? []) w.I64(key).I64(count);
        return w.U8(container).ToArray();
    }

    /// <summary>
    /// 0x5642 ItemStore_RQ: bag stacks (item uid -> count) are deposited into the personal warehouse (container 15) or the
    /// account warehouse (container 2, shared across the account). Answered 0x5643, after the item changes (0x561b).
    /// </summary>
    async Task<List<byte[]>> StoreItemsAsync(byte[] plain, CancellationToken ct)
    {
        var (npc, map, container) = ReadItemStoreRequest(plain);
        byte[] Reply(ushort result) => ItemStoreReply(ItemStoreRs, result, npc, map, container);
        var location = StoreLocation(container);
        if (location is null || map is not { Count: > 0 } || Store is not { } store || CharacterId is not { } character) return [Reply(ItemStoreRefused)];
        var costs = new List<(Guid, long, int)>();
        foreach (var (key, count) in map)
            if (storedItems.TryGetValue(key, out var row) && row.Location == "bag" && count > 0 && count <= row.Quantity)
                costs.Add((row.InstanceId, row.Revision, (int)count));
        if (costs.Count != map.Count) return [Reply(ItemStoreRefused)];
        // Every stored stack takes a slot of its own (the store does not merge). Live 2026-11-15 the character warehouse
        // took 81 stacks in 80 slots: refuse what does not fit.
        if (storedItems.Values.Count(i => i.Location == location) + costs.Count > SlotCapacity(container)) return [Reply(ItemStoreRefused)];
        var stored = await Stored(async () => await store.StoreItemsInContainerAsync(character, location, costs, ct) ? [[]] : []);
        if (stored.Count == 0) return [Reply(ItemStoreRefused)];
        return [.. await InventoryChangesAsync(ct, 0, 0), Reply(ItemStored), .. location == "storage" ? Achieve("StorageStore") : []];
    }

    /// <summary>
    /// 0x5644 ItemTakeOut_RQ: warehouse or account-storage stacks (item uid -> count) move back into this character's bag.
    /// Answered 0x5645, then the item changes (0x561b).
    /// </summary>
    async Task<List<byte[]>> TakeItemsAsync(byte[] plain, CancellationToken ct)
    {
        var (npc, map, container) = ReadItemStoreRequest(plain);
        byte[] Reply(ushort result) => ItemStoreReply(ItemTakeOutRs, result, npc, map, container);
        var location = StoreLocation(container);
        if (location is null || map is not { Count: > 0 } || Store is not { } store || CharacterId is not { } character) return [Reply(ItemStoreRefused)];
        var take = new List<(long, int)>();
        foreach (var (key, count) in map)
        {
            if (!storedItems.TryGetValue(key, out var row) || row.Location != location || count < 1 || count > row.Quantity) return [Reply(ItemStoreRefused)];
            take.Add((key, (int)count));
        }
        var taken = await Stored(async () => await store.TakeItemsFromContainerAsync(character, location, take, ct) ? [[]] : []);
        if (taken.Count == 0) return [Reply(ItemStoreRefused)];
        return [.. await InventoryChangesAsync(ct, 0, 0), Reply(ItemTakenOut)];
    }

    /// <summary>
    /// 0x563c ItemRemove_RQ {map item key -> count (bag), map (temporary bag)}: discarded, answered with the same maps. Success is
    /// u16 0x0300 (768), from the A2Core fork's recordings (addiboii b9d1858, #70); not in ours. Refusal 1: CANDIDATE.
    /// </summary>
    public const ushort ItemRemoved = 0x0300;
    async Task<List<byte[]>> RemoveItemsAsync(byte[] plain, CancellationToken ct)
    {
        var bag = new List<(long Key, long Count)>();
        var temporary = new List<(long Key, long Count)>();
        try
        {
            var reader = new PacketReader(plain) { Offset = 2 };
            foreach (var list in new[] { bag, temporary })
            {
                var count = reader.Varint();
                for (var i = 0; i < count && i < 128; i++) list.Add(((long)reader.U64(), (long)reader.U64()));
            }
        }
        catch (ProtocolException) { return []; }
        byte[] Reply(int result)
        {
            var w = PacketWriter.Opcode(ItemRemoveRs).U16(result);
            foreach (var list in new[] { bag, temporary })
            {
                w.Varint(list.Count);
                foreach (var (key, count) in list) w.I64(key).I64(count);
            }
            return w.ToArray();
        }
        if (Store is not { } store || CharacterId is not { } character || temporary.Count > 0 || bag.Count == 0) return [Reply(1)];
        foreach (var (key, count) in bag)
        {
            if (!storedItems.TryGetValue(key, out var row) || row.Location != "bag" || count < 1 || count > row.Quantity) return [Reply(1)];
            var removed = await Stored(async () => await store.ConsumeItemAsync(character, row.InstanceId, row.Revision, (int)count, ct) ? [[]] : []);
            if (removed.Count == 0) return [Reply(1), .. await InventoryChangesAsync(ct, 0, DecreaseItemRemove)];
        }
        return [Reply(0), .. await InventoryChangesAsync(ct, 0, DecreaseItemRemove)];
    }

    /// <summary>0x563f ItemSort_RS result after a sort (OBSERVED R1115 i=82211 `3f564002000000000f`, i=90078, i=736353).</summary>
    public const ushort ItemSortDone = 576;

    /// <summary>
    /// 0x563e ItemSort_RQ {npc key i32, EItemContainerType u8}: the bag, warehouse or account warehouse ordered with the
    /// currencies first (ItemCurrencies.SortOrder) and the other items by template. OBSERVED: the 0x561b listing the moved
    /// items comes first, then 0x563f {u16 576, npc i32, container u8}. A refusal stays result 1 (OURS).
    /// </summary>
    async Task<List<byte[]>> SortBagAsync(byte[] plain, CancellationToken ct)
    {
        var npc = BitConverter.ToInt32(plain, 2);
        var container = plain[6];
        byte[] Reply(int result) => PacketWriter.Opcode(ItemSortRs).U16(result).I32(npc).U8(container).ToArray();
        var location = container switch { InventoryItem.Bag => "bag", InventoryItem.Storage => "storage", InventoryItem.AccountStorage => "account_storage", _ => null };
        if (location is null || Store is not { } store || CharacterId is not { } character) return [Reply(1)];
        var sorted = await Stored(async () => await store.SortContainerAsync(character, location, ct) ? [[]] : []);
        return sorted.Count == 0 ? [Reply(1)] : [.. await InventoryChangesAsync(ct, 0, 0), Reply(ItemSortDone)];
    }

    /// <summary>
    /// 0x5601 QuickSlotSet_RQ {QuickSlotInfo: slot u8, EQuickSlotType u8, item uid i64, item id i32, skill id i32,
    /// context skills (varint + i32), macro u8, social action i32, toggle u8}: stored, echoed in 0x5602.
    /// </summary>
    async Task<List<byte[]>> SetQuickSlotAsync(byte[] plain, CancellationToken ct)
    {
        int number, type, itemId, skillId;
        long uid;
        int[]? context = null;
        try
        {
            // QuickSlotInfo starts with its presence mask (live 2026-10-16 `00 0e 01 ...` for a potion on slot 14; the reader
            // took the mask for the slot and the slot for the type, so every item drag was refused).
            var reader = new PacketReader(plain) { Offset = 2 };
            var mask = reader.U8();
            number = reader.U8(); type = reader.U8(); uid = (long)reader.U64(); itemId = (int)reader.U32(); skillId = (int)reader.U32();
            if ((mask & 1) != 0 && type == 0 && skillId != 0)
            {
                var count = reader.U8();
                var ids = new int[4];
                for (var i = 0; i < count && i < 4; i++) ids[i] = (int)reader.U32();
                context = ids;
            }
        }
        catch (ProtocolException) { return []; }
        byte[] Reply(int result) => PacketWriter.Opcode(QuickSlotSetRs).U16(result).Raw(plain.AsSpan(2)).ToArray();
        if (Store is not { } store || CharacterId is not { } character || type is < 0 or > 4) return [Reply(1)];
        if (type == 1 && uid != 0 && (!storedItems.TryGetValue(uid, out var itemRow) || itemRow.Location != "bag" || itemRow.TemplateId != itemId))
            return [Reply(1)];
        if (type == 0 && skillId != 0 && !learned.Contains(skillId)) return [Reply(1)];
        context ??= type == 0 && skillId != 0 ? data.DefaultContextSkills(skillId).ToArray() : null;
        var slot = new QuickSlotRecord(number, type, uid, itemId, skillId, context);
        var saved = await Stored(async () => await store.SaveQuickSlotAsync(character, number, slot, ct) ? [[]] : []);
        if (saved.Count == 0) return [Reply(1)];
        quickSlots[number] = slot;
        return [Reply(0)];
    }

    /// <summary>0x5603 QuickSlotReset_RQ {slot u8}: cleared, answered 0x5604 {result, slot}.</summary>
    async Task<List<byte[]>> ResetQuickSlotAsync(byte number, CancellationToken ct)
    {
        byte[] Reply(int result) => PacketWriter.Opcode(QuickSlotResetRs).U16(result).U8(number).ToArray();
        if (Store is not { } store || CharacterId is not { } character) return [Reply(1)];
        // A default-bar slot is emptied with a stored empty record; deleting the row would bring the default back at the next login.
        var tombstone = HasDefaultQuickSlot(number) ? new QuickSlotRecord(number, 0, 0, 0, 0, [0, 0, 0, 0]) : null;
        var saved = await Stored(async () => await store.SaveQuickSlotAsync(character, number, tombstone, ct) ? [[]] : []);
        if (saved.Count == 0) return [Reply(1)];
        if (tombstone is null) quickSlots.Remove(number);
        else quickSlots[number] = tombstone;
        return [Reply(0)];
    }

    /// <summary>
    /// 0x5609 QuickSlotContextSkillReset_RQ (no body; the Skill Order window's reset button): every skill slot's stack goes
    /// back to its class default, saved, answered 0x560a {result u16} and the bar sent again as 0x5600 (CANDIDATE: no official reply sample).
    /// </summary>
    async Task<List<byte[]>> ResetQuickSlotContextAsync(CancellationToken ct)
    {
        byte[] Reply(int result) => PacketWriter.Opcode(QuickSlotContextSkillResetRs).U16(result).ToArray();
        if (Store is not { } store || CharacterId is not { } character || Profile() is not { } profile) return [Reply(1)];
        var changed = quickSlots.Values
            .Where(s => s.Type == 0 && s.SkillId != 0)
            .Select(s => s with { ContextSkills = data.DefaultContextSkills(s.SkillId).ToArray() })
            .Where(s => !s.ContextSkills!.SequenceEqual(ContextSkillsForSlot(quickSlots[s.Slot])))
            .ToList();
        var saved = await Stored(async () =>
        {
            foreach (var slot in changed)
                if (!await store.SaveQuickSlotAsync(character, slot.Slot, slot, ct)) return [];
            return [[]];
        });
        if (saved.Count == 0) return [Reply(1)];
        foreach (var slot in changed) quickSlots[slot.Slot] = slot;
        return [Reply(0), Hotbar(profile.DefaultBar())];
    }

    /// <summary>Party members on the same map within this distance share a kill's experience (OURS).</summary>
    public const double PartyExpRange = 10_000;

    /// <summary>The killer's party members nearby (not the killer).</summary>
    List<WorldPlayer> NearbyPartyMembers()
    {
        if (worldPlayer?.Map is not { } map || position is not { } at || Social?.PartyOf(worldPlayer) is not { } party) return [];
        return party.Others(worldPlayer).Where(m => m.Map == map && !m.Session.Dead
            && (m.Position.X - at.X) * (m.Position.X - at.X) + (m.Position.Y - at.Y) * (m.Position.Y - at.Y) <= PartyExpRange * PartyExpRange).ToList();
    }

    /// <summary>Another member's share of a kill: applied on that player's own loop (its store, its messages).</summary>
    public void ShareExperience(long gained, CancellationToken ct = default) =>
        _ = InvokeAsync(async now => (true, gained > 0 ? await GainExperienceAsync(experience + gained, gained, ExpReasonNpc, ct) : []));
}
