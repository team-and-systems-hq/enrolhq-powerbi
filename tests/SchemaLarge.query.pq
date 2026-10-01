// Column names and types of the large tables, anonymised, entry year 2027 only. No values.
let
    nav = EnrolHQ.Contents("{{instance}}", "2027", "Yes"),
    keys = {"applications", "form_submissions", "event_bookings", "event_booking_attendees", "event_booking_students"},
    schema = (key as text) as table =>
        let
            data = Table.Buffer(nav{[Key = key]}[Data]),
            columns = Table.Schema(data)[[Name], [TypeName]],
            filled = Table.AddColumn(columns, "NonBlank", each List.Count(List.Select(Table.Column(data, [Name]), (v) => v <> null and v <> "")))
        in
            Table.AddColumn(Table.AddColumn(filled, "Table", each key), "Rows", each Table.RowCount(data))
in
    Table.Combine(List.Transform(keys, schema))[[Table], [Rows], [Name], [TypeName], [NonBlank]]
