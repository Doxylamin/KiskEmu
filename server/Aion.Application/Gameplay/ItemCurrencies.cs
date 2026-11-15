namespace Aion.Application.Gameplay;

/// <summary>
/// Item.dat currency items (EItemType::Currency) in the order a container sort puts them, first on slots 0, 1, 2, ...
/// OBSERVED (R1115 0x5611 after a bag sort): 930100031 slot 0, Kina 930100001 slot 1, then 930100003, 930100011,
/// 930100017, 930100021, 930100023, 930100019 on slots 2-7, other items from slot 8.
/// CANDIDATE rule matching that order: Kina (GoldNonbinding) first, then by ItemTableCurrencyInfo.CurrencyCategory
/// (usmap ECurrencyCategory value, tools/data/currency_hud.json); 930100019 (CoinDungeon1, 79) comes after 930100023
/// (CoinEnchant, 71), so it is not the template id.
/// </summary>
public static class ItemCurrencies
{
    public const int KinaNonbinding = 930100031;

    public static readonly IReadOnlyList<int> SortOrder =
    [
        930100031, // GoldNonbinding 94
        930100001, // Gold 1
        930100002, // CoinGuild 2
        930100003, // AbyssPoint 3
        930100024, // CoinBattlePass 4
        930200003, // Mileage 6
        930200001, // GameCash 7
        930200004, // Cash 9
        960300001, 960300002, 960300003, 960300004, 960300005, 960300006, 960300007, 960300008, 960300009, // CoinEvent2-10, 11-19
        930400002, // CoinQuestReset 52
        930100011, // CoinQuest6 60
        930100013, 930100014, 930100015, // Medal2-4, 62-64
        930100017, // CoinQuest7 67
        930100018, // DaevanionPoint 68
        930100020, // SkinDyeCommon 69
        930100021, // SkinDyePremium 70
        930100023, // CoinEnchant 71
        930100019, // CoinDungeon1 79
        930500001, // SoulCrystalAll 80
        930100025, // CoinArena 86
        930100026, 930100027, // DaevanionPointConquest 88, DaevanionPointBattle 89
        930100028, // Season02Crystal01 90
        930100029, // CoinSeason2 91
        930100032, // CoinBattlePass2 92
        930100030, // GoldCombined 93
        930200005, 930200006, // GameCashBinding 95, GameCashNonbinding 96
        930100034, // Season03Crystal01 97
        930100033, // CoinSeason3 98
        930100036, // CoinSeason4 99
        930100035, // CoinBattlePass3 100
        930200008, // GameCashActivating 102
        930200007, // GameCashPending 103
        930100040, // CoinBattlePass4 104
        930100041, // CoinSeason5 105
        930100037, 930100038, 930100039, // CoinOnboarding1-3, 106-108
        930100043, 930100044, 930100045, 930100046, // ExchangeTicket01-04, 109-112
        930100042, // CoinOnboarding4 114
        930100047, // CoinBattlePassEncore1 115
    ];

    static readonly Dictionary<int, int> Rank = SortOrder.Select((id, i) => (id, i)).ToDictionary(x => x.id, x => x.i);

    /// <summary>A currency's place in <see cref="SortOrder"/>, or int.MaxValue for any other item.</summary>
    public static int SortRank(int templateId) => Rank.TryGetValue(templateId, out var rank) ? rank : int.MaxValue;
}
