using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AionClientEditor.Editing;
using AionClientEditor.Serialization;
using CUE4Parse.FileProvider;
using CUE4Parse.GameTypes.Aion2.Objects;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Assets.Objects.Properties;
using Newtonsoft.Json;

namespace AionClientEditor.Cli;

/// <summary>dump-schema: columns and sample rows of a client table, so table facts can be checked locally.</summary>
static class SchemaDump
{
    static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static Task<int> RunAsync(CliOptions options)
    {
        var tableArg = options.Require("table");
        var outPath = options.Require("out");
        var sampleRows = options.GetInt("rows", 3);
        var ids = options.GetAll("id");
        var finds = options.GetAll("find");

        using var session = CliApp.OpenClient(options);
        var provider = session.Provider;
        var paths = ClientTables.Resolve(provider, tableArg);
        var result = new JsonArray();
        foreach (var path in paths)
        {
            Console.Error.WriteLine($"Reading {path}");
            result.Add(path.EndsWith("/L10NString.dat", StringComparison.OrdinalIgnoreCase)
                ? DumpL10N(provider, path, sampleRows, ids, finds)
                : DumpTable(provider, path, sampleRows, ids, finds));
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        File.WriteAllText(outPath, result.ToJsonString(Indented));
        Console.Error.WriteLine($"Wrote {outPath}");
        return Task.FromResult(0);
    }

    static JsonObject DumpTable(IFileProvider provider, string path, int sampleRows, IReadOnlyList<string> ids, IReadOnlyList<string> finds)
    {
        var table = ClientTables.ReadTable(provider, path);
        var rows = ClientTables.Rows(table);

        var columns = new JsonArray();
        foreach (var tag in rows.FirstOrDefault()?.Properties ?? [])
        {
            columns.Add(new JsonObject
            {
                ["name"] = tag.ArrayIndex > 0 ? $"{tag.Name.Text}[{tag.ArrayIndex}]" : tag.Name.Text,
                ["type"] = tag.PropertyType.Text,
                ["struct"] = tag.TagData?.StructType,
                ["enum"] = tag.TagData?.EnumName,
                ["inner"] = tag.TagData?.InnerType,
                ["innerStruct"] = tag.TagData?.InnerTypeData?.StructType,
            });
        }

        var rowIds = rows.Select(DataTableRows.RowId).ToList();
        var numericIds = rowIds.Where(n => n is not null).Select(n => n!.Value).ToList();

        var selected = new SortedSet<int>(Enumerable.Range(0, Math.Min(sampleRows, rows.Count)));
        foreach (var id in ids)
        {
            var index = long.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var wanted)
                ? rowIds.IndexOf(wanted) : -1;
            if (index >= 0) selected.Add(index);
            else Console.Error.WriteLine($"  row {id} not found");
        }
        if (finds.Count > 0)
        {
            for (var i = 0; i < rows.Count; i++)
            {
                var json = JsonConvert.SerializeObject(rows[i]);
                if (finds.Any(f => json.Contains(f, StringComparison.OrdinalIgnoreCase))) selected.Add(i);
                if (selected.Count > sampleRows + ids.Count + 50) break;
            }
        }

        var sample = new JsonArray();
        foreach (var index in selected)
            sample.Add(new JsonObject { ["rowId"] = rowIds[index], ["values"] = RowJson(rows[index]) });

        return new JsonObject
        {
            ["path"] = path,
            ["kind"] = "table",
            ["version"] = table.Version,
            ["rowCount"] = rows.Count,
            ["columnHashCount"] = table.Ids.Length,
            ["numericIdMin"] = numericIds.Count > 0 ? numericIds.Min() : null,
            ["numericIdMax"] = numericIds.Count > 0 ? numericIds.Max() : null,
            ["firstIds"] = new JsonArray(rowIds.Take(5).Select(i => (JsonNode?)i).ToArray()),
            ["lastIds"] = new JsonArray(rowIds.TakeLast(5).Select(i => (JsonNode?)i).ToArray()),
            ["columns"] = columns,
            ["rows"] = sample,
        };
    }

