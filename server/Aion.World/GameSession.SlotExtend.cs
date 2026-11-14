using Aion.Protocol.Native;

namespace Aion.World;

// Bag and warehouse expansion, ported from the zaraza87 fork (GameSession.SlotExtend.cs, forge #72).
//
// ItemContainerSlotExtend_RQ 0x8d4f {npc key i32, EItemContainerType u8, item uid i64 (0: pay Kina), cost idx u8}: OBSERVED
// live 2026-11-14 as a 16-byte request from the bag's expand button (game version 3533), unanswered until now. Answered
// _RS 0x8d50 {result u16, npc key i32, container u8, item uid i64, cost idx u8, extensions by item u8, by Kina u8} and
// _NT 0x8d51 {container u8, item uid i64, by item u8, by Kina u8}. CANDIDATE: the opcodes are usmap 0x398a-0x398c plus the
// offset of the OBSERVED neighbours 0x8d4e BroadcastSealStoneChange_NT and 0x8d52; the fields are the usmap's; the result
// is u16 as every observed reply. The result codes are EResult's (usmap): 544 success, 545 fail, 546 over the maximum,
// 547 invalid item, 263 not enough Kina. The Kina or ticket goes out as 0x561b with EDecreaseReason
// kItemContainerExtendInventory 7 / kItemContainerExtendStorage 8 (usmap).
//
// Sizes and prices are the client's tables: one expansion is one line of 5 slots (GlobalSetting inventory_slot_count_per_line
// / storage_slot_count_per_line). The bag starts with 8 lines (inventory_default_slot_line_count), both warehouses with 5;
// each goes up to 80 lines. Kina per expansion: the bag's n-th from InventorySlot SlotExtensionCostList (10 steps); a
// warehouse's next line from StorageExtensionCost (SlotLineNum 1-36; lines 1-5 are the free default ones). The counts persist
// in the character setting "slot_extends" and go out as ItemDepotList_RS's six trailing counts.
public sealed partial class GameSession
{
    public const ushort SlotExtendRq = 0x8d4f, SlotExtendRs = 0x8d50, SlotExtendNt = 0x8d51;
    public const int SlotsPerLine = 5, BagDefaultLines = 8, BagMaxLines = 80, StorageDefaultLines = 5, StorageMaxLines = 80;

    /// <summary>EResult (usmap) codes the expansion reply uses.</summary>
    public const ushort SlotExtendSuccess = 544, SlotExtendFail = 545, SlotExtendFailOverMax = 546, SlotExtendFailInvalidItem = 547,
        SlotExtendFailNotEnoughKina = 263;

    /// <summary>EDecreaseReason (usmap) kItemContainerExtendInventory / kItemContainerExtendStorage.</summary>
    const byte DecreaseExtendInventory = 7, DecreaseExtendStorage = 8;

    /// <summary>InventorySlot SlotExtensionCostList (GoldCombined): Kina for the bag's 1st..10th Kina expansion.</summary>
    public static readonly long[] BagExtendKina = [10_000, 20_000, 40_000, 80_000, 120_000, 180_000, 250_000, 400_000, 600_000, 1_000_000];

    /// <summary>StorageExtensionCost SlotExtensionCostList / CharStorageSlotExtensionCostList (identical): Kina for warehouse
    /// line 1..36 (index 0 = SlotLineNum 1; the first five are the default lines).</summary>
    public static readonly long[] StorageExtendKina = [1, 1, 1, 1, 1, 1_000, 1_000, 1_500, 2_000, 2_500, 3_500, 5_000, 6_500, 9_000, 15_000,
        20_000, 25_000, 35_000, 45_000, 60_000, 80_000, 150_000, 150_000, 200_000, 300_000, 400_000, 550_000, 700_000, 950_000,
        1_300_000, 1_800_000, 2_500_000, 3_300_000, 4_500_000, 6_200_000, 8_400_000];

    const string SlotExtendsSetting = "slot_extends";

    /// <summary>ItemDepotList_RS order: bag by item, bag by Kina, storage by Kina, storage by item, character storage by Kina,
    /// character storage by item.</summary>
    int[] slotExtends = new int[6];
    public IReadOnlyList<int> SlotExtends => slotExtends;

