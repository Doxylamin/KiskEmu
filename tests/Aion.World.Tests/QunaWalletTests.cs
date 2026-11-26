using Aion.Application.Gameplay;
using Aion.Protocol.Native;
using Aion.World;
using Xunit;
using static Aion.World.Tests.GameSessionTestHelpers;

namespace Aion.World.Tests;

/// <summary>
/// Quna as the realm's account wallet (server/DATABASES.md, migration 095): the ledger's rules (in memory, the same as
/// game.quna_apply), and a world session that spends and grants through the wallet and shows a virtual bag stack.
/// </summary>
public sealed class QunaWalletTests
{
    const long LiveNow = 1792000000000L;
    const int QunaItem = 535300006;
    const int PaidPass = 100151;

    static async Task<(GameSession Session, FakeCharacterStore Store, IReadOnlyList<byte[]> Entry)> EnterWithQunaAsync(long quna, bool asItem = false)
    {
        GameSession.ConfigureShopOptions(new ShopOptions { PremiumMode = ShopPremiumMode.Quna });
        var store = new FakeCharacterStore(Guid.NewGuid());
        // asItem: a stack stored before the wallet (the world neither credits nor shows it; migrate-quna moves it).
        if (asItem) store.Items.Add(FakeCharacterStore.BagItem(QunaItem, quna, 9001));
        else store.Quna.Seed(store.AccountId, quna);
        var session = new GameSession(GameData.Embedded, new GameSessionOptions { CharacterLevel = 45, WorldId = store.WorldId }, new Random(3)) { Store = store };
        await session.HandleAsync(Login(), LiveNow);
        await session.HandleAsync(LightCreate(), LiveNow);
        var wireId = store.States[0].WireId;
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)wireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), LiveNow);
        var entry = await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), LiveNow);
        return (session, store, entry);
    }

    static long RefreshedBalance(IEnumerable<byte[]> packets)
    {
        var r = new PacketReader(packets.Last(p => Op(p) == GameSession.RefreshCurrencyNt)) { Offset = 2 };
        Assert.Equal(GameSession.QunaBmCurrencyGroupId, r.I32());
        r.Varint();
        Assert.Equal(GameSession.GameCashCurrencyCategory, r.I32());
        return r.I64();
    }

    static InventoryItem? VirtualStack(GameSession session) => session.ShownItems.SingleOrDefault(i => i.ItemId == QunaItem);

    static byte[] BuyPass() => new PacketWriter().U16(GameSession.BattlePassPurchaseRq).I32(PaidPass).ToArray();

    // ---------- The ledger ----------

    [Fact]
    public async Task A_repeated_request_id_returns_the_first_result_and_changes_nothing()
    {
        var wallet = new MemoryQunaWallet();
        var account = Guid.NewGuid();
        wallet.Seed(account, 5_000);
        var spend = new QunaRequest(account, "w1204:pass:1", 1_500, QunaWalletRules.Purchase, 1204);
        var first = await wallet.SpendAsync(spend);
        var again = await wallet.SpendAsync(spend);
        Assert.True(first.Ok);
        Assert.False(first.Repeated);
        Assert.Equal(3_500, first.Balance);
        Assert.True(again.Repeated);
        Assert.Equal((first.Balance, first.EntryId), (again.Balance, again.EntryId));
        Assert.Equal(3_500, (await wallet.BalanceAsync(account)).Balance);
        Assert.Equal(2, wallet.Ledger.Count);
        // The same request id for another amount (or account) is a conflict, not a second spend.
        var conflict = await wallet.SpendAsync(spend with { Amount = 10 });
        Assert.Equal(QunaWalletRules.RequestConflict, conflict.Error);
        Assert.Equal(2, wallet.Ledger.Count);
    }

    [Fact]
    public async Task A_spend_above_the_balance_is_refused_and_leaves_no_entry()
    {
        var wallet = new MemoryQunaWallet();
        var account = Guid.NewGuid();
        wallet.Seed(account, 1_000);
        var refused = await wallet.SpendAsync(new QunaRequest(account, "too-much", 1_001, QunaWalletRules.Purchase));
        Assert.False(refused.Ok);
        Assert.Equal(QunaWalletRules.Insufficient, refused.Error);
        Assert.Equal(1_000, refused.Balance);
        Assert.Single(wallet.Ledger);
        // Refused requests are not recorded: the same id may succeed once the balance covers it.
        await wallet.GrantAsync(new QunaRequest(account, "topup", 1, QunaWalletRules.Grant));
        Assert.True((await wallet.SpendAsync(new QunaRequest(account, "too-much", 1_001, QunaWalletRules.Purchase))).Ok);
        Assert.Equal(0, (await wallet.BalanceAsync(account)).Balance);
    }

    [Fact]
    public async Task A_refund_gives_a_spend_back_once()
    {
        var wallet = new MemoryQunaWallet();
        var account = Guid.NewGuid();
        wallet.Seed(account, 2_000);
        await wallet.SpendAsync(new QunaRequest(account, "buy", 700, QunaWalletRules.Purchase, 1205));
        var refund = await wallet.RefundAsync(account, "buy");
        var again = await wallet.RefundAsync(account, "buy");
        Assert.Equal(2_000, refund.Balance);
        Assert.True(again.Repeated);
        Assert.Equal(2_000, (await wallet.BalanceAsync(account)).Balance);
        Assert.Equal((QunaWalletRules.Refund, 700L, (int?)1205), (wallet.Ledger[^1].Reason, wallet.Ledger[^1].Delta, wallet.Ledger[^1].WorldId));
        // Only a spend of that account can be refunded.
        Assert.Equal(QunaWalletRules.NotFound, (await wallet.RefundAsync(Guid.NewGuid(), "buy")).Error);
        Assert.Equal(QunaWalletRules.NotFound, (await wallet.RefundAsync(account, "unknown")).Error);
    }

    [Fact]
    public async Task A_staff_deduction_cannot_be_refunded()
    {
        var wallet = new MemoryQunaWallet();
        var account = Guid.NewGuid();
        wallet.Seed(account, 2_000);
        Assert.True((await wallet.SpendAsync(new QunaRequest(account, "staff-take", 500, QunaWalletRules.Staff))).Ok);
        Assert.Equal(QunaWalletRules.NotFound, (await wallet.RefundAsync(account, "staff-take")).Error);
        Assert.Equal(1_500, (await wallet.BalanceAsync(account)).Balance);
    }

    [Fact]
    public async Task Invalid_amounts_and_reasons_are_refused()
    {
        var wallet = new MemoryQunaWallet();
        var account = Guid.NewGuid();
        Assert.Equal(QunaWalletRules.InvalidAmount, (await wallet.GrantAsync(new QunaRequest(account, "a", 0, QunaWalletRules.Grant))).Error);
        Assert.Equal(QunaWalletRules.InvalidAmount, (await wallet.SpendAsync(new QunaRequest(account, "b", -5, QunaWalletRules.Purchase))).Error);
        Assert.Equal(QunaWalletRules.InvalidReason, (await wallet.SpendAsync(new QunaRequest(account, "c", 5, QunaWalletRules.Grant))).Error);
        Assert.Equal(QunaWalletRules.InvalidReason, (await wallet.GrantAsync(new QunaRequest(account, "d", 5, QunaWalletRules.Purchase))).Error);
        Assert.Empty(wallet.Ledger);
    }

    // ---------- The world session ----------

    [Fact]
    public void RefreshCurrency_packet_uses_ecurrency_category_game_cash_map_key()
    {
        var session = new GameSession(GameData.Embedded, new GameSessionOptions(), new Random(1));
        var packet = session.RefreshCurrency(GameSession.QunaBmCurrencyGroupId, (GameSession.GameCashCurrencyCategory, 50_000));
        Assert.Equal(GameSession.RefreshCurrencyNt, Op(packet));
        Assert.Equal(Convert.FromHexString("91000000010700000050C3000000000000"), packet.AsSpan(2).ToArray());
    }

    [Fact]
    public async Task A_stored_quna_stack_is_neither_credited_nor_shown_by_the_world()
    {
        // Only the realm adds Quna: a world that finds a stack leaves it for migrate-quna.
        var (session, store, _) = await EnterWithQunaAsync(50_000, asItem: true);
        await session.InventoryChangesAsync(CancellationToken.None);
        Assert.Empty(store.Quna.Ledger);
        Assert.Equal(0, store.QunaBalance);
        Assert.Equal(0, session.QunaBalance());
        Assert.Null(VirtualStack(session));
        Assert.Contains(store.Items, i => i.TemplateId == QunaItem && i.Quantity == 50_000);
    }

    [Fact]
    public async Task World_entry_shows_the_balance_as_a_virtual_bag_stack_without_storing_it()
    {
        var (session, store, entry) = await EnterWithQunaAsync(50_000, asItem: false);
        var depot = entry.First(p => Op(p) == 0x5611);
        Assert.Contains("A607E81F", Convert.ToHexString(depot), StringComparison.OrdinalIgnoreCase);
        var stack = VirtualStack(session);
        Assert.NotNull(stack);
        Assert.Equal((GameSession.VirtualQunaKey, 50_000L, InventoryItem.Bag), (stack!.Key, stack.Count, stack.Container));
        Assert.DoesNotContain(store.Items, i => i.TemplateId == QunaItem);
        Assert.DoesNotContain(entry, p => Op(p) == GameSession.RefreshCurrencyNt);
    }

    [Fact]
    public async Task No_virtual_stack_without_quna()
    {
        var (session, _, _) = await EnterWithQunaAsync(0, asItem: false);
        Assert.Null(VirtualStack(session));
        Assert.Equal(0, session.QunaBalance());
    }

    [Fact]
    public async Task Inventory_list_resyncs_game_cash_refresh_after_depot()
    {
        var (session, _, _) = await EnterWithQunaAsync(50_000, asItem: false);
        var depot = await session.HandleAsync(new PacketWriter().U16(0x5610).ToArray(), LiveNow + 1);
        Assert.Equal(50_000L, RefreshedBalance(depot));
    }

    [Fact]
    public async Task A_purchase_spends_from_the_wallet_and_updates_the_virtual_stack()
    {
        var (session, store, _) = await EnterWithQunaAsync(50_000, asItem: false);
        var bought = await session.HandleAsync(BuyPass(), LiveNow + 5);
        Assert.Equal(0, BitConverter.ToUInt16(bought[0], 2));
        var left = 50_000L - GameSession.PaidPassUnlockQunaCost;
        Assert.Equal(left, RefreshedBalance(bought));
        Assert.Equal(left, session.QunaBalance());
        Assert.Equal(left, store.QunaBalance);
        Assert.Equal(left, VirtualStack(session)!.Count);
        Assert.Contains(bought, p => Op(p) == 0x561b);
        var spend = store.Quna.Ledger[^1];
        Assert.Equal((QunaWalletRules.Purchase, -GameSession.PaidPassUnlockQunaCost, (int?)store.WorldId), (spend.Reason, spend.Delta, spend.WorldId));
        Assert.Equal(store.States[0].Summary.CharacterId, spend.CharacterId);
        // The world's purchase ledger still records the line in Quna; the bag was not touched.
        Assert.Equal(QunaItem, store.Orders.Single().CurrencyItemId);
        Assert.DoesNotContain(store.Items, i => i.TemplateId == QunaItem);
    }

    [Fact]
    public async Task Premium_pass_purchase_grants_immediate_paid_track_reward()
    {
        var (session, store, _) = await EnterWithQunaAsync(50_000);
        var bought = await session.HandleAsync(BuyPass(), LiveNow + 5);
        Assert.Equal(0, BitConverter.ToUInt16(bought[0], 2));
        var paidReward = AchievementTables.Embedded.Of(810434000)!.StepRewards(1)[0];
        Assert.Contains(store.Items, i => i.TemplateId == paidReward.ItemId && i.Quantity == paidReward.Count);
    }

    [Fact]
    public async Task Premium_pass_refused_without_enough_quna()
    {
        var (session, store, _) = await EnterWithQunaAsync(100);
        var bought = await session.HandleAsync(BuyPass(), LiveNow + 5);
        Assert.Equal(2, BitConverter.ToUInt16(bought[0], 2));
        Assert.DoesNotContain(store.Quna.Ledger, e => e.Reason == QunaWalletRules.Purchase);
    }

    [Fact]
    public async Task The_realm_refuses_a_spend_another_world_already_took()
    {
        var (session, store, _) = await EnterWithQunaAsync(2_000, asItem: false);
        // Spent on another world: this session still believes 2,000.
        await store.Quna.SpendAsync(new QunaRequest(store.AccountId, "elsewhere", 1_000, QunaWalletRules.Purchase, 1205));
        var bought = await session.HandleAsync(BuyPass(), LiveNow + 5);
        Assert.Equal(2, BitConverter.ToUInt16(bought[0], 2));
        Assert.Equal(1_000, session.QunaBalance());
        Assert.Empty(store.Orders);
    }

    [Fact]
    public async Task A_failed_purchase_gives_the_quna_back()
    {
        var (session, store, _) = await EnterWithQunaAsync(5_000, asItem: false);
        store.FailShopBuy = true;
        var bought = await session.HandleAsync(BuyPass(), LiveNow + 5);
        Assert.Equal(1, BitConverter.ToUInt16(bought[0], 2));
        Assert.Equal(5_000, store.QunaBalance);
        Assert.Equal(5_000, session.QunaBalance());
        var (spend, refund) = (store.Quna.Ledger[^2], store.Quna.Ledger[^1]);
        Assert.Equal(QunaWalletRules.Purchase, spend.Reason);
        Assert.Equal((QunaWalletRules.Refund, QunaWalletRules.RefundId(spend.RequestId)), (refund.Reason, refund.RequestId));
    }

    [Fact]
    public async Task A_pushed_balance_updates_the_virtual_stack_and_an_older_one_is_ignored()
    {
        var (session, store, _) = await EnterWithQunaAsync(5_000, asItem: false);
        // A staff grant on the realm (or a purchase on another world): the realm pushes the new balance.
        var granted = await store.Quna.GrantAsync(new QunaRequest(store.AccountId, "staff-1", 2_500, QunaWalletRules.Staff));
        var pushed = await session.QunaPushedAsync(granted.Balance, granted.EntryId);
        Assert.Equal(7_500, RefreshedBalance(pushed));
        Assert.Equal(7_500, VirtualStack(session)!.Count);
        Assert.Contains(pushed, p => Op(p) == 0x561b);
        // A push that arrives late (an older ledger entry) changes nothing.
        Assert.Empty(await session.QunaPushedAsync(5_000, granted.EntryId - 1));
        Assert.Equal(7_500, session.QunaBalance());
        // The same balance again sends nothing new.
        Assert.Empty(await session.QunaPushedAsync(granted.Balance, granted.EntryId));
    }

    [Fact]
    public async Task A_push_down_to_zero_removes_the_virtual_stack()
    {
        var (session, store, _) = await EnterWithQunaAsync(1_500, asItem: false);
        var spent = await store.Quna.SpendAsync(new QunaRequest(store.AccountId, "elsewhere", 1_500, QunaWalletRules.Purchase, 1206));
        var pushed = await session.QunaPushedAsync(spent.Balance, spent.EntryId);
        Assert.Equal(0, RefreshedBalance(pushed));
        Assert.Null(VirtualStack(session));
    }

    [Fact]
    public async Task A_gm_grant_of_quna_is_refused_in_the_world()
    {
        var (session, store, _) = await EnterWithQunaAsync(0, asItem: false);
        var granted = session.GrantQuna(100_000);
        Assert.False(granted!.Value.Ok);
        Assert.Contains("dashboard", granted.Value.Message);
        Assert.Empty(granted.Value.Packets);
        Assert.DoesNotContain(store.Quna.Ledger, e => e.Reason == QunaWalletRules.Staff);
        Assert.Equal(0, store.QunaBalance);
        Assert.DoesNotContain(store.Items, i => i.TemplateId == QunaItem);
    }

    [Fact]
    public async Task Without_a_wallet_quna_stays_a_bag_item()
    {
        GameSession.ConfigureShopOptions(new ShopOptions { PremiumMode = ShopPremiumMode.Quna });
        var store = new FakeCharacterStore(Guid.NewGuid()) { UseQunaWallet = false };
        store.Items.Add(FakeCharacterStore.BagItem(QunaItem, 5_000, 9001));
        var session = new GameSession(GameData.Embedded, new GameSessionOptions { CharacterLevel = 45, WorldId = store.WorldId }, new Random(3)) { Store = store };
        await session.HandleAsync(Login(), LiveNow);
        await session.HandleAsync(LightCreate(), LiveNow);
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)store.States[0].WireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), LiveNow);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), LiveNow);
        Assert.Equal(5_000, session.QunaBalance());
        Assert.Null(session.GrantQuna(1));
        var bought = await session.HandleAsync(BuyPass(), LiveNow + 5);
        Assert.Equal(0, BitConverter.ToUInt16(bought[0], 2));
        Assert.Equal(5_000 - GameSession.PaidPassUnlockQunaCost, store.Items.Single(i => i.TemplateId == QunaItem).Quantity);
        Assert.Empty(store.Quna.Ledger);
    }
}
