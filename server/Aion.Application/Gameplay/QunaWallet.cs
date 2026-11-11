using System.Text.Json;

namespace Aion.Application.Gameplay;

/// <summary>
/// The account's Quna (NC GameCash, the paid currency): one wallet per account in the realm database with an append-only
/// ledger (migration 095, server/DATABASES.md). Every change names a request id; the same id again returns the first
/// result without changing anything, so a caller may repeat a call it lost the answer to. A debit the balance cannot
/// cover is refused and leaves nothing behind.
/// </summary>
public interface IQunaWallet
{
    /// <summary>The balance and the account's last ledger entry (0 when none).</summary>
    Task<QunaResult> BalanceAsync(Guid account, CancellationToken ct = default);

    /// <summary>Takes <see cref="QunaRequest.Amount"/>; refused ("insufficient") when the balance is lower.</summary>
    Task<QunaResult> SpendAsync(QunaRequest request, CancellationToken ct = default);

    /// <summary>Adds <see cref="QunaRequest.Amount"/> (reason grant, staff or migration). The realm's only: a world server's
    /// wallet refuses it (<see cref="QunaWalletRules.RealmOnly"/>).</summary>
    Task<QunaResult> GrantAsync(QunaRequest request, CancellationToken ct = default);

    /// <summary>Gives back a spend (its request id); the refund's own request id is "refund:" + that id.</summary>
    Task<QunaResult> RefundAsync(Guid account, string spendRequestId, int? worldId = null, CancellationToken ct = default);
}

/// <param name="Amount">Positive; the ledger stores a spend as a negative delta.</param>
/// <param name="Details">What the change was for (goods, pass, staff member); kept in the ledger as JSON.</param>
public sealed record QunaRequest(Guid AccountId, string RequestId, long Amount, string Reason, int? WorldId = null, Guid? CharacterId = null,
    JsonElement? Details = null);

/// <param name="Ok">False when refused (<paramref name="Error"/>); <paramref name="Balance"/> is then the current balance.</param>
/// <param name="EntryId">The ledger entry of this change (the account's last entry for a balance read); pushes and
/// answers carry it so a world keeps the newest balance when they arrive out of order.</param>
/// <param name="Repeated">The request id was already applied: this is that first result.</param>
public sealed record QunaResult(bool Ok, long Balance, long EntryId = 0, string? Error = null, bool Repeated = false);

public sealed record QunaLedgerEntry(long EntryId, Guid AccountId, long Delta, long BalanceAfter, string Reason, string RequestId,
    int? WorldId, Guid? CharacterId, JsonElement? Details, DateTimeOffset At);

public static class QunaWalletRules
{
    public const string Purchase = "purchase", Grant = "grant", Refund = "refund", Staff = "staff", Migration = "migration";
    public static readonly IReadOnlyList<string> Reasons = [Purchase, Grant, Refund, Staff, Migration];
    public const string Insufficient = "insufficient", InvalidAmount = "invalid_amount", InvalidReason = "invalid_reason",
        InvalidRequest = "invalid_request", RequestConflict = "request_conflict", NotFound = "not_found",
        // Credits are the realm's (the staff dashboard's account page, later the web shop): a world server cannot add Quna.
        RealmOnly = "realm_only";
    /// <summary>The Quna item (the client's bag stack of it is virtual where Quna is the wallet's).</summary>
    public const int ItemId = 535300006;
    /// <summary>Upper bound of one change (the ledger's CHECK).</summary>
    public const long MaxAmount = 100_000_000_000;

    public static string RefundId(string spendRequestId) => "refund:" + spendRequestId;

    /// <summary>The refusal of a malformed spend (debit) or credit, else null.</summary>
    public static string? Invalid(QunaRequest request, bool debit)
    {
        if (request.AccountId == Guid.Empty || string.IsNullOrWhiteSpace(request.RequestId) || request.RequestId.Length > 200) return InvalidRequest;
        if (request.Amount is <= 0 or > MaxAmount) return InvalidAmount;
        var allowed = debit ? request.Reason is Purchase or Staff : request.Reason is Grant or Staff or Migration;
        return allowed ? null : InvalidReason;
    }
}

