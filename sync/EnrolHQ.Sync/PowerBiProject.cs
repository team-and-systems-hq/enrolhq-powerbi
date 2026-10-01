using System.Text;

namespace EnrolHQ.Sync;

/// <summary>
/// Writes a Power BI project (.pbip) over the Parquet files: every table, the
/// relationships between them, and a starter set of measures. Opening it in
/// Power BI Desktop and refreshing loads the local copy; it never calls the
/// EnrolHQ API.
///
/// The project is only a starting point. Once a school has added its own
/// measures and report pages it is theirs, so an existing project is never
/// overwritten unless asked.
/// </summary>
internal static class PowerBiProject
{
    public const string Name = "EnrolHQ";

    /// <summary>
    /// The level Power BI Desktop itself saves (August 2026 release). Desktop
    /// raises a lower level to its own when it opens a project, and then
    /// cannot reload the project's files, because a level can never go down.
    /// </summary>
    private const int CompatibilityLevel = 1606;

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Lookups between tables that come from different endpoints, as
    /// (from table, from column, to table, to column, active). Links from a
    /// child table to its parent are worked out from the table names.
    /// </summary>
    private static readonly (string FromTable, string FromColumn, string ToTable, string ToColumn, bool Active)[] Lookups =
    [
        ("applications", "application_status", "application_statuses", "application_status", true),
        ("applications", "campus", "campuses", "id", true),
        ("applications", "attendance_type", "attendance_types", "id", true),
        ("application_details", "id", "applications", "id", true),
        ("events_sessions", "event_id", "events", "id", true),
        ("event_bookings", "session", "events_sessions", "id", true),
        ("event_bookings", "event", "events", "id", true),
        ("event_bookings_student_profiles", "student_profile_id", "applications", "id", true),
        ("form_submissions", "form_id", "forms", "id", true),
        ("form_submissions", "student_profile_id", "applications", "id", true),
    ];

    /// <summary>
    /// Lookups that matter less than the links between a table and its
    /// children. They are added last, so when one would give two routes
    /// between the same tables it is the one switched off.
    /// </summary>
    private static readonly (string FromTable, string FromColumn, string ToTable, string ToColumn, bool Active)[] MinorLookups =
    [
        ("events", "campus", "campuses", "id", true),
    ];

    private static readonly Dictionary<string, string> Descriptions = new(StringComparer.Ordinal)
    {
        ["applications"] = "One row per student application (summary). Personal details of the student and both parents, entry year and grade, status, and offer progress.",
        ["application_details"] = "One row per student application with every field EnrolHQ holds. Joins to applications on id.",
        ["application_statuses"] = "The school's own name for each application status. Use status_label, not the status number.",
        ["campuses"] = "The school's campuses.",
        ["attendance_types"] = "Attendance types, for example day student or boarder.",
        ["events"] = "Events such as tours and open days.",
        ["events_sessions"] = "The sessions of each event.",
        ["event_bookings"] = "One row per booking for an event session.",
        ["event_bookings_attendees"] = "The people attending under each event booking.",
        ["event_bookings_student_profiles"] = "Which students (applications) each event booking is for.",
        ["forms"] = "Custom forms the school has set up.",
        ["form_submissions"] = "One row per form a parent has started or completed. Answers are not included.",
        ["staff"] = "Staff accounts.",
        ["leads"] = "Enquiries that are not yet applications.",
        ["sync_info"] = "When each table was last downloaded from EnrolHQ, and whether the data is anonymised.",
    };

    public static string FolderFor(Settings settings) => Path.Combine(settings.DataDirectory, "powerbi");

    public static string ProjectFileFor(Settings settings) => Path.Combine(FolderFor(settings), Name + ".pbip");

