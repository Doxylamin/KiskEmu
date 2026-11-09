using Aion.Application.Gameplay;

namespace Aion.Host.Worlds;

/// <summary>
/// The auth host's Quna wallet (the realm database's, <see cref="Aion.Infrastructure.Postgres.PostgresQunaWallet"/>): after
/// every applied change (not a repeat) the new balance goes to the world one of the account's characters is in (the
/// presence the worlds push over their realm sockets), over that world's socket or HTTP. The push does not hold up the
/// answer; a world that misses one gets the balance with its next spend or at the next world entry.
/// </summary>
public sealed class PushingQunaWallet(IQunaWallet inner, Func<Guid, int?> worldOf, Func<int, QunaBalancePush, Task<bool>> push,
    ILogger<PushingQunaWallet> logger) : IQunaWallet
{
    public PushingQunaWallet(IQunaWallet inner, WorldPresenceMirror presence, WorldLinks links, ILogger<PushingQunaWallet> logger)
        : this(inner, presence.WorldOfAccount, (world, change) => links.QunaBalanceAsync(world, change, CancellationToken.None), logger) { }

    /// <summary>The last push started (tests wait on it).</summary>
    public Task LastPush { get; private set; } = Task.CompletedTask;

    public Task<QunaResult> BalanceAsync(Guid account, CancellationToken ct = default) => inner.BalanceAsync(account, ct);

    public async Task<QunaResult> SpendAsync(QunaRequest request, CancellationToken ct = default) =>
        Pushed(request.AccountId, await inner.SpendAsync(request, ct));

    public async Task<QunaResult> GrantAsync(QunaRequest request, CancellationToken ct = default) =>
        Pushed(request.AccountId, await inner.GrantAsync(request, ct));

    public async Task<QunaResult> RefundAsync(Guid account, string spendRequestId, int? worldId = null, CancellationToken ct = default) =>
        Pushed(account, await inner.RefundAsync(account, spendRequestId, worldId, ct));

    QunaResult Pushed(Guid account, QunaResult result)
    {
        if (result is { Ok: true, Repeated: false } && worldOf(account) is { } world)
            LastPush = PushAsync(world, new QunaBalancePush(account, result.Balance, result.EntryId));
        return result;
    }

    async Task PushAsync(int world, QunaBalancePush change)
    {
        try
        {
            if (!await push(world, change))
                logger.LogInformation("Quna balance of account {Account} not pushed to world {World} (not reachable)", change.AccountId, world);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            logger.LogWarning("Quna balance push to world {World} failed: {Error}", world, error.Message);
        }
    }
}
