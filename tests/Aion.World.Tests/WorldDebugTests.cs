using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>The staff dashboard's debug switches: startup state, switching on and off, and which files it serves.</summary>
[Collection(nameof(WorldDebugTests))]
public sealed class WorldDebugTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "aion-world-debug-" + Guid.NewGuid().ToString("N"));

    public WorldDebugTests() => Directory.CreateDirectory(root);

    public void Dispose()
    {
        // Global state: nothing else may keep writing a map-event log into a deleted folder.
        WorldDebug.Configure(null, null, null);
        try { Directory.Delete(root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void Startup_Settings_Name_The_Debug_Folder()
    {
        WorldDebug.Configure(null, Path.Combine(root, "events.log"), null);
        Assert.Equal(root, WorldDebug.Directory);
        Assert.Equal(Path.Combine(root, "events.log"), WorldDebug.MapEventLog);
        Assert.Null(WorldDebug.TraceDirectory);

        WorldDebug.Configure(null, null, Path.Combine(root, "traces"));
        Assert.Equal(root, WorldDebug.Directory);
        Assert.Equal(Path.Combine(root, "traces"), WorldDebug.TraceDirectory);

        var configured = Path.Combine(root, "elsewhere");
        WorldDebug.Configure(configured, Path.Combine(root, "events.log"), null);
        Assert.Equal(configured, WorldDebug.Directory);
    }

    [Fact]
    public void Switches_Write_Into_The_Debug_Folder_And_Turn_Off()
    {
        WorldDebug.Configure(root, null, null);
        Assert.True(WorldDebug.SetMapEventLog(true));
        Assert.True(WorldDebug.SetTraces(true));
        Assert.Equal(Path.Combine(root, WorldDebug.MapEventFile), WorldDebug.MapEventLog);
        Assert.Equal(Path.Combine(root, WorldDebug.TraceFolder), WorldDebug.TraceDirectory);
        Assert.True(Directory.Exists(WorldDebug.TraceDirectory));

        Assert.True(WorldDebug.SetMapEventLog(false));
        Assert.True(WorldDebug.SetTraces(false));
        Assert.Null(WorldDebug.MapEventLog);
        Assert.Null(WorldDebug.TraceDirectory);
    }

    [Fact]
    public void Without_A_Debug_Folder_Switching_On_Is_Refused()
    {
        WorldDebug.Configure(null, null, null);
        Assert.False(WorldDebug.SetMapEventLog(true));
        Assert.False(WorldDebug.SetTraces(true));
        Assert.True(WorldDebug.SetMapEventLog(false));
        Assert.Null(WorldDebug.MapEventLog);
    }

    [Fact]
    public void Files_Lists_Logs_And_Traces_And_Never_Resolves_Paths()
    {
        WorldDebug.Configure(root, null, null);
        WorldDebug.SetTraces(true);
        File.WriteAllText(Path.Combine(root, "map-events.log"), "x");
        File.WriteAllText(Path.Combine(root, WorldDebug.TraceFolder, "client-trace-1.json"), "{}");
        File.WriteAllText(Path.Combine(root, "secret.txt"), "no");

        Assert.Equal(["client-trace-1.json", "map-events.log"], WorldDebug.Files().Select(f => f.Name).Order());
        Assert.NotNull(WorldDebug.File("map-events.log"));
        Assert.NotNull(WorldDebug.File("client-trace-1.json"));
        Assert.Null(WorldDebug.File("secret.txt"));
        Assert.Null(WorldDebug.File("../secret.txt"));
        Assert.Null(WorldDebug.File(Path.Combine("traces", "client-trace-1.json")));
        Assert.Null(WorldDebug.File(""));
    }
}

[CollectionDefinition(nameof(WorldDebugTests), DisableParallelization = true)]
public sealed class WorldDebugTestsCollection;
