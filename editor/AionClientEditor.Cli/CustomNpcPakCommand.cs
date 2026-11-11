using System.Security.Cryptography;
using System.Text.Json;
using AionClientEditor.CustomNpcs;
using AionClientEditor.Editing;
using AionClientEditor.Patch;
using AionClientEditor.Serialization;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.FileProvider;
using CUE4Parse.GameTypes.Aion2.Encryption.Aes;
using CUE4Parse.GameTypes.Aion2.Objects;
using CUE4Parse.UE4.Pak.Objects;

namespace AionClientEditor.Cli;

/// <summary>
/// custom-npcs: reads custom-npcs.json and the local client, adds the NPC rows and texts, and writes the patch pak
/// plus custom-npcs.lock.json. Every table is re-serialized unmodified first and must match the client's bytes.
/// </summary>
static class CustomNpcPakCommand
{
    public const string DefaultPakName = "z_KiskEmu_CustomNpcs-Windows_P";
    const string NpcDataPath = "AION2/Content/Data/Table/NpcData.dat";
    const string NpcTalkPath = "AION2/Content/Data/Table/NpcTalk.dat";
    const string StringPath = "AION2/Content/Data/Table/String.dat";

    public static Task<int> RunAsync(CliOptions options)
    {
        var definitions = CustomNpcDefinitions.Parse(File.ReadAllText(options.Require("definitions")));
        var outDir = Path.GetFullPath(options.Require("out"));
        var pakName = options.Get("pak-name") ?? DefaultPakName;
        var talkTemplate = options.Get("talk-template") is { } t ? long.Parse(t) : CustomNpcPatch.DefaultTalkTemplateRowId;

        var errors = CustomNpcPatch.Validate(definitions);
        if (errors.Count > 0) throw new InvalidOperationException("Invalid definitions:\n" + string.Join("\n", errors));

        using var session = CliApp.OpenClient(options);
        var provider = session.Provider;

        var tables = new Dictionary<string, (byte[] Raw, FAion2DataTableFile Table)>();
        foreach (var path in new[] { NpcDataPath, NpcTalkPath, StringPath })
        {
            var raw = ClientTables.ReadRaw(provider, path);
            var table = ClientDatSerializer.ReadTable(raw, path, provider, encrypted: true);
            VerifyTableRoundTrip(provider, path, raw, table, outDir);
            tables[path] = (raw, table);
            Console.Error.WriteLine($"{path}: {ClientTables.Rows(table).Count} rows, round trip ok ({SourceOf(provider, path)})");
        }

        var l10nFiles = new Dictionary<string, (byte[] Raw, FAion2L10NFile File)>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in ClientTables.L10NPaths(provider))
        {
            // StartUpData strings are the pre-login subset; custom NPC texts belong to the main game strings.
            if (path.Contains("/StartUpData/", StringComparison.OrdinalIgnoreCase)) continue;
            var raw = ClientTables.ReadRaw(provider, path);
            var file = ClientTables.ReadL10N(provider, path);
            VerifyL10NRoundTrip(provider, path, raw, file, outDir);
            l10nFiles[path] = (raw, file);
        }
        Console.Error.WriteLine($"L10NString: {l10nFiles.Count} files ({string.Join(", ", l10nFiles.Keys.Select(CustomNpcPatch.LocaleOf).Distinct())}), round trip ok");

        var summary = CustomNpcPatch.Apply(definitions, tables[NpcDataPath].Table, tables[NpcTalkPath].Table,
            tables[StringPath].Table, l10nFiles.ToDictionary(p => p.Key, p => p.Value.File, StringComparer.OrdinalIgnoreCase),
            talkTemplate);
        foreach (var rename in options.GetAll("rename-npc"))
            RenameOfficialNpc(rename, tables[NpcDataPath].Table, tables[StringPath].Table, l10nFiles.Values.Select(v => v.File));