/// <summary>The wallet in memory, with the realm database's semantics (tests and development without a realm).</summary>
public sealed class MemoryQunaWallet : IQunaWallet
{
    readonly object gate = new();
    readonly Dictionary<Guid, (long Balance, long Last)> wallets = [];
    readonly List<QunaLedgerEntry> ledger = [];

    public IReadOnlyList<QunaLedgerEntry> Ledger { get { lock (gate) return [.. ledger]; } }
    /// <summary>Called after each applied change (not a repeat): account, balance, entry (the realm's push).</summary>
    public event Action<Guid, long, long>? Changed;

    public Task<QunaResult> BalanceAsync(Guid account, CancellationToken ct = default)
    {
        lock (gate)
        {
            var (balance, last) = wallets.GetValueOrDefault(account);
            return Task.FromResult(new QunaResult(true, balance, last));
        }
    }

    public Task<QunaResult> SpendAsync(QunaRequest request, CancellationToken ct = default) =>
        Task.FromResult(QunaWalletRules.Invalid(request, debit: true) is { } error ? Refused(request.AccountId, error) : Apply(request, -request.Amount));

    public Task<QunaResult> GrantAsync(QunaRequest request, CancellationToken ct = default) =>
        Task.FromResult(QunaWalletRules.Invalid(request, debit: false) is { } error ? Refused(request.AccountId, error) : Apply(request, request.Amount));

    public Task<QunaResult> RefundAsync(Guid account, string spendRequestId, int? worldId = null, CancellationToken ct = default)
    {
        QunaLedgerEntry? spend;
        lock (gate) spend = ledger.FirstOrDefault(e => e.RequestId == spendRequestId);
        // Only a purchase is given back, not a staff deduction (105_quna_refund_purchases.realm.sql).
        if (spend is null || spend.AccountId != account || spend.Reason != QunaWalletRules.Purchase || spend.Delta >= 0) return Task.FromResult(Refused(account, QunaWalletRules.NotFound));
        var details = JsonSerializer.SerializeToElement(new { refunds = spendRequestId });
        return Task.FromResult(Apply(new QunaRequest(account, QunaWalletRules.RefundId(spendRequestId), -spend.Delta, QunaWalletRules.Refund,
            worldId ?? spend.WorldId, spend.CharacterId, details), -spend.Delta));
    }

    /// <summary>Opening balance (tests): a migration entry.</summary>
    public long Seed(Guid account, long amount) =>
        Apply(new QunaRequest(account, "seed:" + Guid.NewGuid().ToString("N"), amount, QunaWalletRules.Migration), amount).Balance;

    QunaResult Refused(Guid account, string error)
    {
        lock (gate)
        {
            var (balance, last) = wallets.GetValueOrDefault(account);
            return new QunaResult(false, balance, last, error);
        }
    }

    QunaResult Apply(QunaRequest request, long delta)
    {
        QunaResult result;
        lock (gate)
        {
            if (ledger.FirstOrDefault(e => e.RequestId == request.RequestId) is { } done)
                return done.AccountId == request.AccountId && done.Delta == delta
                    ? new QunaResult(true, done.BalanceAfter, done.EntryId, Repeated: true)
                    : new QunaResult(false, wallets.GetValueOrDefault(request.AccountId).Balance, wallets.GetValueOrDefault(request.AccountId).Last, QunaWalletRules.RequestConflict);
            var (balance, last) = wallets.GetValueOrDefault(request.AccountId);
            if (balance + delta < 0) return new QunaResult(false, balance, last, QunaWalletRules.Insufficient);
            var entry = new QunaLedgerEntry(ledger.Count + 1, request.AccountId, delta, balance + delta, request.Reason, request.RequestId,
                request.WorldId, request.CharacterId, request.Details, DateTimeOffset.UtcNow);
            ledger.Add(entry);
            wallets[request.AccountId] = (entry.BalanceAfter, entry.EntryId);
            result = new QunaResult(true, entry.BalanceAfter, entry.EntryId);
        }
        Changed?.Invoke(request.AccountId, result.Balance, result.EntryId);
        return result;
    }
}
