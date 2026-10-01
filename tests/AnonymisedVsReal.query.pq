// Loads applications with and without anonymisation and compares them field by
// field. Returns counts and field names only, never values.
let
    real = EnrolHQ.Contents("{{instance}}"){[Key = "applications"]}[Data],
    fake = EnrolHQ.Contents("{{instance}}", null, "Yes"){[Key = "applications"]}[Data],
    realRows = Table.ToRecords(real),
    fakeById = Record.FromList(Table.ToRecords(fake), List.Transform(fake[id], Text.From)),
    fields = {"first_name", "last_name", "middle_name", "preferred_name", "email", "mobile_phone", "home_phone"},
    parents = {"user_parent", "non_user_parent"},
    same = (a as any, b as any) as logical => a is text and a <> "" and a = b,
    unchanged = (realRecord as any, fakeRecord as any, prefix as text) as list =>
        if not (realRecord is record) or not (fakeRecord is record) then {}
        else List.Select(
            List.Transform(fields, (field) =>
                if same(Record.FieldOrDefault(realRecord, field, null), Record.FieldOrDefault(fakeRecord, field, null))
                then prefix & field else null),
            each _ <> null),
    perRow = List.Transform(realRows, (row) =>
        let
            masked = Record.FieldOrDefault(fakeById, Text.From(row[id]), null)
        in
            if masked = null then {"(row missing from anonymised table)"}
            else unchanged(row, masked, "") & List.Combine(List.Transform(parents, (parent) =>
                unchanged(Record.FieldOrDefault(row, parent, null), Record.FieldOrDefault(masked, parent, null), parent & ".")))),
    all = List.Combine(perRow),
    summary = Table.Group(Table.FromList(all, Splitter.SplitByNothing(), {"Field"}), {"Field"}, {{"UnchangedValues", Table.RowCount, Int64.Type}}),
    result =
        if Table.RowCount(real) <> Table.RowCount(fake) then error "Anonymised table has a different number of rows."
        else if List.IsEmpty(all) then #table({"Check", "Rows"}, {{"No personal value is unchanged after anonymisation", Table.RowCount(real)}})
        else error Error.Record("DataSource.Error", "Some values were unchanged after anonymisation.", summary)
in
    result