        var payloads = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, (raw, table)) in tables)
            payloads[path] = ClientDatSerializer.SerializeOnDiskTable(table, path, provider, raw);
        foreach (var (path, (raw, file)) in l10nFiles)
            payloads[path] = ClientDatSerializer.SerializeOnDiskL10N(file, path, provider, raw);

        // Read the patched bytes back through the same decoders the tools use.
        foreach (var (path, bytes) in payloads)
        {
            if (path.EndsWith("/L10NString.dat", StringComparison.OrdinalIgnoreCase))
                _ = ClientDatSerializer.ReadL10N(bytes, path, provider);
            else
                _ = ClientDatSerializer.ReadTable(bytes, path, provider, encrypted: true);
        }

        Directory.CreateDirectory(outDir);
        var pakPath = Path.Combine(outDir, pakName + ".pak");
        PatchPakBuilder.WritePak(pakPath, payloads, ArchiveKey(options.Require("key")));
        File.WriteAllBytes(Path.Combine(outDir, pakName + ".sig"), []);
        VerifyPak(pakPath, options.Require("key"), provider, summary);
        var sha = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(pakPath)));

        var lockFile = new
        {
            pak = pakName + ".pak",
            sha256 = sha,
            size = new FileInfo(pakPath).Length,
            builtUtc = DateTime.UtcNow,
            files = payloads.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToArray(),
            npcs = summary.Npcs,
        };
        File.WriteAllText(Path.Combine(outDir, "custom-npcs.lock.json"),
            JsonSerializer.Serialize(lockFile, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));

        Console.Error.WriteLine($"Wrote {pakPath} ({payloads.Count} files, sha256 {sha})");
        foreach (var npc in summary.Npcs)
            Console.Error.WriteLine($"  {npc.Slug}: template {npc.TemplateId} (base {npc.BaseTemplateId}), talks {string.Join(", ", npc.Talks.Select(t => t.TalkName))}");
        return Task.FromResult(0);
    }

    /// <summary>
    /// --rename-npc &lt;NpcData id&gt;=&lt;text&gt;: replaces an official NPC's name in every language. A test of whether the
    /// client loads the patch pak at all: the changed name shows on an NPC the client already knows.
    /// </summary>
    static void RenameOfficialNpc(string spec, FAion2DataTableFile npcData, FAion2DataTableFile strings, IEnumerable<FAion2L10NFile> l10n)
    {
        var eq = spec.IndexOf('=');
        if (eq <= 0 || !long.TryParse(spec[..eq], out var id) || eq == spec.Length - 1)
            throw new ArgumentException($"--rename-npc expects <NpcData id>=<text>, not '{spec}'");
        var text = spec[(eq + 1)..];
        var row = DataTableRows.FindRow(npcData, id) ?? throw new KeyNotFoundException($"--rename-npc: NpcData {id} not found");
        var nameKey = DataTableRows.GetPath(row, "Desc.Key") ?? throw new KeyNotFoundException($"--rename-npc: NpcData {id} has no Desc.Key");
        var stringRow = DataTableRows.FindRowByName(strings, nameKey) ?? throw new KeyNotFoundException($"--rename-npc: String {nameKey} not found");
        var bodyKey = DataTableRows.GetPath(stringRow, "Body.Key") ?? throw new KeyNotFoundException($"--rename-npc: String {nameKey} has no Body.Key");
        var changed = 0;
        foreach (var file in l10n)
            if (file.Entries.ContainsKey(bodyKey)) { file.Entries[bodyKey] = text; changed++; }
        if (changed == 0) throw new KeyNotFoundException($"--rename-npc: {bodyKey} is in no L10N file");
        Console.Error.WriteLine($"rename: NpcData {id} ({nameKey} -> {bodyKey}) = '{text}' in {changed} L10N files");
    }

    /// <summary>Mounts the written pak on its own and reads every custom NPC's rows and texts back out of it.</summary>
    static void VerifyPak(string pakPath, string keyJsonPath, IFileProvider client, CustomNpcPatchSummary summary)
    {
        using var reader = new CUE4Parse.UE4.Pak.PakFileReader(pakPath, client.Versions) { AesKey = ArchiveKey(keyJsonPath) };
        reader.Mount(StringComparer.OrdinalIgnoreCase);
        byte[] Read(string virtualPath) =>
            reader.Files.Values.Single(f => f.Path.Equals(virtualPath, StringComparison.OrdinalIgnoreCase)).Read();

        var npcData = ClientDatSerializer.ReadTable(Read(NpcDataPath), NpcDataPath, client, encrypted: true);
        var npcTalk = ClientDatSerializer.ReadTable(Read(NpcTalkPath), NpcTalkPath, client, encrypted: true);
        var strings = ClientDatSerializer.ReadTable(Read(StringPath), StringPath, client, encrypted: true);
        var l10nPaths = reader.Files.Values.Where(f => f.Path.EndsWith("/L10NString.dat", StringComparison.OrdinalIgnoreCase))
            .Select(f => f.Path).ToList();
        var l10n = l10nPaths.Select(p => ClientDatSerializer.ReadL10N(Read(p), p, client)).ToList();

        foreach (var npc in summary.Npcs)
        {
            var row = DataTableRows.FindRow(npcData, npc.TemplateId) ?? throw new InvalidOperationException($"pak check: NpcData {npc.TemplateId} missing");
            var nameKey = DataTableRows.GetPath(row, "Desc.Key");
            var stringRow = DataTableRows.FindRowByName(strings, nameKey ?? "") ?? throw new InvalidOperationException($"pak check: String {nameKey} missing");
            var bodyKey = DataTableRows.GetPath(stringRow, "Body.Key") ?? "";
            if (l10n.Any(f => !f.Entries.ContainsKey(bodyKey))) throw new InvalidOperationException($"pak check: {bodyKey} missing from an L10N file");
            foreach (var talk in npc.Talks)
            {
                var lines = DataTableRows.Rows(npcTalk).Count(r => DataTableRows.GetPath(r, "Name") == talk.TalkName);
                if (lines != talk.Lines) throw new InvalidOperationException($"pak check: {talk.TalkName} has {lines} rows, expected {talk.Lines}");
            }
            Console.Error.WriteLine($"pak check: {npc.Slug} -> '{l10n[0].Entries[bodyKey]}' ({npc.Talks.Sum(t => t.Lines)} lines) in {l10nPaths.Count} L10N files");
        }
    }

    static void VerifyTableRoundTrip(IFileProvider provider, string path, byte[] raw, FAion2DataTableFile table, string outDir)
    {
        Aion2DatFileEncryption.Initialize(provider);
        var expected = Aion2DatFileEncryption.DecryptDataTable(raw, path);
        var actual = ClientDatSerializer.SerializePlaintextTable(table, provider, ClientDatSerializer.UsesUnicodeNames(expected),
            ClientDatSerializer.UsesUnicodeIds(expected));
        if (!expected.AsSpan().SequenceEqual(actual))
            throw new InvalidOperationException($"{path}: re-serialized table differs from the client's ({FirstDifference(expected, actual)}, {SaveMismatch(outDir, path, expected, actual)}); refusing to patch it");
    }

    static void VerifyL10NRoundTrip(IFileProvider provider, string path, byte[] raw, FAion2L10NFile file, string outDir)
    {
        byte[] expected, actual;
        if (raw.Length >= 4 && BitConverter.ToUInt32(raw, 0) == 2)
        {
            Aion2DatFileEncryption.Initialize(provider);
            expected = Aion2DatFileEncryption.DecryptL10N(raw, path);
            actual = ClientDatSerializer.SerializeEncryptedL10NPayload(file, provider.Versions);
        }
        else
        {
            expected = raw;
            actual = ClientDatSerializer.SerializePlaintextL10N(file, provider.Versions);
        }
        if (!expected.AsSpan().SequenceEqual(actual))
            throw new InvalidOperationException($"{path}: re-serialized L10N differs from the client's ({FirstDifference(expected, actual)}, {SaveMismatch(outDir, path, expected, actual)}); refusing to patch it");
    }

    /// <summary>Saves both plaintexts next to the output (client data: keep the output folder out of the repository).</summary>
    static string SaveMismatch(string outDir, string path, byte[] expected, byte[] actual)
    {
        var dir = Path.Combine(outDir, "roundtrip-mismatch");
        Directory.CreateDirectory(dir);
        var name = path.Replace('/', '_');
        File.WriteAllBytes(Path.Combine(dir, name + ".client.bin"), expected);
        File.WriteAllBytes(Path.Combine(dir, name + ".ours.bin"), actual);
        return $"both saved under {dir}";
    }

    static string FirstDifference(byte[] a, byte[] b)
    {
        var n = Math.Min(a.Length, b.Length);
        for (var i = 0; i < n; i++)
            if (a[i] != b[i]) return $"first difference at byte {i} of {a.Length}/{b.Length}";
        return $"lengths {a.Length}/{b.Length}";
    }

    static string SourceOf(IFileProvider provider, string path)
    {
        var file = provider.Files.Values.First(f => f.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
        return file is FPakEntry pak ? $"{pak.Vfs.Name}, mount {pak.Vfs.MountPoint}" : file.GetType().Name;
    }

    static FAesKey ArchiveKey(string keyJsonPath)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(keyJsonPath));
        return new FAesKey(doc.RootElement.GetProperty("key_hex").GetString()
            ?? throw new InvalidDataException("key json must contain key_hex"));
    }
}
