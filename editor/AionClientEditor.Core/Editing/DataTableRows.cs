using System.Globalization;
using CUE4Parse.GameTypes.Aion2.Objects;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Assets.Objects.Properties;

namespace AionClientEditor.Editing;

/// <summary>
/// Adds rows to a client data table. Rows are keyed by their <c>ID</c> property (a <c>TableItemId</c> struct with a
/// <c>Value</c>, or a plain integer). The table header's <c>Ids</c> list is one hash per column (OBSERVED: NpcTalk has
/// 20 ids for 20,396 rows, Subzone 28 ids for 28 columns), so appending rows leaves it unchanged.
/// </summary>
public static class DataTableRows
{
    /// <summary>
    /// Appends a copy of the row with ID <paramref name="cloneOfId"/> under <paramref name="newId"/> and applies scalar
    /// edits. Edit keys are property paths: <c>Name</c>, <c>Desc.Key</c>, <c>Tags[1]</c>. Structs on an edited path are
    /// copied; everything else is shared with the source row and must not be mutated in place.
    /// </summary>
    public static FStructFallback AppendRow(FAion2DataTableFile table, long newId, long cloneOfId,
        IReadOnlyDictionary<string, string>? edits = null)
    {
        var rows = RowArray(table).Properties;
        if (FindRow(table, newId) is not null)
            throw new InvalidOperationException($"Row ID {newId} already exists");
        var source = FindRow(table, cloneOfId) ?? throw new KeyNotFoundException($"Row ID {cloneOfId} not found");

        var clone = CloneStruct(source);
        SetPath(clone, IdPath(clone), newId.ToString(CultureInfo.InvariantCulture));
        foreach (var (path, value) in edits ?? new Dictionary<string, string>())
            SetPath(clone, path, value);

        rows.Add(new StructProperty(new FScriptStruct(clone)));
        return clone;
    }

    /// <summary>
    /// Appends a copy of the row whose <c>Name</c> is <paramref name="cloneOfName"/>, for tables without an ID
    /// (for example String.dat, keyed by <c>Name</c>), and applies edits after setting the new name.
    /// </summary>
    public static FStructFallback AppendRowByName(FAion2DataTableFile table, string newName, string cloneOfName,
        IReadOnlyDictionary<string, string>? edits = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(newName);
        var rows = RowArray(table).Properties;
        if (FindRowByName(table, newName) is not null)
            throw new InvalidOperationException($"Row {newName} already exists");
        var source = FindRowByName(table, cloneOfName) ?? throw new KeyNotFoundException($"Row {cloneOfName} not found");

        var clone = CloneStruct(source);
        SetPath(clone, "Name", newName);
        foreach (var (path, value) in edits ?? new Dictionary<string, string>())
            SetPath(clone, path, value);

        rows.Add(new StructProperty(new FScriptStruct(clone)));
        return clone;
    }

    public static FStructFallback? FindRowByName(FAion2DataTableFile table, string name) =>
        Rows(table).FirstOrDefault(r => GetPath(r, "Name") == name);

    public static FStructFallback? FindRow(FAion2DataTableFile table, long id)
    {
        foreach (var property in RowArray(table).Properties)
        {
            if (property is StructProperty { Value.StructType: FStructFallback row } && RowId(row) == id)
                return row;
        }
        return null;
    }

    public static IEnumerable<FStructFallback> Rows(FAion2DataTableFile table) =>
        RowArray(table).Properties.OfType<StructProperty>()
            .Select(p => p.Value?.StructType).OfType<FStructFallback>();

    /// <summary>The row's ID value, or null when it has no integer ID.</summary>
    public static long? RowId(FStructFallback row)
    {
        var tag = row.Properties.FirstOrDefault(t => t.Name.Text is "ID" or "Id" && t.ArrayIndex == 0);
        if (tag?.Tag is StructProperty { Value.StructType: FStructFallback inner })
            tag = inner.Properties.FirstOrDefault(t => t.Name.Text == "Value");
        if (tag is null || PropertyValueFormat.EditKindOf(tag) != PropertyEditKind.Integer) return null;
        return long.TryParse(PropertyValueFormat.FormatCell(tag), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)
            ? id : null;
    }

    /// <summary>Reads a scalar at a property path (same syntax as edits), or null when the path does not exist.</summary>
    public static string? GetPath(FStructFallback row, string path)
    {
        var current = row;
        var segments = path.Split('.');
        for (var i = 0; i < segments.Length; i++)
        {
            var (name, index) = ParseSegment(segments[i]);
            var tag = current.Properties.FirstOrDefault(t => t.Name.Text == name && t.ArrayIndex == index);
            if (tag is null) return null;
            if (i == segments.Length - 1) return PropertyValueFormat.FormatCell(tag);
            if (tag.Tag is not StructProperty { Value.StructType: FStructFallback inner }) return null;
            current = inner;
        }
        return null;
    }

    static string IdPath(FStructFallback row)
    {
        var tag = row.Properties.FirstOrDefault(t => t.Name.Text is "ID" or "Id" && t.ArrayIndex == 0)
            ?? throw new InvalidOperationException("Row has no ID property");
        return tag.Tag is StructProperty ? $"{tag.Name.Text}.Value" : tag.Name.Text;
    }

    static void SetPath(FStructFallback row, string path, string value)
    {
        var current = row;
        var segments = path.Split('.');
        for (var i = 0; i < segments.Length; i++)
        {
            var (name, index) = ParseSegment(segments[i]);
            var tag = current.Properties.FirstOrDefault(t => t.Name.Text == name && t.ArrayIndex == index)
                ?? throw new KeyNotFoundException($"Row has no property {path}");
            if (i == segments.Length - 1)
            {
                if (!PropertyValueFormat.TryApply(tag, value, out var error))
                    throw new FormatException($"{path}: {error}");
                return;
            }

            if (tag.Tag is not StructProperty { Value.StructType: FStructFallback inner })
                throw new FormatException($"{path}: {segments[i]} is not a struct");
            // Copy the struct before descending so the source row keeps its value.
            var copy = CloneStruct(inner);
            tag.Tag = new StructProperty(new FScriptStruct(copy));
            current = copy;
        }
    }

    static FStructFallback CloneStruct(FStructFallback source) => new(source.Properties.Select(CloneTag).ToList());

    static FPropertyTag CloneTag(FPropertyTag tag) =>
        new(tag.Name, tag.PropertyType, tag.Size, tag.ArrayIndex, tag.TagData, tag.HasPropertyGuid, tag.PropertyGuid, tag.Tag)
        {
            ArraySize = tag.ArraySize,
            PropertyTagFlags = tag.PropertyTagFlags,
        };

    static UScriptArray RowArray(FAion2DataTableFile table) =>
        table.Properties.FirstOrDefault()?.Tag is ArrayProperty { Value: { } array }
            ? array
            : throw new InvalidOperationException("Data table has no row array");

    static (string Name, int Index) ParseSegment(string segment)
    {
        var open = segment.IndexOf('[');
        if (open < 0) return (segment, 0);
        if (!segment.EndsWith(']') || !int.TryParse(segment[(open + 1)..^1], out var index) || index < 0)
            throw new FormatException($"Invalid property path segment {segment}");
        return (segment[..open], index);
    }
}
