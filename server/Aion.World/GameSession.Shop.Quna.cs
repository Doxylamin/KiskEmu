using System.Text.Json;
using Aion.Application.Gameplay;
using Aion.Protocol.Native;

namespace Aion.World;

// Quna (GameCash) is the account's wallet in the realm (server/DATABASES.md, migration 095), not an item: every spend and
// refund goes to Store.QunaWallet; a world never adds Quna (credits are the realm's). The client still expects it as a bag stack of item 535300006 on 0x5611 and in the shop
// packets, so the bag shows a virtual stack (never stored) whose count is the wallet balance, refreshed after a spend or
// refund and when the realm pushes a change (/internal/world/quna/balance). Without a wallet (development logins) Quna stays
// a plain bag item as before.
// Kinah is bag item 930100001 on 0x5611 (GoldCombined). GNB slot wiring for Quna is unverified — see tools/data/currency_hud.json.
// HTTP GET virtual_currency/v2.0/me/balances uses currency_id 109 with currency_group_id 145 (GameCash), 110 with 158 (Mileage).
// RefreshCurrency_NT (0x3bd1) map keys in usmap are ECurrencyCategory (7 GameCash, 6 Mileage), not the HTTP currency_id field.
public sealed partial class GameSession
{
    public const ushort RefreshCurrencyNt = 0x3bd1;

    public const int QunaBmCurrencyGroupId = 145;
    public const int QunaVirtualCurrencyId = 109;
    public const int MileageCurrencyGroupId = 158;
    public const int MileageVirtualCurrencyId = 110;
    public const int GameCashCurrencyCategory = 7;
    public const int MileageCurrencyCategory = 6;
    public const int GoldCombinedCurrencyCategory = 93;

    /// <summary>The bag key of the virtual Quna stack (no stored item has it: wire ids are small sequence numbers).</summary>
    public const long VirtualQunaKey = 0x7F00_0000_1FE8_07A6;
    static readonly Guid VirtualQunaInstance = new("00000000-0000-0000-0000-00001fe807a6");

    long accountQunaBalance;
    // The ledger entry of accountQunaBalance: a push or answer with an older entry is stale and ignored.
    long qunaEntry = -1;
    // The balance the client was last shown (depot list or RefreshCurrency_NT); null before the first inventory load.
    long? shownQuna;

    /// <summary>Quna is the realm's account wallet (an account login on a server with the realm link or realm database).</summary>
    bool QunaInWallet => Store?.QunaWallet is not null;

    public long QunaBalance() => QunaInWallet ? accountQunaBalance : QunaIn(inventory.Values);

    static long QunaIn(IEnumerable<InventoryItem> items) =>
        items.Where(i => i.ItemId == PlatformGoodsTables.Embedded.QunaItemId && i.Container == InventoryItem.Bag).Sum(i => i.Count);

    static bool IsQunaRow(ItemState row) => row.TemplateId == PlatformGoodsTables.Embedded.QunaItemId;

    /// <summary>Takes a wallet answer's balance unless a newer one (higher ledger entry) is already known.</summary>
    void NoteQuna(QunaResult result)
    {
        if (result.EntryId < qunaEntry) return;
        accountQunaBalance = result.Balance;
        qunaEntry = result.EntryId;
    }

    /// <summary>Reads the wallet balance (the first inventory load, and when the client asks for its inventory).</summary>
    async Task RefreshAccountQunaBalanceAsync(CancellationToken ct)
    {
        if (Store is not { QunaWallet: { } wallet } store) return;
        try { NoteQuna(await wallet.BalanceAsync(store.AccountId, ct)); }
        catch (Exception error) when (error is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // The realm is not reachable: the last known balance stays (spends still ask the realm, a push or the next
            // inventory request reads it again); not asked again on every inventory change meanwhile.
            PersistenceErrors.Add("quna_balance");
            qunaEntry = Math.Max(qunaEntry, 0);
        }
    }

    /// <summary>The stored rows plus the virtual Quna stack (when Quna is the wallet's and the balance is above 0).</summary>
    List<ItemState> WithVirtualQuna(IEnumerable<ItemState> rows)
    {
        var list = QunaInWallet ? rows.Where(r => !IsQunaRow(r)).ToList() : rows.ToList();
        if (QunaInWallet && accountQunaBalance > 0)
        {
            var empty = JsonSerializer.SerializeToElement(new { });
            list.Add(new ItemState(VirtualQunaInstance, "", PlatformGoodsTables.Embedded.QunaItemId, accountQunaBalance, "bag", null, 0, empty, empty, VirtualQunaKey));
        }
        return list;
    }

    public byte[] RefreshCurrency(int currencyGroupId, params (int MapKey, long Balance)[] entries)
    {
        var w = PacketWriter.Opcode(RefreshCurrencyNt).I32(currencyGroupId).Varint((uint)entries.Length);
        foreach (var (key, balance) in entries) w.I32(key).I64(balance);
        return w.ToArray();
    }

    byte[] RefreshGameCash(long balance) =>
        RefreshCurrency(QunaBmCurrencyGroupId, (GameCashCurrencyCategory, balance));

    byte[] RefreshMileage(long balance = 0) =>
        RefreshCurrency(MileageCurrencyGroupId, (MileageCurrencyCategory, balance));

    public IEnumerable<byte[]> InventoryDepotSyncPackets()
    {
        if (Store is null || CharacterId is null) yield break;
        var level = EquipItemLevel();
        yield return GamePackets.ItemDepotList([.. inventory.Values]);
        yield return GamePackets.ItemEquipLevelChange(level, level);
    }

