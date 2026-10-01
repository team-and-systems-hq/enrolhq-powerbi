// Column names and types of the small tables, anonymised. No values.
let
    nav = EnrolHQ.Contents("{{instance}}", null, "Yes"),
    keys = {"staff", "campuses", "application_statuses", "attendance_types", "forms", "events", "event_sessions"},
    schema = (key as text) as table =>
        let
            data = nav{[Key = key]}[Data],
            columns = Table.Schema(data)[[Name], [TypeName]]
        in
            Table.AddColumn(Table.AddColumn(columns, "Table", each key), "Rows", each Table.RowCount(data))
in
    Table.Combine(List.Transform(keys, schema))[[Table], [Rows], [Name], [TypeName]]
