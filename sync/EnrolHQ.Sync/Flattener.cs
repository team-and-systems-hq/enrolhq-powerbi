using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace EnrolHQ.Sync;

internal enum ColumnKind
{
    Text,
    Integer,
    Number,
    Logical,
    Date,
    DateTime,
}

internal sealed record Column(string Name, ColumnKind Kind);

/// <summary>A table of plain, typed columns. Each row holds one value per column, in column order.</summary>
internal sealed record FlatTable(string Name, IReadOnlyList<Column> Columns, IReadOnlyList<object?[]> Rows);

/// <summary>
/// Turns nested records into tables of plain columns.
///
/// Nested records become prefixed columns (user_parent.email becomes
/// user_parent_email). A list of records becomes a table of its own, linked
/// to its parent by id, and leaves a count behind. A list of plain values
/// becomes comma-separated text, and also a table of its own when the values
/// are ids.
///
/// A record whose keys are all ids is a list in disguise: each key names an
/// entry, not a field. It becomes a table of its own like a list, with the key
/// as each entry's id, rather than ten columns for every id. One school's
/// custom_form_documents_elements held over a thousand ids, which as columns
/// made a table too wide for Power BI to load.
///
/// Column types are worked out from the values, because which fields exist
/// varies from school to school.
/// </summary>
internal static class Flattener
{
    private const string CountSuffix = "_count";

    /// <summary>Stands for an empty list until Build knows what kind of list it was.</summary>
    private static readonly object EmptyList = new();

    /// <summary>Stands for an empty record until Build knows whether it was a list keyed by id.</summary>
    private static readonly object EmptyRecord = new();

    public static List<FlatTable> Flatten(string table, IEnumerable<JsonObject> records)
    {
        var tables = new Dictionary<string, List<Dictionary<string, object?>>>(StringComparer.Ordinal);
        foreach (var record in records)
        {
            AddRow(table, record, link: null, tables);
        }

        return tables
            .Where(entry => entry.Value.Count > 0)
            .Select(entry => Build(entry.Key, entry.Value))
            .ToList();
    }

    /// <summary>events becomes event, categories becomes category, campuses becomes campus.</summary>
    internal static string Singular(string name)
    {
        if (name.EndsWith("ies", StringComparison.Ordinal))
        {
            return name[..^3] + "y";
        }

        if (name.EndsWith("ses", StringComparison.Ordinal))
        {
            return name[..^2];
        }

        return name.EndsWith('s') && !name.EndsWith("ss", StringComparison.Ordinal) ? name[..^1] : name;
    }

    private static void AddRow(
        string table,
        JsonObject record,
        (string Column, object? Value)? link,
        Dictionary<string, List<Dictionary<string, object?>>> tables)
    {
        if (!tables.TryGetValue(table, out var rows))
        {
            tables[table] = rows = [];
        }

        var row = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (link is { } parent)
        {
            row[parent.Column] = parent.Value;
        }

        // Children link to this record when it has an id, or else to whatever this record links to.
        var id = Scalar(record["id"]);
        var childLink = id is not null ? (Singular(table) + "_id", id) : link;
        AddFields(table, record, prefix: "", row, childLink, tables, reserved: link?.Column);
        rows.Add(row);
    }

    private static void AddFields(
        string table,
        JsonObject record,
        string prefix,
        Dictionary<string, object?> row,
        (string Column, object? Value)? childLink,
        Dictionary<string, List<Dictionary<string, object?>>> tables,
        string? reserved)
    {
        foreach (var (key, value) in record)
        {
            var name = prefix + key;
            if (name == reserved)
            {
                // The record has a field of its own with the same name as the link to its parent.
                name += "_value";
            }

            switch (value)
            {
                // Nothing in an empty record says whether its keys would have been ids.
                case JsonObject { Count: 0 }:
                    row[name] = EmptyRecord;
                    break;
                case JsonObject entries when entries.All(entry => Guid.TryParse(entry.Key, out _)):
                    row[name + CountSuffix] = (long)entries.Count;
                    foreach (var (entryId, entry) in entries)
                    {
                        AddRow(ChildTable(table, name), Keyed(entryId, entry), childLink, tables);
                    }

                    break;
                case JsonObject nested:
                    AddFields(table, nested, name + "_", row, childLink, tables, reserved);
                    break;
                case JsonArray list when list.Any(item => item is JsonObject or JsonArray):
                    row[name + CountSuffix] = (long)list.Count;
                    foreach (var item in list.OfType<JsonObject>())
                    {
                        AddRow(ChildTable(table, name), item, childLink, tables);
                    }

                    break;
                // Nothing in an empty list says what it would have held. Build decides,
                // from the other rows, whether this is a count of 0 or a blank.
                case JsonArray { Count: 0 }:
                    row[name] = EmptyList;
                    break;
                case JsonArray list:
                    var values = list.Select(Scalar).ToList();
                    row[name] = string.Join(", ", values.Select(ToText));
                    if (childLink is not null && values.All(item => item is string text && Guid.TryParse(text, out _)))
                    {
                        AddIdRows(ChildTable(table, name), Singular(key) + "_id", values, childLink.Value, tables);
                    }

                    break;
                default:
                    row[name] = Scalar(value);
                    break;
            }
        }
    }

    /// <summary>
    /// A child table's name, which also names its file. It comes from a key in
    /// the API's answer, so anything but letters, digits and underscores is replaced.
    /// </summary>
    private static string ChildTable(string table, string name) =>
        $"{table}_{new string(name.Select(character => char.IsAsciiLetterOrDigit(character) || character == '_' ? character : '_').ToArray())}";

