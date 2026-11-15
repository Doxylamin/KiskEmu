using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>#93: a kill or party level-up re-checks the quests; QuestClassReward items are paid to the player's class.</summary>
public sealed class QuestLevelUpAndClassRewardTests
{
    const long Now = 1_760_000_400_000;

    [Fact]
    public async Task Party_experience_level_up_moves_a_PCLevel_quest_on()
    {
        // HQ1103010 (Light, Auto): its only step is PCLevel 45.
        var store = new FakeCharacterStore { Progress = (44, 0) };
        var session = await GameSessionTestHelpers.EnterSharedWorldAsync(store, Now, level: 44);
        Assert.Equal(44, session.Level);
        await session.QuestCommandAsync("QuestAdd", "HQ1103010", default);
        Assert.Equal(QuestLog.Incomplete, session.Quests!.Get("HQ1103010")!.Status);

        session.ShareExperience(GameData.Embedded.NeedExp(44) - session.Experience);
        await session.TickAsync(Now + 1_000);

        Assert.Equal(45, session.Level);
        Assert.NotEqual(QuestLog.Incomplete, session.Quests!.Get("HQ1103010")!.Status);
    }

    [Fact]
    public async Task Class_reward_items_are_paid_after_the_common_items()
    {
        // HQ1102010 (Light): common items 530116001, 530146001, 530560001, then the class's item (Gladiator 110150028, ...).
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now);
        var quest = QuestBook.Embedded.Find("HQ1102010")!;
        var classItems = quest.Rewards!.ClassItems!;
        var cls = session.ClassName!;
        var expected = classItems[cls].Select(i => (int)i[0]).ToList();
        var others = classItems.Where(kv => kv.Key != cls).SelectMany(kv => kv.Value).Select(i => (int)i[0]).Except(expected).ToList();

        await session.QuestCommandAsync("QuestAdd", "HQ1102010", default);
        await session.QuestCommandAsync("QuestComplete", "HQ1102010", default);
        var record = session.Quests!.Get("HQ1102010")!;
        Assert.Equal(QuestLog.Complete, record.Status);
        var before = store.Items.Count;
        await session.HandleAsync(new PacketWriter().U16(0x8d0e).I32((int)record.Uid).I32(0).ToArray(), Now);

        var granted = store.Items.Skip(before).Select(i => i.TemplateId).ToList();
        foreach (var id in expected) Assert.Contains(id, granted);
        Assert.DoesNotContain(granted, others.Contains);
        var lastCommon = granted.FindLastIndex(id => quest.Rewards.Items!.Any(c => c[0] == id));
        Assert.True(lastCommon < granted.IndexOf(expected[0]), "the class items come after the common items");
    }
}
