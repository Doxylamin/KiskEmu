using System.Reflection;

namespace Aion.Host.Debug;

/// <summary>What this host runs: its source commit (bug reports) and the opcode names (packet traces).</summary>
public static class BuildInfo
{
    static readonly Lazy<OpcodeCatalog> opcodes = new(OpcodeCatalog.Load);

    /// <summary>
    /// The git commit: Aion:Commit (or AION_COMMIT) when the deployment sets it, else the one the SDK put in the informational
    /// version ("1.0.0+&lt;sha&gt;" when built from a git checkout); null when unknown.
    /// </summary>
    public static string? Commit(IConfiguration configuration)
    {
        if (configuration["Aion:Commit"] is { Length: > 0 } configured) return configured.Trim();
        if (Environment.GetEnvironmentVariable("AION_COMMIT") is { Length: > 0 } env) return env.Trim();
        var version = typeof(BuildInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var plus = version?.IndexOf('+') ?? -1;
        return plus >= 0 && version!.Length > plus + 1 ? version[(plus + 1)..] : null;
    }

    /// <summary>The message's struct name (tools/data/opcode-names.json), else null.</summary>
    public static string? OpcodeName(ushort opcode) => opcodes.Value.Name(opcode) is var name && name != "?" ? name : null;
}
