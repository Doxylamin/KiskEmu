using System.Globalization;
using System.Net;
using System.Text.Json;
using Aion.Application.Authentication;
using Aion.Application.Gameplay;
using Aion.World;

namespace Aion.Host.Platform;

/// <summary>
/// The account's GameCash (Quna) for platform GET virtual_currency/v2.0/me/balances: the realm's account wallet
/// (server/DATABASES.md). Without a wallet (development without accounts) the one online session's balance.
/// </summary>
public sealed class ShopQunaBalanceService(
    WorldPresence presence,
    IPlatformSecondaryAuth? platformAuth = null,
    IQunaWallet? wallet = null)
{
    public async Task<long> BalanceAsync(IPAddress? clientAddress, string? platformSessionToken, string? wireAccountId, CancellationToken ct)
    {
        if (await ResolveAccountIdAsync(clientAddress, platformSessionToken, wireAccountId, ct) is not { } accountId)
            return 0;
        if (wallet is not null)
            return (await wallet.BalanceAsync(accountId, ct)).Balance;
        return OnlineSessionQuna(accountId);
    }

    long OnlineSessionQuna(Guid accountId)
    {
        var online = presence.All.Where(e => e.AccountId == accountId || e.Session.Store?.AccountId == accountId).ToList();
        if (online.Count == 1) return online[0].Session.QunaBalance();
        return 0;
    }

    async Task<Guid?> ResolveAccountIdAsync(IPAddress? clientAddress, string? platformSessionToken, string? wireAccountId, CancellationToken ct)
    {
        // Only an authenticated platform client (its session or the address its launch was bound to) names the account.
        // The game_account_id query is unauthenticated and sequential: trusting it let anyone read any account's balance
        // and bind their own address to that account for the platform OTP routes. It is used only when no platform auth
        // is configured (local development), and then only for an account that is online right now.
        if (platformAuth is not null)
            return await platformAuth.ResolveAccountAsync(clientAddress, platformSessionToken, ct);
        if (wireAccountId is not null && SessionByWireAccount(wireAccountId)?.Store?.AccountId is { } fromOnline)
            return fromOnline;
        return null;
    }

    GameSession? SessionByWireAccount(string wireAccountId) =>
        presence.All.FirstOrDefault(e => string.Equals(e.Session.WireAccountId, wireAccountId, StringComparison.OrdinalIgnoreCase))?.Session;

    /// <summary>
    /// ECurrencyCategory GameCashPending: the entry of the GameCash answer the client's Quna counter shows (the top-left HUD
    /// slot and Currency Info; GnbCurrencyTableItem's first slot). OBSERVED 2026-11-10 on a test server: of entries with
    /// currency_id 7, 95, 96, 102, 103 and 109 in one answer, the counter showed exactly the 103 amount and ignored the
    /// rest; with only 109 it stayed 0. The official game connection never carries the balance (a purchase on 2026-11-04
    /// delivered a Quna Recharge item 535300006 that the server removed at once, and no message held the amount), so the
    /// counter comes from this answer alone, read at login.
    /// </summary>
    public const int GameCashPendingCurrencyId = 103;

    /// <summary>Official balance_info_list shape for one currency_group_id query (145 GameCash Quna, 158 mileage).</summary>
    public static byte[] JsonBody(long qunaInBag, int currencyGroupId) => currencyGroupId switch
    {
        // 109 for the shop's checks as before, 103 for the Quna counter (see GameCashPendingCurrencyId); the total once.
        GameSession.QunaBmCurrencyGroupId => JsonBodyFor(currencyGroupId, [GameSession.QunaVirtualCurrencyId, GameCashPendingCurrencyId], qunaInBag),
        GameSession.MileageCurrencyGroupId => JsonBodyFor(currencyGroupId, [GameSession.MileageVirtualCurrencyId], 0L),
        _ => JsonBodyFor(currencyGroupId, [GameSession.QunaVirtualCurrencyId], 0L),
    };

    public static byte[] JsonBodyFor(int currencyGroupId, int currencyId, long balance) => JsonBodyFor(currencyGroupId, [currencyId], balance);

    /// <summary>One balance_info_list entry per currency id, each with the balance; total_balance is the balance once.</summary>
    public static byte[] JsonBodyFor(int currencyGroupId, IReadOnlyList<int> currencyIds, long balance)
    {
        var amount = balance.ToString("F6", CultureInfo.InvariantCulture);
        var zero = "0.000000";
        var group = currencyGroupId.ToString(CultureInfo.InvariantCulture);
        return JsonSerializer.SerializeToUtf8Bytes(new
        {
            balance_info_list = currencyIds.Select(id => new
            {
                currency_id = id.ToString(CultureInfo.InvariantCulture),
                currency_group_id = group,
                balance = amount,
                pending_balance = zero,
            }).ToArray(),
            balance_info_list_count = currencyIds.Count.ToString(CultureInfo.InvariantCulture),
            total_pending_balance = zero,
            total_balance = amount,
        }, PlatformResponses.Plain);
    }
}
