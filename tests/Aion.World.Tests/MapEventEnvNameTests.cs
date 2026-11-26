using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>
/// Map events that name an object by its EnvObjData name, not its spawner. Live 2026-10-30 (Strel, HQ2101090): F on the
/// Prison Compound Door (spawner D_Starter_Layer_2_EnvObj_DoorSmall_1, EnvObjData E_D0_Layer_2_PrisonDoorSmall_001)
/// opened and closed it and "Open the Prison Compound Door" never counted.
/// </summary>
public sealed class MapEventEnvNameTests
{
    const long Now = 1_760_005_000_000;

    static byte[] Use(int key) => new PacketWriter().U16(0x3636).U8(1).Varint(key).I64(0).I32(0).ToArray();

    [Fact]
    public void Prison_door_carries_its_envobj_name()
    {
        var door = WorldObjects.ObjectsOn(110005).Single(o => o.Name == "D_Starter_Layer_2_EnvObj_DoorSmall_1");
        Assert.Equal("E_D0_Layer_2_PrisonDoorSmall_001", door.EnvName);
    }

    static bool Named(string goal, int map, string spawn) => WorldObjects.ObjectsOn(map).Single(o => o.Name == spawn) is var obj
        && MapEventRun.NamesObject(new MapEventObjective(1, "UseEnvObj", [goal, "1"]), obj.Name, obj.EnvDataName);

    [Fact]
    public void Use_env_obj_goals_name_objects_exactly()
    {
        // Bakron's wind altar by its EnvObjData name; AbyssStone_01 by spawn entry, not its V01 variant (another EnvObjData row).
        Assert.True(Named("E_PD_Bakron_SpiritAltar_01", 600071, "BakronIsland_Easy_SpiritAltar_Wind"));
        Assert.True(Named("E_PD_ReforgedAbyss_AbyssStone_01_V01_1", 600082, "E_PD_ReforgedAbyss_AbyssStone_01_V01_1"));
        Assert.True(Named("E_PD_ReforgedAbyss_AbyssStone_01", 600082, "E_PD_ReforgedAbyss_AbyssStone_01_1"));
        Assert.False(Named("E_PD_ReforgedAbyss_AbyssStone_01", 600082, "E_PD_ReforgedAbyss_AbyssStone_01_V01_1"));
    }

    [Fact]
    public void Munihelle_second_firewood_answers_to_its_own_name()
    {
        // E_D0_FireWood_002 is spawned as E_D0_FireWood_001_2: only the second batch's goal counts it.
        var second = WorldObjects.ObjectsOn(110002).Single(o => o.Name == "E_D0_FireWood_001_2");
        Assert.Equal("E_D0_FireWood_002", second.EnvDataName);
        Assert.True(Named("E_D0_FireWood_002", 110002, second.Name));
        Assert.False(Named("E_D0_FireWood_001", 110002, second.Name));
        Assert.True(Named("E_D0_FireWood_001", 110002, "E_D0_FireWood_001_1"));
    }

    [Fact]
    public async Task Prison_compound_door_counts_the_step()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterSharedDarkWorldAsync(store, Now);
        await session.QuestCommandAsync("QuestAdd", "HQ2101090", default);
        await MapEventScenarioHelpers.EnterDungeonByIdAsync(session, 110005, Now + 200);
        var run = await MapEventScenarioHelpers.WaitForMapEventRunAsync(session, "World_D_Starter_Layer_2_MapEvent", Now + 300, attempts: 16);
        Assert.NotNull(run);
        // Step 1 (the corridor trigger and the first Draco warrior) straight on the run.
        var t = Now + 2_000;
        run!.PositionAt(n => n == "D_Starter_Layer_2_Trigger_R1_DracoWay", _ => false, t);
        run.Killed(0, "D_Starter_Layer_2_Single_DracoWar_1", true, false, t);
        await session.TickAsync(t + 500);
        Assert.Equal(2, run!.StepOrder);
        var objects = WorldObjects.ObjectsOn(110005);
        var index = objects.Select((o, i) => (o, i)).Single(x => x.o.Name == "D_Starter_Layer_2_EnvObj_DoorSmall_1").i;
        await MapEventScenarioHelpers.SyncPositionAsync(session, objects[index].At, t + 1_000);
        await session.HandleAsync(Use(WorldObjects.FirstKey + index), t + 2_000);
        for (var tick = 0; tick < 10; tick++) await session.TickAsync(t + 2_000 + objects[index].InteractTime + tick * 500);
        Assert.True(run.StepOrder > 2, $"step {run.StepOrder} goals {string.Join(',', run.Goals)}");
    }
}
