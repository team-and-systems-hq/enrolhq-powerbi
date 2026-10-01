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
        var written = new List<ExportedTable>();
        foreach (var endpoint in Endpoint.All)
        {
            foreach (var table in Flattener.Flatten(endpoint.Table, store.Read(endpoint.Table)))
            {
                reporter.Progress($"writing {table.Name}: {Reporter.Number(table.Rows.Count)} rows");
                await ParquetExporter.WriteAsync(table, settings.ParquetDirectory, cancel);
                written.Add(new ExportedTable(table.Name, table.Rows.Count, table.Columns));
            }
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

        return written;
    }

    private static JsonObject Describe(ExportedTable table, Settings settings, Store store)
    {
        // A child table such as event_bookings_attendees comes from its parent's download.
        var source = Endpoint.All
            .Where(endpoint => table.Name == endpoint.Table || table.Name.StartsWith(endpoint.Table + "_", StringComparison.Ordinal))
            .OrderByDescending(endpoint => endpoint.Table.Length)
            .First();
        return new JsonObject
        {
            ["table"] = table.Name,
            ["rows"] = table.Rows,
            ["columns"] = table.Columns.Count,
            ["downloaded_from"] = source.Path,
            ["last_synced_at"] = store.GetState(source.Table).Watermark?.ToLocalTime().ToString("yyyy-MM-dd'T'HH:mm:ss"),
            ["anonymised"] = settings.Anonymise,
            ["instance"] = settings.Instance,
        };
    }
}
