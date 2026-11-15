using Aion.Protocol.Native;

namespace Aion.World;

// The list and state messages of the world entry, in the official order. OBSERVED order: our own recordings' world entries
// (2026-11-12, client 3535, two level-45 characters; the forks' 2.0.6 recordings have the same order where they overlap):
//   0xe263 | 0x8d53 0x8d58 0x5648 0x564b 0x5679 0x5682 0x5683 0x5633 0x8aaf 0xe28c 0x8d5b 0xe22e 0xe226 0xe237 0x9330 0x610b
//   0x8d7e 0x8d37 0x8d8c 0xe224 0xe32a 0x9331 0xe33b 0xe33e 0xffae 0x6103 [0xe30b] 0xe324 0x56b6 0xe256 0xffa5 0xffa7 0xe262
//   0x561d 0xe25c 0xe342 0xe343 0x5611 0x572d, PrepareEnterMap 0x3621, and after the load reply and the skill list and
//   hotbar: 0xe251 0x400a 0xe200 0xffab, the own appearance, 0xe223 0x6000; 0x570f later.
// Left out of the list (not sent by us or sent elsewhere): 0x8d44, 0x5672, 0x56ac, 0x9232, 0x8d1c, 0xe33d, 0xe257, 0xe352,
// 0xffb0, 0x5712, 0x5684, 0x8d09; MonolithList_NT 0xe30b goes out with the map's objects (GameSession.Monolith.cs).
// The official server sends the part up to 0x572d before 0x3621 and the stats, appearance and abnormals (0x3633, 0x3646,
// 0x382a, 0x364a) after the lists; ours sends the whole block after the load reply and the appearance (OURS: our entry
// works that way; LoadProtection keeps the recorded order of the appearance part).
// Names from the catalogue runs (0x54ca / 0xdd07) and the usmap; '?' where our mapping has none.
public sealed partial class GameSession
{
    static byte[] Observed(ushort opcode, string hex) => [.. PacketWriter.Opcode(opcode).ToArray(), .. Convert.FromHexString(hex)];

    /// <summary>The entry lists up to 0x572d (the part the official server sends before 0x3621), in the recorded order.</summary>
    List<byte[]> EntryLists(long now, bool withProfile)
    {
        ApplyAbyssWeeklyReset(now);
        var replies = new List<byte[]>
        {
            Observed(0xe263, "023f005300"),            // TradeServerInfo_NT
            SeasonSchedule(now),                       // SeasonSchedule_NT 0x8d53
        };
        if (withProfile)
            replies.AddRange([Observed(0x8d58, "00"), PotionAutoUseList(), ItemAutoUseList()]);   // MyPrevSeasonRanking_NT; 0x5648, 0x564b
        replies.AddRange(
        [
            TitleList(),                               // 0x5679
            PrevSeasonMissionInfo(now),                // 0x5682
            AchievementList(now),                      // 0x5683
            GodstoneList(),                            // 0x5633
        ]);
        if (withProfile) replies.AddRange([AgitDecoListAtEntry(), PresetInfo()]);   // 0x8aaf, PresetInfo_NT 0xe28c right after it
        replies.AddRange(
        [
            SkinInfo(),                                // 0x8d5b
            DaevanionPointUpdate(),                    // 0xe22e
            DaevanionBoardInfo(),                      // 0xe226
            BossChallengeList(),                       // 0xe237
            BookmarkedContentList(),                   // 0x9330 right before 0x610b
            ContentsTicketsList(now),                  // 0x610b
        ]);
        replies.AddRange(CraftListsAtEntry());         // 0x8d7e, 0x8d37, 0x8d8c
        replies.AddRange(
        [
            Observed(0xe224, "02010202"),              // ContentsControl_NT
            Observed(0xe32a, "0301dc05000000000000000000000002dc05000000000000000000000003dc050000000000000000000000"),   // ArenaRanking_NT: Single/Team/Strategy at 1500
            PartyDungeonChallengeList(),               // 0x9331
            SubscribeList(now),                        // SubscribeList_NT 0xe33b (`00` without a membership)
            Observed(0xe33e, "00"),                    // ? (0xe33e)
            AccountCurrencyLimitStatus(now),           // 0xffae
            WaveDungeonInfoAtEntry(now),               // WaveDungeonInfo_NT 0x6103: the dailies' best today (`00` without one)
            // [MonolithList_NT 0xe30b: recorded here; sent with the map's objects, see the file remarks]
            BattlePassInfoList(now),                   // BattlePassInfoList_NT 0xe324
            AccountContentsInfoList(now),              // AccountContentsInfoList_NT 0x56b6 (`00` without keyed attendance seasons)
            PeriodCollectionList(now),                 // PeriodCollectionList_NT 0xe256
            KinaStatus(0xffa5, now),                   // ? (0xffa5)
            KinaStatus(0xffa7, now),                   // ? (0xffa7)
            TrustLimitInfoNotice(now),                 // TrustLimitInfo_NT 0xe262
        ]);
        replies.AddRange(InventoryDepotSyncPackets()); // 0x5611 and 0x561d (recorded: 0x561d here, the depot list before 0x572d)
        replies.Add(Observed(0xe25c, "014b01"));       // ContentsFeatureOnOffListByAdmin_NT
        if (Store is not null) replies.Add(RestorableItems());   // the buyback list 0xe342
        if (withProfile) replies.Add(ItemRestoreStatus(now));   // 0xe343 right after it
        replies.Add(DungeonCoolTimesList(now));        // OURS: not in the recordings; kept with the lists
        replies.Add(Observed(0x572d, "00"));           // ? (0x572d): `00` in every recorded entry, the last list before 0x3621
        return replies;
    }

