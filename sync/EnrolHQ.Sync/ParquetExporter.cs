using Parquet;
using Parquet.Schema;

namespace EnrolHQ.Sync;

/// <summary>Writes each table as a Parquet file, which Power BI reads without a driver.</summary>
internal static class ParquetExporter
{
    /// <summary>
    /// Writes to a temporary file and then swaps it in, so Power BI never reads
    /// a half-written file.
    /// </summary>
    public static async Task WriteAsync(FlatTable table, string directory, CancellationToken cancel)
    {
        Directory.CreateDirectory(directory);
        var path = PathFor(directory, table.Name);
        var temporary = path + ".writing";

        var fields = table.Columns.Select(FieldFor).ToArray();
        await using (var stream = File.Create(temporary))
        await using (var writer = await ParquetWriter.CreateAsync(new ParquetSchema(fields), stream, cancellationToken: cancel))
        {
            using var group = writer.CreateRowGroup();
            for (var index = 0; index < fields.Length; index++)
            {
                await WriteColumnAsync(group, fields[index], table.Columns[index].Kind, table.Rows, index, cancel);
            }
        }

        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>Removes files for tables that no longer exist, for example a list that is now empty.</summary>
    public static IReadOnlyList<string> RemoveOthers(string directory, IEnumerable<string> tables)
    {
        if (!Directory.Exists(directory))
        {
            return [];
        }

        var wanted = tables.Select(table => PathFor(directory, table)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var stale = Directory.EnumerateFiles(directory, "*.parquet").Where(file => !wanted.Contains(file)).ToList();
        foreach (var file in stale)
        {
            File.Delete(file);
        }

        return stale.Select(file => Path.GetFileNameWithoutExtension(file)!).ToList();
    }

    private static string PathFor(string directory, string table) => Path.Combine(directory, table + ".parquet");

    private static DataField FieldFor(Column column) => column.Kind switch
    {
        ColumnKind.Integer => new DataField<long?>(column.Name),
        ColumnKind.Number => new DataField<double?>(column.Name),
        ColumnKind.Logical => new DataField<bool?>(column.Name),
        ColumnKind.Date => new DateTimeDataField(column.Name, DateTimeFormat.Date, isNullable: true),
        // Local clock time with no offset. Marked as not adjusted to UTC, so
        // Power BI reads a plain date and time and does not shift it.
        ColumnKind.DateTime => new DateTimeDataField(
            column.Name, DateTimeFormat.Timestamp, isAdjustedToUTC: false, unit: DateTimeTimeUnit.Millis, isNullable: true),
        _ => new DataField<string>(column.Name),
    };

    private static Task WriteColumnAsync(
        ParquetRowGroupWriter group,
        DataField field,
        ColumnKind kind,
        IReadOnlyList<object?[]> rows,
        int index,
        CancellationToken cancel)
    {
        switch (kind)
        {
            case ColumnKind.Integer:
                return group.WriteAsync<long>(field, Values<long>(rows, index), cancellationToken: cancel);
            case ColumnKind.Number:
                return group.WriteAsync<double>(field, Values<double>(rows, index), cancellationToken: cancel);
            case ColumnKind.Logical:
                return group.WriteAsync<bool>(field, Values<bool>(rows, index), cancellationToken: cancel);
            case ColumnKind.Date or ColumnKind.DateTime:
                return group.WriteAsync<DateTime>(field, Values<DateTime>(rows, index), cancellationToken: cancel);
            default:
                return group.WriteAsync(field, rows.Select(row => (string?)row[index]).ToArray()!);
        }
    }

    private static ReadOnlyMemory<T?> Values<T>(IReadOnlyList<object?[]> rows, int index)
        where T : struct =>
        rows.Select(row => (T?)row[index]).ToArray();
}
