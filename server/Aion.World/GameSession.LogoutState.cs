using System.Text.Json;

namespace Aion.World;

// What a character keeps over a logout (community recordings 2026-11-04 to 06, ported from the addiboii fork, 525b531).
// OBSERVED:
// - timed buffs come back with the time they had left, which does not run while logged out: the buffs stay in the
//   "active_buffs" setting (GameSession.Buffs.cs), saved with the clock at the save, and the load moves their expiry by the
//   time offline;
// - the flight gauge comes back as it was (FP 549067 of 650567 at 234912 #56821), stored here in the "logout_fp" setting.
// Written by the forced saves (logout, lobby, disconnect: FlushDeferredPersistenceAsync), restored at the first load after a
// selection. Not restored: the in-air state (the official character comes back in flight with abnormal 100000002); HP/MP
// (not settled: full in every recording).
public sealed partial class GameSession
{
    const string LogoutFpSetting = "logout_fp";

    int? pendingLogoutFp;

    void LoadLogoutState(IReadOnlyDictionary<string, JsonElement> settings)
    {
        try { pendingLogoutFp = settings.TryGetValue(LogoutFpSetting, out var v) ? v.Deserialize<int>() : null; }
        catch (JsonException) { pendingLogoutFp = null; }
    }

    /// <summary>At a forced save: the timed buffs with the clock now (their time left) and the flight gauge.</summary>
    async Task SaveLogoutStateAsync(long now, CancellationToken ct)
    {
        if (Store is null || CharacterId is null || resources is null) return;
        // Re-save the buffs even when nothing changed since the last flush, so their time left is the one at the logout.
        if (buffs.Any(b => b.ExpireAt != PassiveExpireTime && b.ExpireAt > now)) MarkBuffSettingsDirty();
        if (resources.FpMax > 0) await SaveSettingAsync(LogoutFpSetting, resources.Fp, ct);
    }

    /// <summary>At the first load after a selection: the flight gauge as it was at the logout (never above the maximum).</summary>
    void RestoreLogoutFp()
    {
        if (pendingLogoutFp is not { } fp || resources is null) return;
        pendingLogoutFp = null;
        if (resources.FpMax > 0) resources.Fp = Math.Clamp(fp, 0, resources.FpMax);
    }
}
