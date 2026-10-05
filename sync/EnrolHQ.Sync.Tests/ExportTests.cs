using System.Text.Json.Nodes;
using Parquet;

namespace EnrolHQ.Sync.Tests;

/// <summary>A Power BI project that a school has added to must keep loading as the data changes.</summary>
public sealed class ExportTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "enrolhq-sync-tests", Guid.NewGuid().ToString("N"));
    private readonly Settings _settings;
    private readonly Store _store;
    private readonly Reporter _reporter = new(logDirectory: null, DateTimeOffset.UtcNow);

    public ExportTests()
    {
        _settings = new Settings("enrol.school.edu.au", "secret", Anonymise: true, _folder);
        _store = new Store(_settings.DatabasePath);
    }

    public void Dispose()
    {
        _store.Dispose();
        _reporter.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_folder, recursive: true);
    }

    private void Hold(string table, params string[] records)
    {
        var cycle = _store.StartCycle(table, updatedAfter: null, DateTimeOffset.UtcNow).Cycle;
        var parsed = records.Select(json => (JsonObject)JsonNode.Parse(json)!).ToList();
        _store.SavePage(table, cycle, 1, parsed.Count, parsed.Select(record => ((string)record["id"]!, record)), DateTimeOffset.UtcNow);
        _store.CompleteCycle(table, cycle, removeUnseen: true, DateTimeOffset.UtcNow);
    }

    private async Task<List<ExportedTable>> ExportAndWriteProjectAsync()
    {
        var tables = await Exporter.ExportAsync(_settings, _store, _reporter, newProject: false, CancellationToken.None);
        PowerBiProject.Write(_settings, tables, overwrite: false);
        return tables;
    }

    private async Task<(string[] Columns, long Rows)> ReadParquetAsync(string table)
    {
        await using var stream = File.OpenRead(Path.Combine(_settings.ParquetDirectory, table + ".parquet"));
        await using var reader = await ParquetReader.CreateAsync(stream);
        var rows = Enumerable.Range(0, reader.RowGroupCount).Sum(group => reader.OpenRowGroupReader(group).RowCount);
        return (reader.Schema.GetDataFields().Select(field => field.Name).ToArray(), rows);
    }

    [Fact]
    public async Task A_table_that_empties_still_has_a_file_with_the_columns_the_project_expects()
    {
        Hold("leads", """{"id":"l1","lead_status":2}""");
        await ExportAndWriteProjectAsync();

        Hold("leads");
        await Exporter.ExportAsync(_settings, _store, _reporter, newProject: false, CancellationToken.None);

        var (columns, rows) = await ReadParquetAsync("leads");
        Assert.Equal(["id", "lead_status"], columns);
        Assert.Equal(0, rows);
    }

    [Fact]
    public async Task A_column_the_project_expects_is_still_written_when_the_data_no_longer_has_it()
    {
        Hold("staff", """{"id":"s1","is_active":true,"nickname_count":1}""");
        await ExportAndWriteProjectAsync();

        Hold("staff", """{"id":"s1","is_active":true,"roles":"r1"}""");
        var tables = await Exporter.ExportAsync(_settings, _store, _reporter, newProject: false, CancellationToken.None);

        var staff = tables.Single(table => table.Name == "staff");
        Assert.Equal(["id", "is_active", "nickname_count", "roles"], staff.Columns.Select(column => column.Name));
        Assert.Equal(ColumnKind.Integer, staff.Columns.Single(column => column.Name == "nickname_count").Kind);
    }

    [Fact]
    public async Task Exporting_for_a_new_project_drops_columns_only_the_old_project_had()
    {
        Hold("staff", """{"id":"s1","is_active":true,"nickname_count":1}""");
        await ExportAndWriteProjectAsync();

        Hold("staff", """{"id":"s1","is_active":true}""");
        var tables = await Exporter.ExportAsync(_settings, _store, _reporter, newProject: true, CancellationToken.None);

        Assert.Equal(["id", "is_active"], tables.Single(table => table.Name == "staff").Columns.Select(column => column.Name));
        Assert.Equal(["id", "is_active"], (await ReadParquetAsync("staff")).Columns);
    }

    [Fact]
    public async Task A_column_the_project_reads_as_text_is_written_as_text()
    {
        Hold("forms", """{"id":"f1","fee":"x"}""");
        await ExportAndWriteProjectAsync();

        Hold("forms", """{"id":"f1","fee":5}""");
        var tables = await Exporter.ExportAsync(_settings, _store, _reporter, newProject: false, CancellationToken.None);

        // The project reads fee as text; a number written as text loses nothing.
        Assert.Equal(ColumnKind.Text, tables.Single(table => table.Name == "forms").Columns.Single(column => column.Name == "fee").Kind);
    }

    [Fact]
    public async Task A_project_whose_pbip_file_was_renamed_is_never_overwritten()
    {
        Hold("staff", """{"id":"s1"}""");
        var tables = await ExportAndWriteProjectAsync();
        var folder = PowerBiProject.FolderFor(_settings);
        File.Move(PowerBiProject.ProjectFileFor(_settings), Path.Combine(folder, "Admissions dashboard.pbip"));
        var page = Path.Combine(folder, "EnrolHQ.Report", "definition", "pages", "school-page.json");
        File.WriteAllText(page, "the school's own page");

        var written = PowerBiProject.Write(_settings, tables, overwrite: false);

        Assert.False(written);
        Assert.True(File.Exists(page));
    }

    [Fact]
    public async Task Replacing_a_project_swaps_in_a_complete_one()
    {
        Hold("staff", """{"id":"s1"}""");
        var tables = await ExportAndWriteProjectAsync();

        Assert.True(PowerBiProject.Write(_settings, tables, overwrite: true));

        var folder = PowerBiProject.FolderFor(_settings);
        Assert.True(File.Exists(PowerBiProject.ProjectFileFor(_settings)));
        Assert.True(File.Exists(Path.Combine(folder, "EnrolHQ.SemanticModel", "definition", "tables", "staff.tmdl")));
        Assert.Empty(Directory.EnumerateDirectories(folder, "*.old").Concat(Directory.EnumerateDirectories(folder, "*.new")));
    }

    [Fact]
    public async Task Exporting_an_empty_copy_says_so()
    {
        var tables = await Exporter.ExportAsync(_settings, _store, _reporter, newProject: false, CancellationToken.None);

        Assert.Empty(tables);
    }
}