    /// <summary>An entry of a record keyed by id, with its key as its id unless it has one of its own.</summary>
    private static JsonObject Keyed(string key, JsonNode? entry)
    {
        if (entry is JsonObject fields)
        {
            var record = (JsonObject)fields.DeepClone();
            if (!record.ContainsKey("id"))
            {
                record["id"] = key;
            }

            return record;
        }

        return new JsonObject { ["id"] = key, ["value"] = entry?.DeepClone() };
    }

    private static void AddIdRows(
        string table,
        string column,
        List<object?> ids,
        (string Column, object? Value) link,
        Dictionary<string, List<Dictionary<string, object?>>> tables)
    {
        if (!tables.TryGetValue(table, out var rows))
        {
            tables[table] = rows = [];
        }

        rows.AddRange(ids.Select(id => new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [link.Column] = link.Value,
            [column == link.Column ? column + "_value" : column] = id,
        }));
    }

    private static FlatTable Build(string name, List<Dictionary<string, object?>> rows)
    {
        var counted = rows.SelectMany(row => row.Keys).Where(key => key.EndsWith(CountSuffix, StringComparison.Ordinal)).ToHashSet(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            foreach (var (key, empty) in row.Where(field => ReferenceEquals(field.Value, EmptyList) || ReferenceEquals(field.Value, EmptyRecord)).ToList())
            {
                if (counted.Contains(key + CountSuffix))
                {
                    row.Remove(key);
                    row[key + CountSuffix] = 0L;
                }
                else if (ReferenceEquals(empty, EmptyRecord))
                {
                    // An empty record that was never a list has no fields to give columns to.
                    row.Remove(key);
                }
                else
                {
                    row[key] = null;
                }
            }
        }

        var names = rows.SelectMany(row => row.Keys).Distinct(StringComparer.Ordinal).ToList();
        var kinds = names.ToDictionary(
            column => column,
            column => KindOf(rows.Select(row => row.GetValueOrDefault(column))),
            StringComparer.Ordinal);

        // "non_user_parent" is null on rows with no second parent and expands to
        // non_user_parent_* on the rest, leaving an empty column behind.
        var kept = names
            .Where(column => kinds[column] is not null
                || !names.Any(other => other.StartsWith(column + "_", StringComparison.Ordinal)))
            .ToList();

        var columns = kept.Select(column => new Column(column, kinds[column] ?? ColumnKind.Text)).ToList();
        var converted = rows
            .Select(row => columns.Select(column => Convert(row.GetValueOrDefault(column.Name), column.Kind)).ToArray())
            .ToList();
        return new FlatTable(name, columns, converted);
    }

    /// <summary>The narrowest type that fits every value, or null when the column has no values.</summary>
    private static ColumnKind? KindOf(IEnumerable<object?> values)
    {
        var present = values.Where(value => value is not null and not "").ToList();
        if (present.Count == 0)
        {
            return null;
        }

        if (present.All(value => value is bool))
        {
            return ColumnKind.Logical;
        }

        if (present.All(value => value is long))
        {
            return ColumnKind.Integer;
        }

        if (present.All(value => value is long or double))
        {
            return ColumnKind.Number;
        }

        if (present.All(value => value is string text && IsDate(text)))
        {
            return ColumnKind.Date;
        }

        if (present.All(value => value is string text && IsDateTime(text)))
        {
            return ColumnKind.DateTime;
        }

        return ColumnKind.Text;
    }

    private static object? Convert(object? value, ColumnKind kind)
    {
        if (value is null or "")
        {
            return kind == ColumnKind.Text ? value : null;
        }

        return kind switch
        {
            ColumnKind.Number => System.Convert.ToDouble(value, CultureInfo.InvariantCulture),
            ColumnKind.Date => ParseDate((string)value),
            ColumnKind.DateTime => ParseDateTime((string)value),
            ColumnKind.Text => ToText(value),
            _ => value,
        };
    }

    private static bool IsDate(string value) =>
        value.Length == 10 && value[4] == '-' && value[7] == '-' && ParseDate(value) is not null;

    private static bool IsDateTime(string value) =>
        value.Length >= 19 && value[10] == 'T' && ParseDateTime(value) is not null;

    private static DateTime? ParseDate(string value) =>
        DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;

    /// <summary>
    /// Times are kept as the school's local clock time and the offset is
    /// dropped. EnrolHQ sends local times with the school's offset; one sent in
    /// UTC is turned into this computer's local time, which is the school's
    /// when the tool runs at the school.
    /// </summary>
    private static DateTime? ParseDateTime(string value)
    {
        if (!DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
        {
            return null;
        }

        var local = time.Offset == TimeSpan.Zero ? time.ToLocalTime() : time;
        return DateTime.SpecifyKind(local.DateTime, DateTimeKind.Unspecified);
    }

    private static object? Scalar(JsonNode? node)
    {
        if (node is not JsonValue value)
        {
            return null;
        }

        switch (value.GetValueKind())
        {
            case JsonValueKind.String:
                return value.GetValue<string>();
            case JsonValueKind.True:
                return true;
            case JsonValueKind.False:
                return false;
            case JsonValueKind.Number:
                // Not a conditional expression: that would turn the whole number into a double.
                var text = value.ToJsonString();
                if (long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var whole))
                {
                    return whole;
                }

                return double.Parse(text, CultureInfo.InvariantCulture);
            default:
                return null;
        }
    }

    private static string ToText(object? value) => value switch
    {
        null => "",
        bool flag => flag ? "true" : "false",
        IFormattable number => number.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };
}