    /// <returns>True when the project was written; false when one already exists and was left alone.</returns>
    public static bool Write(Settings settings, IReadOnlyList<ExportedTable> tables, bool overwrite)
    {
        var folder = FolderFor(settings);
        if (File.Exists(ProjectFileFor(settings)) && !overwrite)
        {
            return false;
        }

        var model = Path.Combine(folder, Name + ".SemanticModel");
        var report = Path.Combine(folder, Name + ".Report");
        foreach (var generated in new[] { model, report })
        {
            if (Directory.Exists(generated))
            {
                Directory.Delete(generated, recursive: true);
            }
        }

        var relationships = Relationships(tables);

        Save(ProjectFileFor(settings), ProjectFile);
        Save(Path.Combine(folder, ".gitignore"), "**/.pbi/localSettings.json\n**/.pbi/cache.abf\n");

        Save(Path.Combine(model, "definition.pbism"), ModelProperties);
        Save(Path.Combine(model, "definition", "database.tmdl"), $"database\n\tcompatibilityLevel: {CompatibilityLevel}\n");
        Save(Path.Combine(model, "definition", "model.tmdl"), Model(tables));
        Save(Path.Combine(model, "definition", "expressions.tmdl"), Expressions(settings));
        Save(Path.Combine(model, "definition", "relationships.tmdl"), string.Join("\n", relationships.Select(Relationship)));
        foreach (var table in tables)
        {
            Save(Path.Combine(model, "definition", "tables", table.Name + ".tmdl"), Table(table));
        }

        Save(Path.Combine(report, "definition.pbir"), ReportProperties);
        Save(Path.Combine(report, "definition", "version.json"), ReportVersion);
        Save(Path.Combine(report, "definition", "report.json"), Report);
        Save(Path.Combine(report, "definition", "pages", "pages.json"), Pages);
        Save(Path.Combine(report, "definition", "pages", PageName, "page.json"), Page);
        return true;
    }

    /// <summary>
    /// The relationships, most important first. Power BI refuses a model in
    /// which filters can travel between two tables by more than one route, so
    /// a relationship that would open a second route is kept but switched off.
    /// A measure can still use it with USERELATIONSHIP.
    /// </summary>
    internal static List<(string FromTable, string FromColumn, string ToTable, string ToColumn, bool Active)> Relationships(
        IReadOnlyList<ExportedTable> tables)
    {
        var byName = tables.ToDictionary(table => table.Name, StringComparer.Ordinal);
        bool Has(string table, string column) =>
            byName.TryGetValue(table, out var found) && found.Columns.Any(candidate => candidate.Name == column);

        var wanted = Lookups.Concat(ChildLinks(tables, Has)).Concat(MinorLookups)
            .Where(candidate => Has(candidate.FromTable, candidate.FromColumn) && Has(candidate.ToTable, candidate.ToColumn));

        // Filters travel from the "to" table (one row per key) to the "from" table (many rows per key).
        var reaches = tables.ToDictionary(table => table.Name, table => new HashSet<string> { table.Name }, StringComparer.Ordinal);
        var result = new List<(string, string, string, string, bool)>();
        foreach (var candidate in wanted)
        {
            var sources = reaches.Where(entry => entry.Value.Contains(candidate.ToTable)).Select(entry => entry.Key).ToList();
            var targets = reaches[candidate.FromTable];
            var secondRoute = sources.Any(source => targets.Any(target => reaches[source].Contains(target)));
            if (secondRoute)
            {
                result.Add(candidate with { Active = false });
                continue;
            }

            foreach (var source in sources)
            {
                reaches[source].UnionWith(targets);
            }

            result.Add(candidate);
        }

        return result;
    }

    /// <summary>
    /// A child table links to its parent: event_bookings_attendees.event_booking_id
    /// to event_bookings.id. The parent is the longest table name the child's name starts with.
    /// </summary>
    private static IEnumerable<(string FromTable, string FromColumn, string ToTable, string ToColumn, bool Active)> ChildLinks(
        IReadOnlyList<ExportedTable> tables,
        Func<string, string, bool> has)
    {
        foreach (var child in tables)
        {
            var parent = tables
                .Where(candidate => child.Name.StartsWith(candidate.Name + "_", StringComparison.Ordinal))
                .OrderByDescending(candidate => candidate.Name.Length)
                .FirstOrDefault();
            if (parent is null)
            {
                continue;
            }

            var link = Flattener.Singular(parent.Name) + "_id";
            if (has(child.Name, link) && !Lookups.Any(lookup => lookup.FromTable == child.Name && lookup.FromColumn == link))
            {
                yield return (child.Name, link, parent.Name, "id", true);
            }
        }
    }