    public IEnumerable<byte[]> QunaRefreshPackets()
    {
        foreach (var packet in InventoryDepotSyncPackets()) yield return packet;
        yield return QunaRefreshNow();
        yield return RefreshMileage();
    }

    /// <summary>RefreshCurrency_NT when the balance differs from what the client was last shown.</summary>
    List<byte[]> QunaRefreshIfChanged()
    {
        var balance = QunaBalance();
        if (shownQuna == balance) return [];
        shownQuna = balance;
        return [RefreshGameCash(balance)];
    }

    public byte[] QunaRefreshNow()
    {
        shownQuna = QunaBalance();
        return RefreshGameCash(shownQuna.Value);
    }

    public string WireAccountId => loginIdentity.AccountSid;

    /// <summary>The items as the client was last shown them (tests).</summary>
    internal IReadOnlyCollection<InventoryItem> ShownItems => inventory.Values;

    /// <summary>
    /// The realm pushed this account's balance (its wallet changed: a purchase on this or another world, a staff grant):
    /// the virtual stack and RefreshCurrency_NT follow when it is newer than the balance known here.
    /// </summary>
    public async Task<List<byte[]>> QunaPushedAsync(long balance, long entryId, CancellationToken ct = default)
    {
        if (!QunaInWallet || entryId < qunaEntry) return [];
        accountQunaBalance = balance;
        qunaEntry = entryId;
        return CharacterId is null ? [] : await InventoryChangesAsync(ct);
    }

    enum QunaPay { Done, NotEnough, Failed }

    /// <summary>A store call through <see cref="Stored"/>: false when it refused or failed.</summary>
    async Task<bool> StoredOk(Func<Task<bool>> action) => (await Stored(async () => await action() ? [[]] : [])).Count > 0;

    /// <summary>
    /// A purchase's costs: the bag stacks of every currency but Quna, and the Quna to take from the wallet (Quna is a bag
    /// stack only without a wallet). Null when the bag cannot cover the other currencies.
    /// </summary>
    (List<(Guid Item, long Revision, int Count)> Costs, long Quna)? PurchaseCosts(IEnumerable<(int Currency, long Price)> prices)
    {
        var costs = new List<(Guid, long, int)>();
        var quna = 0L;
        foreach (var (currency, price) in prices)
        {
            if (price <= 0) continue;
            if (QunaInWallet && currency == PlatformGoodsTables.Embedded.QunaItemId)
            {
                if (!PacketSecurity.TryAdd(quna, price, out quna)) return null;
                continue;
            }
            if (CostStacks(currency, price) is not { } stacks) return null;
            costs.AddRange(stacks);
        }
        // Not checked against the balance known here: the realm decides (this world may not have heard of a grant yet).
        return (costs, quna);
    }

    /// <summary>
    /// Takes <paramref name="quna"/> from the account wallet, then runs the purchase's own (world database) transaction;
    /// when that fails the Quna is given back (a refund of the same request). Without Quna to take, only the purchase runs.
    /// </summary>
    async Task<QunaPay> PayQunaAsync(long quna, object details, Func<Task<bool>> purchase, CancellationToken ct)
    {
        if (quna <= 0) return await purchase() ? QunaPay.Done : QunaPay.Failed;
        if (Store is not { QunaWallet: { } wallet } store || CharacterId is not { } character) return QunaPay.Failed;
        var request = $"w{store.WorldId}:{character:N}:{Guid.NewGuid():N}";
        QunaResult spent;
        try
        {
            spent = await wallet.SpendAsync(new QunaRequest(store.AccountId, request, quna, QunaWalletRules.Purchase, store.WorldId, character,
                JsonSerializer.SerializeToElement(details)), ct);
        }
        catch (Exception error) when (error is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // The spend may have reached the realm before the link failed: give it back (nothing happens when it did not).
            PersistenceErrors.Add("quna_spend");
            await RefundQunaAsync(wallet, store.AccountId, request, store.WorldId, ct);
            return QunaPay.Failed;
        }
        NoteQuna(spent);
        if (!spent.Ok) return spent.Error == QunaWalletRules.Insufficient ? QunaPay.NotEnough : QunaPay.Failed;
        DebugShop($"Quna spent: {quna} (request {request}, balance {spent.Balance})");
        var done = false;
        try { done = await purchase(); }
        finally
        {
            if (!done) await RefundQunaAsync(wallet, store.AccountId, request, store.WorldId, ct);
        }
        return done ? QunaPay.Done : QunaPay.Failed;
    }

    async Task RefundQunaAsync(IQunaWallet wallet, Guid account, string request, int world, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                var refunded = await wallet.RefundAsync(account, request, world, CancellationToken.None);
                NoteQuna(refunded);
                if (refunded.Ok) DebugShop($"Quna refunded: request {request}, balance {refunded.Balance}");
                return;
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                // The ledger keeps the spend (its request id) for staff to refund by hand.
                if (attempt == 1) PersistenceErrors.Add("quna_refund");
            }
        }
    }

    /// <summary>
    /// The GM command's Quna grant: refused where Quna is the wallet's, since only the realm adds Quna (the staff dashboard's
    /// account page). Null without a wallet (development logins: the caller grants the item).
    /// </summary>
    public (bool Ok, string Message, List<byte[]> Packets)? GrantQuna(long amount) =>
        QunaInWallet ? (false, "Quna is added on the staff dashboard (a character's account), not in a world.", []) : null;
}
