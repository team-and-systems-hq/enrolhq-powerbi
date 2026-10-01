// PQTest only returns the first rows of a table, so count them here.
let
    nav = EnrolHQ.Contents("{{instance}}"),
    keys = {"applications", "leads", "events", "event_bookings", "forms", "form_submissions", "staff"},
    counts = List.Transform(keys, (key) => {key, Table.RowCount(nav{[Key = key]}[Data]), Table.ColumnCount(nav{[Key = key]}[Data])})
in
    #table(type table [Table = text, Rows = Int64.Type, Columns = Int64.Type], counts)
