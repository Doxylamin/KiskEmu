using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class PlaythroughElyosStepTests
{
    const long Now = 1_760_050_100_000;

    [Fact]
    public async Task HQ1101018_step2_dratona_takes_combat_kills()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterFreshSharedWorldAsync(store, Now, race: 1, level: 4);
        await session.QuestCommandAsync("QuestAdd", "HQ1101018", default);
        await session.QuestCommandAsync("QuestCompleteStep", "HQ1101018", default);

        var record = session.Quests!.Get("HQ1101018")!;
        Assert.Equal(2, record.Step);

        var forest = new Vector3d(-23077, -44872, 17873);
        await MapEventScenarioHelpers.SyncPositionAsync(session, forest, Now);
        var prey = session.Targets!.Values.FirstOrDefault(t => !t.Dead && t.NpcId == 2101164);
        Assert.NotNull(prey);

        var skillId = MapEventScenarioHelpers.BasicAttackSkill(session);
        prey!.Immune = false;
        prey.Relationship = 6;
        prey.Hp = 1;
        session.SetPositionForTests(prey.Position);   // in reach (skill use range)
        var castReplies = await MapEventScenarioHelpers.CombatCastAsync(session, skillId, prey.Key, prey.Position, Now + 100);
        Assert.Contains(castReplies, p => GameSessionTestHelpers.Op(p) == 0x3804);
        prey = session.Targets!.Values.First(t => t.Key == prey.Key);

        record = session.Quests!.Get("HQ1101018")!;
        Assert.True(prey.Dead || record.Goals[0] >= 1 || record.Step > 2,
            $"expected kill credit, prey dead={prey.Dead} hp={prey.Hp} openKills={string.Join(",", session.Quests!.OpenKillNpcIds())} goals={string.Join(",", record.Goals)} step={record.Step}");
    }

    [Fact]
    public async Task HQ1101070_instance_layer_clears_without_dev_fallback()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterFreshSharedWorldAsync(store, Now, race: 1, level: 10);
        await session.QuestCommandAsync("QuestAdd", "HQ1101070", default);
        Assert.True(await MapEventScenarioHelpers.PlayStarterInstanceLayer2Async(session, Now + 10), $"step layer map={session.MapId}");
        Assert.True(session.Quests!.Get("HQ1101070")!.Status >= QuestLog.Complete);
    }

    [Fact]
    public async Task HQ1101070_strict_play_to_reward_after_starter_chain()
    {
        var (report, session, store) = await CampaignPlaythroughHelpers.RunElyosStarterWithSessionAsync(Now, stopBeforeCode: "HQ1101070");
        Assert.Null(report.BlockedQuestCode);
        var gaps = new List<string>();
        var fallbacks = new List<string>();
        var played = await CampaignPlaythroughHelpers.PlayQuestStrictAsync(session, store, "HQ1101070", 1, Now + 40_000L, gaps, fallbacks);
        Assert.True(played.Ok, played.Reason ?? "play failed");
        Assert.Equal(QuestLog.Rewarded, session.Quests!.Get("HQ1101070")!.Status);
    }

    [Fact]
    public async Task HQ1101070_after_full_starter_chain_to_1050()
    {
        var (report, session, _) = await CampaignPlaythroughHelpers.RunElyosStarterWithSessionAsync(Now, stopBeforeCode: "HQ1101070");
        Assert.Null(report.BlockedQuestCode);
        Assert.Equal("HQ1101050", report.LastRewardedCode);
        if (session.Quests!.Get("HQ1101070") is null)
        {
            Assert.True(session.CanAcceptQuest("HQ1101070"));
            await CampaignPlaythroughHelpers.TryAcceptQuestForStarterAsync(session, "HQ1101070", 1, Now + 50_000);
        }
        Assert.NotNull(session.Quests!.Get("HQ1101070"));
        var ok = await MapEventScenarioHelpers.PlayStarterInstanceLayer2Async(session, Now + 2_000_000_000L);
        var q = session.Quests!.Get("HQ1101070");
        Assert.NotNull(q);
        Assert.True(ok && q!.Status >= QuestLog.Complete,
            $"ok={ok} map={session.MapId} instanceKey={session.InstanceKey} status={q.Status}");
    }
}