    /// <summary>The (by item, by Kina) indexes into slotExtends for an EItemContainerType (kInventory 1, kStorage 2,
    /// kCharacterStorage 15); null: not expandable.</summary>
    static (int Item, int Kina)? SlotExtendIndex(byte container) => container switch
    {
        1 => (0, 1),
        2 => (3, 2),
        15 => (5, 4),
        _ => null,
    };

    /// <summary>Slot lines a container has now: its default lines plus the expansions (one line each), up to its maximum.</summary>
    int SlotLines(byte container)
    {
        if (SlotExtendIndex(container) is not { } at) return 0;
        var (initial, max) = container == InventoryItem.Bag ? (BagDefaultLines, BagMaxLines) : (StorageDefaultLines, StorageMaxLines);
        return Math.Min(max, initial + slotExtends[at.Item] + slotExtends[at.Kina]);
    }

    /// <summary>Slots a bag or warehouse has now (lines × 5).</summary>
    public int SlotCapacity(byte container) => SlotLines(container) * SlotsPerLine;

    /// <summary>Kina for the next Kina expansion of a container; null when the table has no further step.</summary>
    long? NextExtendKina(byte container)
    {
        if (SlotExtendIndex(container) is not { } at) return null;
        if (container == InventoryItem.Bag)
            return slotExtends[at.Kina] < BagExtendKina.Length && SlotLines(container) < BagMaxLines ? BagExtendKina[slotExtends[at.Kina]] : null;
        var line = SlotLines(container) + 1;   // StorageExtensionCost SlotLineNum of the line bought
        return line <= StorageExtendKina.Length && line <= StorageMaxLines ? StorageExtendKina[line - 1] : null;
    }

    void LoadSlotExtends(int[]? stored) => slotExtends = stored is { Length: 6 } ? stored : new int[6];

    async Task<List<byte[]>> SlotExtendAsync(byte[] plain, CancellationToken ct)
    {
        var npc = BitConverter.ToInt32(plain, 2);
        var container = plain[6];
        var uid = BitConverter.ToInt64(plain, 7);
        var costIdx = plain[15];
        var index = SlotExtendIndex(container);
        byte[] Reply(ushort result)
        {
            var (byItem, byKina) = index is { } i ? (slotExtends[i.Item], slotExtends[i.Kina]) : (0, 0);
            return PacketWriter.Opcode(SlotExtendRs).U16(result).I32(npc).U8(container).I64(uid).U8(costIdx)
                .U8((byte)byItem).U8((byte)byKina).ToArray();
        }
        if (index is not { } at || Store is not { } store || CharacterId is not { } character) return [Reply(SlotExtendFail)];
        var maxLines = container == InventoryItem.Bag ? BagMaxLines : StorageMaxLines;
        if (SlotLines(container) >= maxLines) return [Reply(SlotExtendFailOverMax)];

        List<(Guid Item, long Revision, int Count)> costs;
        if (uid != 0)
        {
            // The client offers paying with an item only for an expansion ticket; ticket ids are not in our tables, so the
            // named bag item is taken as the ticket.
            if (!storedItems.TryGetValue(uid, out var ticket) || ticket.Location != "bag") return [Reply(SlotExtendFailInvalidItem)];
            costs = [(ticket.InstanceId, ticket.Revision, 1)];
        }
        else if (NextExtendKina(container) is not { } price) return [Reply(SlotExtendFailOverMax)];
        else if (CostStacks(KinaItemId, price) is { } kina) costs = kina;
        else return [Reply(SlotExtendFailNotEnoughKina)];

        var paid = await Stored(async () =>
        {
            foreach (var (item, revision, count) in costs)
                if (!await store.ConsumeItemAsync(character, item, revision, count, ct)) return [];
            return [[]];
        });
        if (paid.Count == 0) return [Reply(SlotExtendFail), .. await InventoryChangesAsync(ct, 0, 0)];

        slotExtends[uid != 0 ? at.Item : at.Kina]++;
        await SaveSettingAsync(SlotExtendsSetting, slotExtends, ct);
        var nt = PacketWriter.Opcode(SlotExtendNt).U8(container).I64(uid)
            .U8((byte)slotExtends[at.Item]).U8((byte)slotExtends[at.Kina]).ToArray();
        var reason = container == InventoryItem.Bag ? DecreaseExtendInventory : DecreaseExtendStorage;
        return [Reply(SlotExtendSuccess), nt, .. await InventoryChangesAsync(ct, 0, reason)];
    }
}