    private static string Model(IReadOnlyList<ExportedTable> tables)
    {
        var text = new StringBuilder();
        text.Append("model Model\n\tculture: en-AU\n\tdefaultPowerBIDataSourceVersion: powerBI_V3\n\tsourceQueryCulture: en-AU\n\n");
        foreach (var table in tables)
        {
            text.Append($"ref table {Quote(table.Name)}\n");
        }

        return text.ToString();
    }

    /// <summary>The folder is a parameter, so the project still works if the local copy is moved.</summary>
    private static string Expressions(Settings settings) =>
        $"/// The parquet folder written by enrolhq-sync.\n"
        + $"expression DataFolder = \"{settings.ParquetDirectory.Replace("\"", "\"\"")}\" "
        + "meta [IsParameterQuery=true, Type=\"Text\", IsParameterQueryRequired=true]\n";

    private static string Table(ExportedTable table)
    {
        var text = new StringBuilder();
        if (Descriptions.TryGetValue(table.Name, out var description))
        {
            text.Append($"/// {description}\n");
        }

        text.Append($"table {Quote(table.Name)}\n\n");
        foreach (var measure in Measures(table))
        {
            text.Append($"\tmeasure {Quote(measure.Name)} = {measure.Expression}\n");
            text.Append($"\t\tformatString: {measure.Format}\n\n");
        }

        foreach (var column in table.Columns)
        {
            text.Append($"\tcolumn {Quote(column.Name)}\n");
            text.Append($"\t\tdataType: {DataType(column.Kind)}\n");
            if (FormatString(column.Kind) is { } format)
            {
                text.Append($"\t\tformatString: {format}\n");
            }

            // Ids, years and grades are numbers that must never be added up.
            text.Append("\t\tsummarizeBy: none\n");
            text.Append($"\t\tsourceColumn: {column.Name}\n\n");
        }

        text.Append($"\tpartition {Quote(table.Name)} = m\n");
        text.Append("\t\tmode: import\n");
        text.Append("\t\tsource =\n");
        text.Append("\t\t\t\tlet\n");
        text.Append($"\t\t\t\t\tSource = Parquet.Document(File.Contents(DataFolder & \"\\{table.Name}.parquet\"))\n");
        text.Append("\t\t\t\tin\n");
        text.Append("\t\t\t\t\tSource\n");
        return text.ToString();
    }

    private static IEnumerable<(string Name, string Expression, string Format)> Measures(ExportedTable table)
    {
        bool Has(string column) => table.Columns.Any(candidate => candidate.Name == column);
        const string Whole = "#,0";

        switch (table.Name)
        {
            case "applications":
                yield return ("Applications", "COUNTROWS(applications)", Whole);
                if (Has("progress_enrolment_offer_made_at") && Has("progress_enrolment_offer_completed_at"))
                {
                    yield return (
                        "Enrolment offers made",
                        "CALCULATE(COUNTROWS(applications), NOT ISBLANK(applications[progress_enrolment_offer_made_at]))",
                        Whole);
                    yield return (
                        "Enrolment offers accepted",
                        "CALCULATE(COUNTROWS(applications), NOT ISBLANK(applications[progress_enrolment_offer_completed_at]))",
                        Whole);
                    yield return ("Offer acceptance rate", "DIVIDE([Enrolment offers accepted], [Enrolment offers made])", "0.0%");
                }

                break;
            case "event_bookings":
                yield return ("Event bookings", "COUNTROWS(event_bookings)", Whole);
                break;
            case "event_bookings_student_profiles":
                yield return (
                    "Students who booked an event",
                    "DISTINCTCOUNT(event_bookings_student_profiles[student_profile_id])",
                    Whole);
                break;
            case "form_submissions":
                yield return ("Form submissions", "COUNTROWS(form_submissions)", Whole);
                if (Has("completed_at"))
                {
                    yield return (
                        "Form submissions completed",
                        "CALCULATE(COUNTROWS(form_submissions), NOT ISBLANK(form_submissions[completed_at]))",
                        Whole);
                }

                break;
        }
    }

    private static string Relationship((string FromTable, string FromColumn, string ToTable, string ToColumn, bool Active) relationship)
    {
        var name = $"{relationship.FromTable}_{relationship.FromColumn}_to_{relationship.ToTable}";
        return $"relationship {Quote(name)}\n"
            + (relationship.Active ? "" : "\tisActive: false\n")
            + $"\tfromColumn: {Quote(relationship.FromTable)}.{Quote(relationship.FromColumn)}\n"
            + $"\ttoColumn: {Quote(relationship.ToTable)}.{Quote(relationship.ToColumn)}\n";
    }

    private static string DataType(ColumnKind kind) => kind switch
    {
        ColumnKind.Integer => "int64",
        ColumnKind.Number => "double",
        ColumnKind.Logical => "boolean",
        ColumnKind.Date or ColumnKind.DateTime => "dateTime",
        _ => "string",
    };

    private static string? FormatString(ColumnKind kind) => kind switch
    {
        ColumnKind.Integer => "0",
        ColumnKind.Date => "Short Date",
        ColumnKind.DateTime => "General Date",
        _ => null,
    };

    /// <summary>TMDL needs quotes around a name that has anything but letters, digits and underscores.</summary>
    private static string Quote(string name) =>
        name.All(character => char.IsAsciiLetterOrDigit(character) || character == '_')
            ? name
            : "'" + name.Replace("'", "''") + "'";

    /// <summary>Power BI expects UTF-8 without a byte order mark, and Windows line endings.</summary>
    private static void Save(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content.ReplaceLineEndings("\r\n"), Utf8NoBom);
    }

    private const string PageName = "overview";

    private const string ProjectFile =
        """
        {
          "$schema": "https://developer.microsoft.com/json-schemas/fabric/pbip/pbipProperties/1.0.0/schema.json",
          "version": "1.0",
          "artifacts": [
            {
              "report": {
                "path": "EnrolHQ.Report"
              }
            }
          ],
          "settings": {
            "enableAutoRecovery": true
          }
        }
        """;

    private const string ModelProperties =
        """
        {
          "$schema": "https://developer.microsoft.com/json-schemas/fabric/item/semanticModel/definitionProperties/1.0.0/schema.json",
          "version": "4.2",
          "settings": {}
        }
        """;

    private const string ReportProperties =
        """
        {
          "$schema": "https://developer.microsoft.com/json-schemas/fabric/item/report/definitionProperties/2.0.0/schema.json",
          "version": "4.0",
          "datasetReference": {
            "byPath": {
              "path": "../EnrolHQ.SemanticModel"
            }
          }
        }
        """;

    private const string ReportVersion =
        """
        {
          "$schema": "https://developer.microsoft.com/json-schemas/fabric/item/report/definition/versionMetadata/1.0.0/schema.json",
          "version": "2.0.0"
        }
        """;

    private const string Report =
        """
        {
          "$schema": "https://developer.microsoft.com/json-schemas/fabric/item/report/definition/report/3.3.0/schema.json",
          "themeCollection": {},
          "settings": {
            "useEnhancedTooltips": false
          }
        }
        """;

    private const string Pages =
        """
        {
          "$schema": "https://developer.microsoft.com/json-schemas/fabric/item/report/definition/pagesMetadata/1.0.0/schema.json",
          "pageOrder": [
            "overview"
          ],
          "activePageName": "overview"
        }
        """;

    private const string Page =
        """
        {
          "$schema": "https://developer.microsoft.com/json-schemas/fabric/item/report/definition/page/2.0.0/schema.json",
          "name": "overview",
          "displayName": "Overview",
          "displayOption": "FitToPage",
          "height": 720,
          "width": 1280
        }
        """;
}
