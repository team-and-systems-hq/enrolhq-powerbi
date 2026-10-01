using System.Globalization;
using System.Text.Json.Nodes;

namespace EnrolHQ.Sync;

/// <summary>What was written for one table.</summary>
internal sealed record ExportedTable(string Name, int Rows, IReadOnlyList<Column> Columns);

/// <summary>Turns the stored records into Parquet files for Power BI.</summary>
internal static class Exporter
{
    public const string InfoTable = "sync_info";

    public static async Task<List<ExportedTable>> ExportAsync(Settings settings, Store store, Reporter reporter, CancellationToken cancel)
    {
        // An existing project reads the tables and columns it was made with.
        // Each file keeps giving it those, so its refresh never fails because
        // a table has emptied or a column has come or gone.
        var project = PowerBiProject.ReadColumns(settings);
        var added = new List<string>();
        var retyped = new List<string>();
        var written = new List<ExportedTable>();
        foreach (var endpoint in Endpoint.All)
        {
            foreach (var flat in Flattener.Flatten(endpoint.Table, store.Read(endpoint.Table)))
            {
                var table = project.TryGetValue(flat.Name, out var expected) ? Conform(flat, expected, added, retyped) : flat;
                reporter.Progress($"writing {table.Name}: {Reporter.Number(table.Rows.Count)} rows");
                await ParquetExporter.WriteAsync(table, settings.ParquetDirectory, cancel);
                written.Add(new ExportedTable(table.Name, table.Rows.Count, table.Columns));
            }
        }

        // Tables the project has that hold no rows now.
        foreach (var (name, columns) in project.Where(entry => entry.Key != InfoTable && written.All(table => table.Name != entry.Key)))
        {
            var empty = new FlatTable(name, columns, []);
            await ParquetExporter.WriteAsync(empty, settings.ParquetDirectory, cancel);
            written.Add(new ExportedTable(name, 0, columns));
        }

        if (written.Count == 0)
        {
            reporter.Line("The local copy holds no records yet, so there is nothing to write. Run enrolhq-sync first.");
            return written;
        }

        // Says how fresh each table is and whether the data is real, so a
        // report, or an LLM reading the model, can tell.
        var info = Flattener.Flatten(InfoTable, written.Select(table => Describe(table, settings, store))).Single();
        await ParquetExporter.WriteAsync(info, settings.ParquetDirectory, cancel);
        written.Add(new ExportedTable(info.Name, info.Rows.Count, info.Columns));

        var removed = ParquetExporter.RemoveOthers(settings.ParquetDirectory, written.Select(table => table.Name));
        reporter.Line($"Wrote {written.Count} tables to {settings.ParquetDirectory}");
        if (removed.Count > 0)
        {
            reporter.Line($"Removed tables that no longer have any rows: {string.Join(", ", removed)}");
        }

        if (added.Count > 0)
        {
            reporter.Line($"Columns the Power BI project does not have yet, so it does not load them: {string.Join(", ", added)}");
            reporter.Line("  Add them in Power BI Desktop, or make a fresh project with enrolhq-sync export --new-project (anything added to the project is lost).");
        }

        if (retyped.Count > 0)
        {
            reporter.Line($"Columns whose values no longer fit the type the Power BI project gives them: {string.Join(", ", retyped)}");
            reporter.Line("  If Refresh fails on one of them, change its data type in Power BI Desktop.");
        }

        return written;
    }

    /// <summary>
    /// Gives the project the columns it expects, in its own types where that
    /// loses nothing. Missing columns come back empty; columns the project does
    /// not have are kept, and reported.
    /// </summary>
    private static FlatTable Conform(FlatTable table, IReadOnlyList<Column> expected, List<string> added, List<string> retyped)
    {
        var index = table.Columns.Select((column, position) => (column.Name, position)).ToDictionary(pair => pair.Name, pair => pair.position, StringComparer.Ordinal);
        var columns = new List<Column>();
        var sources = new List<(int? Position, ColumnKind From, ColumnKind To)>();
        foreach (var column in expected)
        {
            if (!index.TryGetValue(column.Name, out var position))
            {
                columns.Add(column);
                sources.Add((null, column.Kind, column.Kind));
                continue;
            }

            var actual = table.Columns[position].Kind;
            var kind = Fits(actual, column.Kind) ? column.Kind : actual;
            if (kind != column.Kind)
            {
                retyped.Add($"{table.Name}.{column.Name}");
            }

            columns.Add(column with { Kind = kind });
            sources.Add((position, actual, kind));
        }

        foreach (var column in table.Columns.Where(column => expected.All(known => known.Name != column.Name)))
        {
            added.Add($"{table.Name}.{column.Name}");
            columns.Add(column);
            sources.Add((index[column.Name], column.Kind, column.Kind));
        }

        var rows = table.Rows
            .Select(row => sources.Select(source => source.Position is { } at ? Convert(row[at], source.From, source.To) : null).ToArray())
            .ToList();
        return new FlatTable(table.Name, columns, rows);
    }

    /// <summary>True when every value of the first kind can be written as the second without losing anything.</summary>
    private static bool Fits(ColumnKind from, ColumnKind to) =>
        from == to
        || to == ColumnKind.Text
        || (from, to) is (ColumnKind.Integer, ColumnKind.Number) or (ColumnKind.Date, ColumnKind.DateTime);

    private static object? Convert(object? value, ColumnKind from, ColumnKind to) => value switch
    {
        null => null,
        _ when from == to => value,
        long whole when to == ColumnKind.Number => (double)whole,
        DateTime date when to == ColumnKind.DateTime => date,
        bool flag when to == ColumnKind.Text => flag ? "true" : "false",
        DateTime time when to == ColumnKind.Text => from == ColumnKind.Date
            ? time.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : time.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture),
        IFormattable number when to == ColumnKind.Text => number.ToString(null, CultureInfo.InvariantCulture),
        _ => value,
    };

    private static JsonObject Describe(ExportedTable table, Settings settings, Store store)
    {
        // A child table such as event_bookings_attendees comes from its parent's download.
        var source = Endpoint.All
            .Where(endpoint => table.Name == endpoint.Table || table.Name.StartsWith(endpoint.Table + "_", StringComparison.Ordinal))
            .OrderByDescending(endpoint => endpoint.Table.Length)
            .FirstOrDefault();
        return new JsonObject
        {
            ["table"] = table.Name,
            ["rows"] = table.Rows,
            ["columns"] = table.Columns.Count,
            ["downloaded_from"] = source?.Path,
            ["last_synced_at"] = source is null ? null : store.GetState(source.Table).Watermark?.ToLocalTime().ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture),
            ["anonymised"] = settings.Anonymise,
            ["instance"] = settings.Instance,
        };
    }
}