    static JsonObject DumpL10N(IFileProvider provider, string path, int sampleRows, IReadOnlyList<string> ids, IReadOnlyList<string> finds)
    {
        var file = ClientTables.ReadL10N(provider, path);
        var picked = new JsonObject();
        foreach (var pair in file.Entries.Take(sampleRows)) picked[pair.Key] = pair.Value;
        foreach (var id in ids)
            if (file.Entries.TryGetValue(id, out var value)) picked[id] = value;
        foreach (var pair in file.Entries)
        {
            if (picked.Count > sampleRows + ids.Count + 50) break;
            if (finds.Any(f => pair.Key.Contains(f, StringComparison.OrdinalIgnoreCase) || pair.Value.Contains(f, StringComparison.OrdinalIgnoreCase)))
                picked[pair.Key] = pair.Value;
        }
        return new JsonObject
        {
            ["path"] = path,
            ["kind"] = "l10n",
            ["namespace"] = file.Namespace,
            ["entryCount"] = file.Entries.Count,
            ["entries"] = picked,
        };
    }

    static JsonObject RowJson(FStructFallback row)
    {
        var values = new JsonObject();
        foreach (var tag in row.Properties)
        {
            var key = tag.ArrayIndex > 0 ? $"{tag.Name.Text}[{tag.ArrayIndex}]" : tag.Name.Text;
            values[key] = PropertyValueFormat.EditKindOf(tag) == PropertyEditKind.JsonComplex
                ? JsonNode.Parse(JsonConvert.SerializeObject(tag.Tag))
                : PropertyValueFormat.FormatCell(tag);
        }
        return values;
    }
}

/// <summary>Finding and reading client tables by short name.</summary>
static class ClientTables
{
    public static IReadOnlyList<string> Resolve(IFileProvider provider, string table)
    {
        if (table.Contains('/'))
            return [provider.Files.Values.First(f => f.Path.Equals(table, StringComparison.OrdinalIgnoreCase)).Path];

        var parts = table.Split(':', 2);
        if (parts[0].Equals("L10NString", StringComparison.OrdinalIgnoreCase))
        {
            var locale = parts.Length > 1 ? parts[1] : "en-US";
            var matches = provider.Files.Values
                .Where(f => f.Path.EndsWith("/L10NString.dat", StringComparison.OrdinalIgnoreCase)
                    && f.Path.Contains($"/{locale}/", StringComparison.OrdinalIgnoreCase))
                .Select(f => f.Path).Distinct().OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
            return matches.Count > 0 ? matches : throw new FileNotFoundException($"No L10NString.dat for {locale}");
        }

        var path = $"AION2/Content/Data/Table/{table}.dat";
        var file = provider.Files.Values.FirstOrDefault(f => f.Path.Equals(path, StringComparison.OrdinalIgnoreCase))
            ?? throw new FileNotFoundException($"Table not found: {path}");
        return [file.Path];
    }

    public static IReadOnlyList<string> L10NPaths(IFileProvider provider) =>
        provider.Files.Values
            .Where(f => f.Path.EndsWith("/L10NString.dat", StringComparison.OrdinalIgnoreCase))
            .Select(f => f.Path).Distinct().OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();

    public static byte[] ReadRaw(IFileProvider provider, string path) =>
        provider.Files.Values.First(f => f.Path.Equals(path, StringComparison.OrdinalIgnoreCase)).Read();

    public static FAion2DataTableFile ReadTable(IFileProvider provider, string path) =>
        ClientDatSerializer.ReadTable(ReadRaw(provider, path), path, provider, encrypted: true);

    public static FAion2L10NFile ReadL10N(IFileProvider provider, string path) =>
        ClientDatSerializer.ReadL10N(ReadRaw(provider, path), path, provider);

    public static List<FStructFallback> Rows(FAion2DataTableFile table) =>
        table.Properties.FirstOrDefault()?.Tag is ArrayProperty { Value: { } array }
            ? array.Properties.Select(p => p is StructProperty { Value.StructType: FStructFallback row } ? row : null)
                .Where(r => r is not null).Cast<FStructFallback>().ToList()
            : throw new InvalidOperationException("Data table has no row array");
}
