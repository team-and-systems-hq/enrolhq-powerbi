// Leak scan on live data: looks for each person's real first name, surname and
// email anywhere in their anonymised row, including inside other fields.
// Reports field paths and counts only, never values.
let
    key = "applications",
    real = List.FirstN(Table.ToRecords(EnrolHQ.Contents("{{instance}}"){[Key = key]}[Data]), 2000),
    masked = EnrolHQ.Anonymise(real, key),
    // Real names and emails of everyone named in a row. Short names are skipped
    // because they match ordinary words and suburbs by chance.
    people = (value as any) as list =>
        if value is record then
            List.Select(
                List.Transform({"first_name", "last_name", "email"}, (field) => Record.FieldOrDefault(value, field, null)),
                each _ is text and Text.Length(_) >= 5)
            & List.Combine(List.Transform(Record.FieldValues(value), @people))
        else if value is list then List.Combine(List.Transform(value, @people))
        else {},
    leaves = (value as any, path as text) as list =>
        if value is record then
            List.Combine(List.Transform(Record.FieldNames(value), (k) => @leaves(Record.Field(value, k), path & "." & k)))
        else if value is list then List.Combine(List.Transform(value, (item) => @leaves(item, path & "[]")))
        else if value is text and value <> "" then {[Path = path, Text = Text.Lower(value)]}
        else {},
    scan = (index as number) as list =>
        let
            secrets = List.Distinct(List.Transform(people(real{index}), Text.Lower)),
            found = List.Select(leaves(masked{index}, key), (leaf) => List.AnyTrue(List.Transform(secrets, (secret) => Text.Contains(leaf[Text], secret))))
        in
            List.Transform(found, each [Path]),
    hits = List.Combine(List.Transform(List.Positions(real), scan)),
    summary = Table.Group(Table.FromList(hits, Splitter.SplitByNothing(), {"Path"}), {"Path"}, {{"RowsContainingARealValue", Table.RowCount, Int64.Type}}),
    result = Table.Sort(summary, {{"RowsContainingARealValue", Order.Descending}})
in
    result
