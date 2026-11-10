using System.Net;
using System.Text.Json;
using Aion.Application.Authentication;
using Aion.Application.Gameplay;
using Aion.Host.Platform;
using Aion.Protocol.Native;
using Aion.World;
using Aion.World.Tests;
using Xunit;

namespace Aion.Host.Tests;

public sealed class PlatformVirtualCurrencyTests
{
    [Fact]
    public void Balance_json_group_145_uses_quna_virtual_currency_id_and_string_amounts()
    {
        using var doc = JsonDocument.Parse(ShopQunaBalanceService.JsonBody(50_000, GameSession.QunaBmCurrencyGroupId));
        var root = doc.RootElement;
        Assert.Equal("2", root.GetProperty("balance_info_list_count").GetString());
        Assert.Equal("50000.000000", root.GetProperty("total_balance").GetString());
        var row = root.GetProperty("balance_info_list")[0];
        Assert.Equal(GameSession.QunaBmCurrencyGroupId.ToString(), row.GetProperty("currency_group_id").GetString());
        Assert.Equal(GameSession.QunaVirtualCurrencyId.ToString(), row.GetProperty("currency_id").GetString());
        Assert.Equal("50000.000000", row.GetProperty("balance").GetString());
        // The Quna counter (top left, Currency Info) shows the GameCashPending entry only.
        var counter = root.GetProperty("balance_info_list")[1];
        Assert.Equal("103", counter.GetProperty("currency_id").GetString());
        Assert.Equal(GameSession.QunaBmCurrencyGroupId.ToString(), counter.GetProperty("currency_group_id").GetString());
        Assert.Equal("50000.000000", counter.GetProperty("balance").GetString());
    }

    [Fact]
    public void Balance_json_group_158_returns_mileage_currency_id_and_zero()
    {
        using var doc = JsonDocument.Parse(ShopQunaBalanceService.JsonBody(50_000, GameSession.MileageCurrencyGroupId));
        var row = doc.RootElement.GetProperty("balance_info_list")[0];
        Assert.Equal(GameSession.MileageCurrencyGroupId.ToString(), row.GetProperty("currency_group_id").GetString());
        Assert.Equal(GameSession.MileageVirtualCurrencyId.ToString(), row.GetProperty("currency_id").GetString());
        Assert.Equal("0.000000", row.GetProperty("balance").GetString());
        Assert.Equal("0.000000", doc.RootElement.GetProperty("total_balance").GetString());
    }

    [Fact]
    public void Me_country_uses_geolocation_fixture()
    {
        var geo = """{"geolocation":"NO","coordinates":{"latitude":"59.9139","longitude":"10.7522"}}"""u8.ToArray();
        using var doc = JsonDocument.Parse(PlatformApiGateResponses.MeCountry(geo));
        Assert.Equal("NO", doc.RootElement.GetProperty("country_code").GetString());
    }

    [Fact]
    public void Me_country_defaults_when_geolocation_missing()
    {
        using var doc = JsonDocument.Parse(PlatformApiGateResponses.MeCountry(null));
        Assert.Equal("US", doc.RootElement.GetProperty("country_code").GetString());
    }

    [Fact]
    public async Task Balance_returns_zero_when_account_unknown()
    {
        var wallet = new MemoryQunaWallet();
        wallet.Seed(Guid.NewGuid(), 50_000);
        var svc = new ShopQunaBalanceService(new WorldPresence(), platformAuth: null, wallet: wallet);
        Assert.Equal(0, await svc.BalanceAsync(IPAddress.Loopback, null, "9999999", default));
    }

    [Fact]
    public async Task Balance_ignores_the_unauthenticated_wire_game_account_id()
    {
        // The wire game_account_id is sequential and unauthenticated: with platform auth configured it names no account.
        var account = Guid.NewGuid();
        var wallet = new MemoryQunaWallet();
        wallet.Seed(account, 50_000);
        var svc = new ShopQunaBalanceService(new WorldPresence(), new WireOnlyPlatformAuth(account), wallet);
        Assert.Equal(0, await svc.BalanceAsync(IPAddress.Loopback, platformSessionToken: null, wireAccountId: "1000001", default));
    }

    [Fact]
    public async Task Platform_balance_is_the_account_wallet_before_world_entry()
    {
        // virtual_currency/v2.0/me/balances reads the realm's wallet directly: no world server is asked.
        var account = Guid.NewGuid();
        var wallet = new MemoryQunaWallet();
        wallet.Seed(account, 50_000);
        await wallet.SpendAsync(new QunaRequest(account, "spent-elsewhere", 1_500, QunaWalletRules.Purchase, WorldId: 1205));
        var svc = new ShopQunaBalanceService(new WorldPresence(), new WireOnlyPlatformAuth(account, "session"), wallet);
        var balance = await svc.BalanceAsync(IPAddress.Loopback, platformSessionToken: "session", wireAccountId: "1000001", default);
        Assert.Equal(48_500, balance);
        using var doc = JsonDocument.Parse(ShopQunaBalanceService.JsonBody(balance, GameSession.QunaBmCurrencyGroupId));
        Assert.Equal("48500.000000", doc.RootElement.GetProperty("total_balance").GetString());
    }

    sealed class WireOnlyPlatformAuth(Guid accountId, string? session = null) : IPlatformSecondaryAuth
    {
        public Task RememberClientAsync(IPAddress? address, Guid accountId, CancellationToken ct = default) => Task.CompletedTask;
        public Task<Guid?> ResolveAccountAsync(IPAddress? address, string? platformAccessToken, CancellationToken ct) =>
            Task.FromResult<Guid?>(session is not null && platformAccessToken == session ? accountId : null);
        public Task<Guid?> ResolveAccountByWireIdAsync(string wireAccountId, CancellationToken ct) =>
            wireAccountId == "1000001" ? Task.FromResult<Guid?>(accountId) : Task.FromResult<Guid?>(null);
        public Task<byte[]?> AuthTypesAsync(Guid accountId, CancellationToken ct) => Task.FromResult<byte[]?>(null);
        public Task<byte[]?> StartAsync(Guid accountId, string? audienceAppId, CancellationToken ct) => Task.FromResult<byte[]?>(null);
        public Task<byte[]?> FinishAsync(Guid accountId, string sessionId, string? code, string? recoveryCode, CancellationToken ct) =>
            Task.FromResult<byte[]?>(null);
        public Task<bool> IsVerifiedAsync(Guid accountId, string platformAccessToken, CancellationToken ct) => Task.FromResult(false);
        public Task<bool> HasRecentVerificationAsync(Guid accountId, CancellationToken ct) => Task.FromResult(false);
    }
}