    /// <summary>
    /// The entry lists after the skill list and the hotbar (the official server sends them after the load reply, around the own
    /// appearance), in the recorded order: 0xe251, 0x400a, 0xe200, 0xffab, 0xe223, 0x6000, 0x570f.
    /// </summary>
    List<byte[]> EntryListsAfterLoad(long now) =>
    [
        Observed(0xe251, "00"),                    // BMShopTutorialInfo_NT
        ArenaMatchingInfo(),                       // ArenaMatchingInfo_NT 0x400a
        Observed(0xe200, "00002392ffffffffff000000"),   // MessageCardInitData_NT
        AbyssPointUpdate(),                        // AbyssPointUpdate_NT 0xffab
        RankingPointUpdate(),                      // 0xe273 (OURS: not in the recordings; kept with 0xffab)
        PacketWriter.Opcode(0xe223).I64(now).I32(0).ToArray(),   // WorldChatFreeChance_NT (observed: a time then four zero bytes)
        Observed(0x6000, "00"),                    // ? (0x6000)
        Observed(0x570f, "000000"),                // DungeonClearKillNpc_NT
    ];

    /// <summary>
    /// 0xffae (zaraza87: AccountCurrencyLimitStatus_NT): varint 2 Ã— {category u8, count i32}, then the time i64. OBSERVED
    /// `02 01 00000000 02 00000000 &lt;now&gt;` in both of our 2026-11-12 entries (and zaraza87's 10); addiboii's level-1 entries had
    /// the empty `00 &lt;now&gt;`. CANDIDATE: the counts (always 0 in the recordings; we track none).
    /// </summary>
    public static byte[] AccountCurrencyLimitStatus(long now) =>
        PacketWriter.Opcode(0xffae).Varint(2).U8(1).I32(0).U8(2).I32(0).I64(now).ToArray();

    /// <summary>
    /// 0xffa5 / 0xffa7 {i64 amount, i64 time} (zaraza87 names 0xffa7 InitKinaLimitStatus_NT). OBSERVED `0 &lt;now&gt;` for both in
    /// our 2026-11-12 entries (0xffa7 also 2 and 0xffa5 0x83 in later ones; addiboii and zaraza87 recorded 0 at level 1). The
    /// amount is 0 (OURS: we keep no Kina limit).
    /// </summary>
    public static byte[] KinaStatus(ushort opcode, long now) => PacketWriter.Opcode(opcode).I64(0).I64(now).ToArray();

    /// <summary>
    /// ArenaMatchingInfo_NT 0x400a: one contents type (1) with its two matching servers. OBSERVED `0a40 01 01 02 2905 01 2b05 02`
    /// in both of our 2026-11-12 entries = 1 type {1: [(server 1321, Light 1), (server 1323, Dark 2)]}, the server ids u16
    /// (zaraza87's builder; addiboii replays two recorded bodies by race). CANDIDATE: this world for both races (one world here
    /// serves both; the official pairs a Light and a Dark world).
    /// </summary>
    byte[] ArenaMatchingInfo() => ArenaMatchingInfo(ServerId, ServerId);

    public static byte[] ArenaMatchingInfo(int lightServer, int darkServer) =>
        PacketWriter.Opcode(0x400a).Varint(1).U8(1).Varint(2).U16(lightServer).U8(1).U16(darkServer).U8(2).ToArray();
}
