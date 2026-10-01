namespace EnrolHQ.Sync.Tests;

public sealed class PowerBiProjectFileTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "enrolhq-sync-tests", Guid.NewGuid().ToString("N"));
    private readonly Settings _settings;

    private static readonly ExportedTable[] Tables =
    [
        new("applications", 2, [
            new Column("id", ColumnKind.Text),
            new Column("entry_year", ColumnKind.Integer),
            new Column("dob", ColumnKind.Date),
            new Column("progress_enrolment_offer_made_at", ColumnKind.DateTime),
            new Column("progress_enrolment_offer_completed_at", ColumnKind.DateTime),
            new Column("is_favorite", ColumnKind.Logical),
            new Column("fee", ColumnKind.Number),
        ]),
        new("applications_custom_categories_options", 3, [
            new Column("application_id", ColumnKind.Text),
            new Column("custom_categories_option_id", ColumnKind.Text),
        ]),
    ];

    public PowerBiProjectFileTests()
    {
        _settings = new Settings("enrol.school.edu.au", "secret", Anonymise: true, _folder);
    }

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    private string Read(params string[] path) =>
        File.ReadAllText(Path.Combine([PowerBiProject.FolderFor(_settings), .. path]));

    /// <summary>
    /// Power BI Desktop raises a lower level to its own on opening, and then
    /// fails every reload with "CompatibilityLevel downgrade".
    /// </summary>
    [Fact]
    public void Uses_the_compatibility_level_power_bi_desktop_saves()
    {
        PowerBiProject.Write(_settings, Tables, overwrite: false);

        Assert.Equal("database\r\n\tcompatibilityLevel: 1606\r\n", Read("EnrolHQ.SemanticModel", "definition", "database.tmdl"));
    }

    [Fact]
    public void Reads_each_table_from_its_parquet_file_in_the_data_folder()
    {
        PowerBiProject.Write(_settings, Tables, overwrite: false);

        var table = Read("EnrolHQ.SemanticModel", "definition", "tables", "applications.tmdl");
        Assert.Contains("Parquet.Document(File.Contents(DataFolder & \"\\applications.parquet\"))", table);
        Assert.Contains($"expression DataFolder = \"{_settings.ParquetDirectory}\"", Read("EnrolHQ.SemanticModel", "definition", "expressions.tmdl"));
    }

    [Fact]
    public void Gives_each_column_its_type_and_never_adds_numbers_up_by_default()
    {
        PowerBiProject.Write(_settings, Tables, overwrite: false);

        var table = Read("EnrolHQ.SemanticModel", "definition", "tables", "applications.tmdl");
        Assert.Contains("\tcolumn entry_year\r\n\t\tdataType: int64\r\n\t\tformatString: 0\r\n\t\tsummarizeBy: none\r\n\t\tsourceColumn: entry_year\r\n", table);
        Assert.Contains("\tcolumn dob\r\n\t\tdataType: dateTime\r\n\t\tformatString: Short Date\r\n", table);
        Assert.Contains("\tcolumn is_favorite\r\n\t\tdataType: boolean\r\n", table);
        Assert.Contains("\tcolumn fee\r\n\t\tdataType: double\r\n", table);
        Assert.DoesNotContain("summarizeBy: sum", table);
    }

    [Fact]
    public void Adds_measures_only_when_their_columns_exist()
    {
        PowerBiProject.Write(_settings, Tables, overwrite: false);
        Assert.Contains("measure 'Offer acceptance rate'", Read("EnrolHQ.SemanticModel", "definition", "tables", "applications.tmdl"));

        var withoutOffers = Tables.Select(table => table with
        {
            Columns = table.Columns.Where(column => !column.Name.StartsWith("progress_", StringComparison.Ordinal)).ToList(),
        }).ToList();
        PowerBiProject.Write(_settings, withoutOffers, overwrite: true);

        var applications = Read("EnrolHQ.SemanticModel", "definition", "tables", "applications.tmdl");
        Assert.Contains("measure Applications = COUNTROWS(applications)", applications);
        Assert.DoesNotContain("Offer", applications);
    }

    [Fact]
    public void Leaves_an_existing_project_alone_unless_told_to_replace_it()
    {
        Assert.True(PowerBiProject.Write(_settings, Tables, overwrite: false));
        var table = Path.Combine(PowerBiProject.FolderFor(_settings), "EnrolHQ.SemanticModel", "definition", "tables", "applications.tmdl");
        File.AppendAllText(table, "\tmeasure 'Added by the school' = 1\r\n");

        Assert.False(PowerBiProject.Write(_settings, Tables, overwrite: false));
        Assert.Contains("Added by the school", File.ReadAllText(table));

        Assert.True(PowerBiProject.Write(_settings, Tables, overwrite: true));
        Assert.DoesNotContain("Added by the school", File.ReadAllText(table));
    }

    [Fact]
    public void Writes_files_the_way_power_bi_expects()
    {
        PowerBiProject.Write(_settings, Tables, overwrite: false);

        foreach (var file in Directory.EnumerateFiles(PowerBiProject.FolderFor(_settings), "*", SearchOption.AllDirectories))
        {
            var bytes = File.ReadAllBytes(file);
            Assert.False(bytes.Take(3).SequenceEqual(new byte[] { 0xEF, 0xBB, 0xBF }), $"{file} starts with a byte order mark");
            var text = File.ReadAllText(file);
            Assert.False(text.Replace("\r\n", "").Contains('\n'), $"{file} has a line ending that is not CRLF");
            if (file.EndsWith(".json", StringComparison.Ordinal) || Path.GetFileName(file).StartsWith("definition.", StringComparison.Ordinal) || file.EndsWith(".pbip", StringComparison.Ordinal))
            {
                Assert.NotNull(System.Text.Json.JsonDocument.Parse(text));
            }
        }
    }
}
