using AionClientProbe;

namespace AionClientEditor.Cli;

/// <summary>
/// kisk-client-tool: headless client-table commands. Reads the local client install with the user's own archive key;
/// writes only to the given output paths (keep client-derived output out of the repository, e.g. research/local).
/// </summary>
public static class CliApp
{
    const string Usage = """
        kisk-client-tool <command> [options]

        Commands:
          dump-schema   Columns and sample rows of a client table, as JSON.
                        --table <name|virtual path>  NpcData, NpcTalk, L10NString[:locale] or AION2/Content/...
                        [--rows 3] [--id <rowId>]... [--find <text>]... --out <file.json>
          custom-npcs   Build the custom NPC patch pak from exported definitions.
                        --definitions <custom-npcs.json> --out <folder> [--pak-name <name>]

        Client options (all commands):
          --paks <Content/Paks folder>   default: the launcher's private copy
          --key <archive key json>       file with a key_hex field (never commit it)
          [--oodle <dll>] [--usmap <file>]
        """;

    public static async Task<int> RunAsync(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            Console.WriteLine(Usage);
            return args.Length == 0 ? 2 : 0;
        }

        try
        {
            var options = CliOptions.Parse(args[1..]);
            return args[0] switch
            {
                "dump-schema" => await SchemaDump.RunAsync(options),
                "custom-npcs" => await CustomNpcPakCommand.RunAsync(options),
                _ => Fail($"Unknown command '{args[0]}'"),
            };
        }
        catch (Exception ex) when (ex is ArgumentException or FileNotFoundException or DirectoryNotFoundException
            or InvalidOperationException or KeyNotFoundException or FormatException or NotSupportedException)
        {
            return Fail(ex.Message);
        }
    }

    internal static AionClientSession OpenClient(CliOptions options)
    {
        var paks = options.Get("paks") ?? AionClientSession.DefaultPaksFolder();
        var key = options.Require("key");
        var oodle = AionClientSession.ResolveOodleDll(options.Get("oodle"));
        var usmap = AionClientSession.TryResolveUsmap(options.Get("usmap"))
            ?? throw new FileNotFoundException("Usmap not found; pass --usmap", options.Get("usmap"));
        Console.Error.WriteLine($"Mounting {paks} ...");
        return AionClientSession.Open(paks, key, oodle, usmap);
    }

    static int Fail(string message)
    {
        Console.Error.WriteLine($"error: {message}");
        return 1;
    }
}

public sealed class CliOptions
{
    readonly Dictionary<string, List<string>> _values = new(StringComparer.OrdinalIgnoreCase);

    public static CliOptions Parse(IReadOnlyList<string> args)
    {
        var options = new CliOptions();
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal) || arg.Length == 2)
                throw new ArgumentException($"Unexpected argument '{arg}'");
            var name = arg[2..];
            var value = i + 1 < args.Count && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[++i] : "true";
            if (!options._values.TryGetValue(name, out var list)) options._values[name] = list = [];
            list.Add(value);
        }
        return options;
    }

    public string? Get(string name) => _values.TryGetValue(name, out var list) ? list[^1] : null;

    public IReadOnlyList<string> GetAll(string name) => _values.TryGetValue(name, out var list) ? list : [];

    public string Require(string name) => Get(name) ?? throw new ArgumentException($"--{name} is required");

    public int GetInt(string name, int fallback) =>
        Get(name) is { } text ? int.TryParse(text, out var value) ? value : throw new FormatException($"--{name} must be a number") : fallback;
}
