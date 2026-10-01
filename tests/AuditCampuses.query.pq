// Field audit: which JSON paths exist, and which of them anonymisation changes.
// Reports field names, types and counts only, never values.
let
    key = "campuses",
    real = List.FirstN(Table.ToRecords(EnrolHQ.Contents("{{instance}}"){[Key = key]}[Data]), 1000),
    masked = EnrolHQ.Anonymise(real, key),
    typeName = (v as any) as text =>
        if v = null then "null" else if v is text then "text" else if v is number then "number"
        else if v is logical then "logical" else "other",
    walk = (r as any, m as any, path as text) as list =>
        if r is record then
            List.Combine(List.Transform(Record.FieldNames(r), (k) =>
                @walk(Record.Field(r, k), if m is record then Record.FieldOrDefault(m, k, null) else null, path & "." & k)))
        else if r is list then
            List.Combine(List.Transform(List.Positions(r), (i) =>
                @walk(r{i}, if m is list and List.Count(m) > i then m{i} else null, path & "[]")))
        else
            {[Path = path, Type = typeName(r), Value = r, HasValue = r <> null and r <> "", Changed = r <> null and r <> "" and r <> m]},
    leaves = Table.FromRecords(List.Combine(List.Transform(List.Positions(real), (i) => walk(real{i}, masked{i}, key)))),
    grouped = Table.Group(leaves, {"Path"}, {
        {"Types", each Text.Combine(List.Sort(List.Distinct([Type])), "/"), type text},
        {"WithValue", each List.Count(List.Select([HasValue], each _)), Int64.Type},
        {"Masked", each List.Count(List.Select([Changed], each _)), Int64.Type},
        {"Distinct", each List.Count(List.Distinct(List.RemoveNulls([Value]))), Int64.Type},
        {"MaxLength", each List.Max(List.Transform([Value], (v) => if v is text then Text.Length(v) else 0)), Int64.Type}
    }),
    result = Table.Sort(grouped, {"Path"})
in
    result
