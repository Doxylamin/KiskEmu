using CUE4Parse.GameTypes.Aion2.Objects;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Assets.Objects.Properties;

namespace AionClientEditor.Editing;

/// <summary>Adds rows to a client data table (<c>Ids</c> plus the <c>Data</c> struct array, kept index-aligned).</summary>
public static class DataTableRows
{
    static readonly string[] IdPropertyNames = ["Id", "ID"];

    /// <summary>
    /// Appends a copy of row <paramref name="cloneOfId"/> under <paramref name="newId"/> and applies scalar edits.
    /// Edit keys are top-level property names, with <c>[n]</c> for fixed-array elements (for example <c>Name</c>,
    /// <c>Tags[1]</c>). An integer <c>Id</c>/<c>ID</c> property equal to the source id is set to the new id unless an
    /// edit sets it. Nested values are shared with the source row and must not be mutated in place.
    /// </summary>
    public static FStructFallback AppendRow(FAion2DataTableFile table, string newId, string cloneOfId,
        IReadOnlyDictionary<string, string>? edits = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(newId);
        if (newId.Contains(',')) throw new ArgumentException("Row ids cannot contain commas", nameof(newId));

        var rows = RowArray(table).Properties;
        if (table.Ids.Length != rows.Count)
            throw new InvalidOperationException($"Table has {table.Ids.Length} ids but {rows.Count} rows");
        if (table.Ids.Contains(newId, StringComparer.Ordinal))
            throw new InvalidOperationException($"Row id {newId} already exists");

        var sourceIndex = Array.IndexOf(table.Ids, cloneOfId);
        if (sourceIndex < 0) throw new KeyNotFoundException($"Row id {cloneOfId} not found");
        if (rows[sourceIndex] is not StructProperty { Value.StructType: FStructFallback source })
            throw new InvalidOperationException($"Row {cloneOfId} is not a struct row");

        var clone = new FStructFallback(source.Properties.Select(CloneTag).ToList());

        var pending = new Dictionary<string, string>(edits ?? new Dictionary<string, string>(), StringComparer.Ordinal);
        foreach (var name in IdPropertyNames)
        {
            var idTag = clone.Properties.FirstOrDefault(t => t.Name.Text == name && t.ArrayIndex == 0);
            if (idTag is not null && !pending.ContainsKey(name)
                && PropertyValueFormat.EditKindOf(idTag) == PropertyEditKind.Integer
                && PropertyValueFormat.FormatCell(idTag) == cloneOfId)
                pending[name] = newId;
        }

        foreach (var (key, value) in pending)
        {
            var (name, index) = ParseKey(key);
            var tag = clone.Properties.FirstOrDefault(t => t.Name.Text == name && t.ArrayIndex == index)
                ?? throw new KeyNotFoundException($"Row has no property {key}");
            if (!PropertyValueFormat.TryApply(tag, value, out var error))
                throw new FormatException($"{key}: {error}");
        }

        rows.Add(new StructProperty(new FScriptStruct(clone)));
        table.Ids = [.. table.Ids, newId];
        return clone;
    }

    public static FStructFallback? FindRow(FAion2DataTableFile table, string id)
    {
        var index = Array.IndexOf(table.Ids, id);
        if (index < 0) return null;
        return RowArray(table).Properties[index] is StructProperty { Value.StructType: FStructFallback row } ? row : null;
    }

    static UScriptArray RowArray(FAion2DataTableFile table) =>
        table.Properties.FirstOrDefault()?.Tag is ArrayProperty { Value: { } array }
            ? array
            : throw new InvalidOperationException("Data table has no row array");

    static FPropertyTag CloneTag(FPropertyTag tag) =>
        new(tag.Name, tag.PropertyType, tag.Size, tag.ArrayIndex, tag.TagData, tag.HasPropertyGuid, tag.PropertyGuid, tag.Tag)
        {
            ArraySize = tag.ArraySize,
            PropertyTagFlags = tag.PropertyTagFlags,
        };

    static (string Name, int Index) ParseKey(string key)
    {
        var open = key.IndexOf('[');
        if (open < 0) return (key, 0);
        if (!key.EndsWith(']') || !int.TryParse(key[(open + 1)..^1], out var index) || index < 0)
            throw new FormatException($"Invalid property key {key}");
        return (key[..open], index);
    }
}
