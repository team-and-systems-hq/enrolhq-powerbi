// EnrolHQ connector for Power BI / Power Query.
// Read-only: the connector only ever issues GET requests, plus the POST that
// exchanges the long-lived API token for a short-lived access token.
[Version = "0.3.0"]
section EnrolHQ;

DefaultPageSize = 200;
// applications/ returns the full record for each application and allows at
// most 100 per page.
DetailPageSize = 100;
MaxPages = 10000;

// ── Data source ──────────────────────────────────────────────────────────────

[DataSource.Kind = "EnrolHQ", Publish = "EnrolHQ.Publish"]
shared EnrolHQ.Contents = Value.ReplaceType(ContentsImpl, ContentsType);

ContentsType = type function (
    instance as (type text meta [
        Documentation.FieldCaption = "EnrolHQ address",
        Documentation.FieldDescription = "Your school's EnrolHQ address, for example enrol.yourschool.edu.au or yourschool.enrolhq.com.au.",
        Documentation.SampleValues = {"yourschool.enrolhq.com.au"}
    ]),
    optional entryYears as (type nullable text meta [
        Documentation.FieldCaption = "Entry years (optional)",
        Documentation.FieldDescription = "Comma-separated entry years to load, for example 2026,2027. Leave blank to load every year.",
        Documentation.SampleValues = {"2026,2027"}
    ]),
    optional anonymise as (type nullable text meta [
        Documentation.FieldCaption = "Anonymise personal data",
        Documentation.FieldDescription = "Yes replaces names, contact details, identifiers, free text and file links with fake values before the data is loaded. Suburb, state, postcode and dates of birth are kept.",
        Documentation.AllowedValues = {"No", "Yes"}
    ])
) as table meta [
    Documentation.Name = "EnrolHQ",
    Documentation.LongDescription = "Loads applications, leads, events, form submissions and reference data from an EnrolHQ instance, optionally anonymised."
];

ContentsImpl = (instance as text, optional entryYears as nullable text, optional anonymise as nullable text) as table =>
    let
        baseUrl = NormalizeBaseUrl(instance),
        years = ParseEntryYears(entryYears),
        anonymiseOn = ParseYesNo(anonymise),
        resolved = ResolveToken(baseUrl),
        ctx = [
            BaseUrl = baseUrl,
            Token = resolved[Token],
            IsApiToken = resolved[IsApiToken],
            Anonymise = anonymiseOn
        ],
        // M is lazy, so the options are checked and the token resolved here.
        // A bad option, address or token then fails at connect time, not
        // halfway through loading a table.
        optionsValid = anonymiseOn <> null and List.Count(years) >= 0,
        nav = if optionsValid and ctx[Token] <> null then NavigationTable(ctx, years) else null
    in
        nav;

EnrolHQ = [
    // The function has optional parameters, so the data source path arrives
    // as JSON: {"instance":"..."}.
    TestConnection = (dataSourcePath) => {"EnrolHQ.Contents", Json.Document(dataSourcePath)[instance]},
    Authentication = [
        Key = [
            KeyLabel = "Access token",
            Label = "EnrolHQ access token"
        ]
    ],
    Label = "EnrolHQ"
];

EnrolHQ.Publish = [
    Beta = true,
    Category = "Online Services",
    ButtonText = {"EnrolHQ", "Load enrolment and admissions data from your school's EnrolHQ instance."},
    LearnMoreUrl = "https://www.enrolhq.com.au"
];

// ── Local copy ───────────────────────────────────────────────────────────────
//
// Reads the Parquet files written by enrolhq-sync. Nothing here calls the
// EnrolHQ API, so loading is fast, needs no token and cannot be rate limited.

[DataSource.Kind = "EnrolHQLocal", Publish = "EnrolHQLocal.Publish"]
shared EnrolHQ.LocalCopy = Value.ReplaceType(LocalCopyImpl, LocalCopyType);

LocalCopyType = type function (
    folder as (type text meta [
        Documentation.FieldCaption = "Folder",
        Documentation.FieldDescription = "The parquet folder written by enrolhq-sync.",
        Documentation.SampleValues = {"C:\enrolhq\data\enrol.yourschool.edu.au\anonymised\parquet"}
    ])
) as table meta [
    Documentation.Name = "EnrolHQ local copy",
    Documentation.LongDescription = "Loads the local copy of a school's EnrolHQ data kept up to date by enrolhq-sync."
];

LocalCopyImpl = (folder as text) as table =>
    let
        root = Text.TrimEnd(Text.Trim(folder), {"\", "/"}) & "\",
        files = Table.SelectRows(
            Folder.Files(root),
            each Text.Lower([Extension]) = ".parquet" and Text.Lower([Folder Path]) = Text.Lower(root)
        ),
        sorted = Table.Sort(files, {"Name"}),
        entities = Table.FromRecords(
            List.Transform(
                Table.ToRecords(sorted),
                (file) =>
                    let
                        name = Text.BeforeDelimiter(file[Name], ".", {0, RelativePosition.FromEnd})
                    in
                        [
                            Name = name,
                            Key = name,
                            Data = Parquet.Document(file[Content]),
                            ItemKind = "Table",
                            ItemName = "Table",
                            IsLeaf = true
                        ]
            ),
            type table [Name = text, Key = text, Data = table, ItemKind = text, ItemName = text, IsLeaf = logical]
        ),
        nav =
            if Table.IsEmpty(files) then
                error Error.Record(
                    "DataSource.Error",
                    "No Parquet files in that folder. Run enrolhq-sync first, then choose its parquet folder.",
                    folder
                )
            else
                Table.ToNavigationTable(entities, {"Key"}, "Name", "Data", "ItemKind", "ItemName", "IsLeaf")
    in
        nav;

// Files are read as the signed-in Windows user, the same as Power BI's own
// Folder source.
EnrolHQLocal = [
    Authentication = [Windows = []],
    Label = "EnrolHQ local copy"
];

EnrolHQLocal.Publish = [
    Beta = true,
    Category = "Other",
    ButtonText = {"EnrolHQ local copy", "Load the local copy of your school's EnrolHQ data kept by enrolhq-sync."},
    LearnMoreUrl = "https://www.enrolhq.com.au"
];

// Masks rows exactly as the connector does when Anonymise is Yes. Exposed so
// the masking rules can be tested without calling the API.
shared EnrolHQ.Anonymise = (rows as list, tableKey as text) as list => Anon.Rows(rows, tableKey);

// ── Navigation ───────────────────────────────────────────────────────────────

NavigationTable = (ctx as record, years as list) as table =>
    let
        // The suffix carries through to the table name in the model, so anyone
        // opening the report can see the data is not real.
        suffix = if ctx[Anonymise] then " (anonymised)" else "",
        entities = #table(
            {"Name", "Key", "Data", "ItemKind", "ItemName", "IsLeaf"},
            {
                {"Applications" & suffix, "applications", Applications(ctx, years), "Table", "Table", true},
                {"Application details (slow)" & suffix, "application_details", ApplicationDetails(ctx, years), "Table", "Table", true},
                {"Leads" & suffix, "leads", PagedTable(ctx, "leads", "leads/", []), "Table", "Table", true},
                {"Events" & suffix, "events", PagedTable(ctx, "events", "staff-events/", []), "Table", "Table", true},
                {"Event sessions" & suffix, "event_sessions", ChildTable(PagedRecords(ctx, "staff-events/", []), ctx, "events", "sessions", "event_id", "value"), "Table", "Table", true},
                {"Event bookings" & suffix, "event_bookings", PagedTable(ctx, "event_bookings", "staff-event-bookings/", []), "Table", "Table", true},
                {"Event booking attendees" & suffix, "event_booking_attendees", ChildTable(PagedRecords(ctx, "staff-event-bookings/", []), ctx, "event_bookings", "attendees", "event_booking_id", "value"), "Table", "Table", true},
                {"Event booking students" & suffix, "event_booking_students", ChildTable(PagedRecords(ctx, "staff-event-bookings/", []), ctx, "event_bookings", "student_profiles", "event_booking_id", "student_profile_id"), "Table", "Table", true},
                {"Forms" & suffix, "forms", PagedTable(ctx, "forms", "forms/staff/", []), "Table", "Table", true},
                {"Form submissions" & suffix, "form_submissions", PagedTableByYear(ctx, "form_submissions", "forms/staff-submits/", years), "Table", "Table", true},
                {"Staff" & suffix, "staff", PagedTable(ctx, "staff", "staff/", []), "Table", "Table", true},
                {"Application statuses" & suffix, "application_statuses", PagedTable(ctx, "application_statuses", "application-status-settings/", []), "Table", "Table", true},
                {"Campuses" & suffix, "campuses", PagedTable(ctx, "campuses", "school-campuses/", []), "Table", "Table", true},
                {"Attendance types" & suffix, "attendance_types", PagedTable(ctx, "attendance_types", "attendance-types/", []), "Table", "Table", true},
                {"Lead references" & suffix, "lead_references", PagedTable(ctx, "lead_references", "lead-references/", []), "Table", "Table", true}
            }
        ),
        nav = Table.ToNavigationTable(entities, {"Key"}, "Name", "Data", "ItemKind", "ItemName", "IsLeaf")
    in
        nav;

// ── Tables ───────────────────────────────────────────────────────────────────

Applications = (ctx as record, years as list) as table =>
    PagedTableByYear(ctx, "applications", "applications-list/", years);

// applications-list/ returns a summary. Guardians, emergency contacts, medical
// data and custom form submit ids are only on the full record, which
// applications/ returns page by page. That endpoint has no entry year filter,
// so the years are applied here.
ApplicationDetails = (ctx as record, years as list) as table =>
    let
        all = PagedRecords(ctx, "applications/", [], DetailPageSize),
        wanted =
            if List.IsEmpty(years) then all
            else List.Select(all, (row) => List.Contains(years, Text.From(Record.FieldOrDefault(row, "entry_year", ""))))
    in
        ToTable(wanted, ctx, "application_details");

PagedTable = (ctx as record, tableKey as text, path as text, query as record) as table =>
    ToTable(PagedRecords(ctx, path, query), ctx, tableKey);

PagedTableByYear = (ctx as record, tableKey as text, path as text, years as list) as table =>
    ToTable(PagedRecordsByYear(ctx, path, years), ctx, tableKey);

// entry_year accepts a comma-separated list.
PagedRecordsByYear = (ctx as record, path as text, years as list) as list =>
    PagedRecords(ctx, path, if List.IsEmpty(years) then [] else [entry_year = Text.Combine(years, ",")]);

// Every table is built from these rows, so this is the single place masking
// is applied. Masking runs on the nested records, before they are flattened,
// because the rules depend on where a field sits.
PreparedRows = (records as list, ctx as record, tableKey as text) as list =>
    let
        source = List.Buffer(records)
    in
        if ctx[Anonymise] then Anon.Rows(source, tableKey) else source;

ToTable = (records as list, ctx as record, tableKey as text) as table =>
    Flat.Table(PreparedRows(records, ctx, tableKey));

// One row per item of a list inside each parent row, for example one row per
// attendee of each event booking. parentColumn carries the parent's id.
ChildTable = (records as list, ctx as record, tableKey as text, listKey as text, parentColumn as text, valueColumn as text) as table =>
    let
        parents = PreparedRows(records, ctx, tableKey),
        children = List.Combine(
            List.Transform(
                parents,
                (parent) =>
                    let
                        items = Record.FieldOrDefault(parent, listKey, null),
                        link = Record.FromList({Record.FieldOrDefault(parent, "id", null)}, {parentColumn})
                    in
                        if items is list then
                            List.Transform(
                                items,
                                (item) => if item is record then link & item else link & Record.FromList({item}, {valueColumn})
                            )
                        else
                            {}
            )
        )
    in
        Flat.Table(children);

// ── Flattening and typing ────────────────────────────────────────────────────

// Nested records become prefixed columns (user_parent.email -> user_parent_email),
// lists of plain values become comma-separated text, and lists of records
// become a count (their rows are served as separate tables where useful).
// Column types are worked out from the values, because which fields exist
// varies from school to school.
Flat.Table = (rows as list) as table =>
    let
        flat = List.Buffer(List.Transform(rows, (row) => if row is record then Flat.Record(row, "") else [value = row])),
        names = List.Distinct(List.Combine(List.Transform(flat, Record.FieldNames))),
        raw = Table.Buffer(Table.FromRecords(flat, names, MissingField.UseNull)),
        kinds = List.Transform(names, (name) => Flat.Kind(Table.Column(raw, name))),
        // "non_user_parent" is null on rows with no second parent and expands to
        // non_user_parent_* on the rest, leaving an empty column behind.
        isLeftover = (index as number) as logical =>
            kinds{index} = "empty"
            and List.MatchesAny(names, (other) => Text.StartsWith(other, names{index} & "_")),
        kept = List.Select(List.Positions(names), (index) => not isLeftover(index)),
        selected = Table.SelectColumns(raw, List.Transform(kept, (index) => names{index})),
        typed = Table.TransformColumns(
            selected,
            List.Transform(kept, (index) => {names{index}, Flat.Converter(kinds{index}), Flat.Type(kinds{index})})
        )
    in
        if List.IsEmpty(flat) then #table({}, {}) else typed;

Flat.Record = (source as record, prefix as text) as record =>
    Record.Combine(
        List.Transform(
            Record.FieldNames(source),
            (key) =>
                let
                    value = Record.Field(source, key),
                    name = prefix & key
                in
                    if value is record then Flat.Record(value, name & "_")
                    else if value is list then
                        (
                            if List.MatchesAny(value, each _ is record or _ is list) then
                                Record.FromList({List.Count(value)}, {name & "_count"})
                            else if List.IsEmpty(value) then
                                Record.FromList({null}, {name})
                            else
                                Record.FromList(
                                    {Text.Combine(List.Transform(value, each if _ = null then "" else Text.From(_)), ", ")},
                                    {name}
                                )
                        )
                    else Record.FromList({value}, {name})
        )
    );

Flat.Kind = (values as list) as text =>
    let
        present = List.Select(values, each _ <> null and _ <> "")
    in
        if List.IsEmpty(present) then "empty"
        else if List.MatchesAll(present, each _ is logical) then "logical"
        else if List.MatchesAll(present, each _ is number) then
            (if List.MatchesAll(present, each _ = Number.RoundDown(_)) then "integer" else "number")
        else if List.MatchesAll(present, each _ is text) then
            (
                if List.MatchesAll(present, Flat.IsDate) then "date"
                else if List.MatchesAll(present, Flat.IsDateTime) then "datetime"
                else "text"
            )
        else "mixed";

Flat.IsDate = (value as text) as logical =>
    Text.Length(value) = 10
    and Text.At(value, 4) = "-"
    and Text.At(value, 7) = "-"
    and Text.Length(Text.Select(value, {"0".."9"})) = 8;

Flat.IsDateTime = (value as text) as logical =>
    Text.Length(value) >= 19 and Text.At(value, 10) = "T" and Flat.IsDate(Text.Start(value, 10));

Flat.Type = (kind as text) as type =>
    if kind = "logical" then type nullable logical
    else if kind = "integer" then type nullable Int64.Type
    else if kind = "number" then type nullable number
    else if kind = "date" then type nullable date
    else if kind = "datetime" then type nullable datetime
    else type nullable text;

Flat.Converter = (kind as text) as function =>
    if kind = "logical" or kind = "integer" or kind = "number" then
        (value) => if value = "" then null else value
    else if kind = "date" then
        (value) => if value = null or value = "" then null else (try Date.FromText(value) otherwise null)
    // Times are kept as the school's local clock time; the offset is dropped.
    else if kind = "datetime" then
        (value) =>
            if value = null or value = "" then null
            else (try DateTimeZone.RemoveZone(DateTimeZone.FromText(value)) otherwise null)
    else
        (value) => if value = null then null else Text.From(value);

// ── Paging ───────────────────────────────────────────────────────────────────

// Page-number pagination: { count, next, previous, results }. Endpoints that
// return a bare list are treated as a single page.
PagedRecords = (ctx as record, path as text, query as record, optional pageSize as nullable number) as list =>
    let
        size = Text.From(if pageSize = null then DefaultPageSize else pageSize),
        getPage = (page as number) => GetJson(ctx, path, query & [page = Text.From(page), page_size = size]),
        hasNext = (body) => body is record and Record.FieldOrDefault(body, "next", null) <> null,
        resultsOf = (body) =>
            if body is list then body
            else if body is record then Record.FieldOrDefault(body, "results", {})
            else {},
        pages = List.Generate(
            () => [Page = 1, Body = getPage(1)],
            each [Body] <> null and [Page] <= MaxPages,
            each
                if hasNext([Body]) then [Page = [Page] + 1, Body = getPage([Page] + 1)]
                else [Page = [Page] + 1, Body = null],
            each resultsOf([Body])
        )
    in
        List.Combine(pages);

// ── HTTP ─────────────────────────────────────────────────────────────────────

// EnrolHQ allows 5 token exchanges a minute, and each exchange cancels the
// access token issued before it. Power BI evaluates every table separately,
// several times, so exchanging the API token in each evaluation both hits that
// limit and cancels the token other tables are still using.
//
// The credential can therefore be an access token, which is used as it is and
// never exchanged. If the credential is the long-lived API token it is
// exchanged once per evaluation, which is only suitable for loading one table
// at a time.
ResolveToken = (baseUrl as text) as record =>
    let
        key = Extension.CurrentCredential()[Key],
        probePath = "attendance-types/",
        probe = SendGet(baseUrl, probePath, [page_size = "1"], key, false),
        status = Value.Metadata(probe)[Response.Status]
    in
        if status < 400 then [Token = key, IsApiToken = false]
        else if status = 401 or status = 403 then [Token = GetAccessToken(baseUrl, false), IsApiToken = true]
        else error ApiError(status, probePath, probe);

GetJson = (ctx as record, path as text, query as record) as any =>
    let
        first = SendGet(ctx[BaseUrl], path, query, ctx[Token], false),
        firstStatus = Value.Metadata(first)[Response.Status],
        // Access tokens last an hour. An API token can be exchanged again; an
        // access token given as the credential cannot.
        response =
            if firstStatus = 401 and ctx[IsApiToken] then
                SendGet(ctx[BaseUrl], path, query, GetAccessToken(ctx[BaseUrl], true), true)
            else
                first,
        status = Value.Metadata(response)[Response.Status],
        result =
            if status = 401 and not ctx[IsApiToken] then
                error Error.Record(
                    "DataSource.Error",
                    "The EnrolHQ access token has expired or been replaced. Get a new one and update the credential under Data source settings.",
                    [Status = status, Path = path]
                )
            else if status >= 400 then error ApiError(status, path, response)
            else Json.Document(response)
    in
        result;

// Waits and retries while EnrolHQ answers 429 (too many requests). The last
// attempt is returned whatever its status. 5xx is left to the engine's
// built-in retry handling.
SendWithRetry = (send as function, waits as list) as binary =>
    Value.WaitFor(
        (attempt) =>
            let
                response = send(attempt > 0),
                status = Value.Metadata(response)[Response.Status]
            in
                if status = 429 and attempt < List.Count(waits) - 1 then null else response,
        (attempt) => #duration(0, 0, 0, waits{attempt}),
        List.Count(waits)
    );

// Data requests are limited to 5 a second.
SendGet = (baseUrl as text, path as text, query as record, token as text, isRetry as logical) as binary =>
    SendWithRetry(
        (again as logical) =>
            Web.Contents(
                baseUrl,
                [
                    RelativePath = path,
                    Query = query,
                    Headers = [Authorization = "Token " & token, Accept = "application/json"],
                    ManualCredentials = true,
                    ManualStatusHandling = {400, 401, 403, 404, 429},
                    IsRetry = isRetry or again
                ]
            ),
        {0, 2, 4, 8, 16, 32}
    );

// Exchanges the long-lived API token for a short-lived access token. Limited
// to 5 a minute, so the waits are long.
GetAccessToken = (baseUrl as text, isRetry as logical) as text =>
    let
        response = SendWithRetry(
            (again as logical) =>
                Web.Contents(
                    baseUrl,
                    [
                        RelativePath = "accounts/refresh/",
                        Headers = [
                            Authorization = "Token " & Extension.CurrentCredential()[Key],
                            Accept = "application/json"
                        ],
                        Content = Text.ToBinary(""),
                        ManualCredentials = true,
                        ManualStatusHandling = {400, 401, 403, 404, 429},
                        IsRetry = isRetry or again
                    ]
                ),
            {0, 15, 15, 15, 15}
        ),
        status = Value.Metadata(response)[Response.Status],
        body = try Json.Document(response) otherwise null,
        token = if body is record then Record.FieldOrDefault(body, "access_token", null) else null,
        result =
            if status = 401 or status = 403 then
                error Error.Record(
                    "DataSource.Error",
                    "EnrolHQ did not accept the token. Check it is current and was copied in full.",
                    [Status = status]
                )
            else if status = 429 then
                error Error.Record(
                    "DataSource.Error",
                    "EnrolHQ allows 5 sign-ins a minute and that limit has been reached. Wait a minute, then load one table at a time, or use an access token as the credential.",
                    [Status = status]
                )
            else if status >= 400 then
                error Error.Record(
                    "DataSource.Error",
                    "Could not sign in to EnrolHQ at " & baseUrl & ". Check the EnrolHQ address.",
                    [Status = status]
                )
            else if token = null then
                error Error.Record(
                    "DataSource.Error",
                    "EnrolHQ did not return an access token. Check the EnrolHQ address.",
                    [Status = status]
                )
            else
                token
    in
        result;

ApiError = (status as number, path as text, response as binary) as record =>
    let
        bodyText = try Text.FromBinary(response) otherwise "",
        json = try Json.Document(bodyText) otherwise null,
        detail = if json is record then Record.FieldOrDefault(json, "detail", bodyText) else bodyText,
        message =
            if status = 401 then "EnrolHQ did not accept the access token for " & path & "."
            else if status = 403 then "The API token does not have permission to read " & path & "."
            else if status = 404 then "EnrolHQ has no endpoint " & path & " on this instance."
            else if status = 429 then
                "EnrolHQ is limiting requests and " & path & " could not be loaded. Load fewer tables at once: set Parallel loading of tables to One."
            else "EnrolHQ rejected the request for " & path & "."
    in
        Error.Record("DataSource.Error", message, [Status = status, Path = path, Detail = detail]);

// ── Inputs ───────────────────────────────────────────────────────────────────

// Accepts "demo", "demo.enrolhq.com.au", "enrol.school.edu.au" or a full URL.
// Only the host is kept, and https is always used, so the API token is never
// sent anywhere but the instance's own API.
NormalizeBaseUrl = (instance as text) as text =>
    let
        trimmed = Text.Trim(instance),
        lower = Text.Lower(trimmed),
        withScheme =
            if Text.StartsWith(lower, "https://") then trimmed
            else if Text.StartsWith(lower, "http://") then "https://" & Text.Middle(trimmed, 7)
            else "https://" & trimmed,
        host = Text.Lower(Uri.Parts(withScheme)[Host]),
        fullHost = if Text.Contains(host, ".") then host else host & ".enrolhq.com.au",
        result =
            if host = "" then
                error Error.Record("Expression.Error", "Enter your school's EnrolHQ address.", instance)
            else
                "https://" & fullHost & "/api/v2"
    in
        result;

ParseEntryYears = (entryYears as nullable text) as list =>
    let
        parts =
            if entryYears = null then {}
            else List.Select(List.Transform(Text.Split(entryYears, ","), Text.Trim), each _ <> ""),
        toYear = (part as text) as text =>
            let
                number = try Number.FromText(part) otherwise null
            in
                if number = null or number <> Number.RoundDown(number) or number < 1900 or number > 2200 then
                    error Error.Record(
                        "Expression.Error",
                        "Entry years must be comma-separated years, for example 2026,2027.",
                        part
                    )
                else
                    Text.From(number)
    in
        List.Distinct(List.Transform(parts, toYear));

// Anything other than a clear Yes or No is an error, so a typo can never
// silently load real data.
ParseYesNo = (value as nullable text) as logical =>
    let
        normalised = if value = null then "" else Text.Lower(Text.Trim(value))
    in
        if normalised = "" or normalised = "no" then false
        else if normalised = "yes" then true
        else error Error.Record("Expression.Error", "Anonymise personal data must be Yes or No.", value);

// ── Anonymisation ────────────────────────────────────────────────────────────
//
// Rules are per JSON key, because that is what the API returns.
//
// Dates of birth, external_id and student_code are kept on purpose: schools
// need dates of birth for reporting and the ids to match their own systems.
//
// Fake values are not picked at random: they are derived from a hash of the
// record's id and the field name. The same student or parent therefore gets
// the same fake values in every table and on every refresh, so tables still
// join and reports stay stable. The fake value depends only on the id, never
// on the real value.

// Masks every row, then checks the result. If any masked field does not look
// masked, the whole table fails and nothing is loaded.
Anon.Rows = (rows as list, tableKey as text) as list =>
    let
        masked = List.Buffer(
            List.Transform(
                List.Positions(rows),
                (index) => Anon.Value(rows{index}, tableKey & "#" & Text.From(index), tableKey, tableKey)
            )
        ),
        violations = List.Combine(List.Transform(masked, (row) => Anon.Violations(row, tableKey, tableKey, tableKey))),
        result =
            if List.IsEmpty(violations) then
                masked
            else
                error Error.Record(
                    "DataSource.Error",
                    "Anonymisation check failed, so nothing was loaded.",
                    [Table = tableKey, Count = List.Count(violations), Fields = List.FirstN(List.Distinct(violations), 20)]
                )
    in
        result;

Anon.Value = (value as any, seed as text, parent as text, tableKey as text) as any =>
    if value is record then
        Anon.Record(value, seed, parent, tableKey)
    else if value is list then
        List.Transform(
            List.Positions(value),
            (index) => Anon.Value(value{index}, seed & "/" & Text.From(index), parent, tableKey)
        )
    else
        value;

Anon.Record = (source as record, seed as text, parent as text, tableKey as text, optional sameAs as text) as record =>
    let
        ownSeed = if sameAs <> null then sameAs else Anon.SeedFor(source, seed, parent),
        isPerson = Anon.IsPerson(source),
        names = Record.FieldNames(source),
        values = List.Transform(
            names,
            (key) =>
                let
                    value = Record.Field(source, key),
                    rule = Anon.RuleFor(parent, key, isPerson)
                in
                    if rule = "Sid" then Mask.Sid(value, source, ownSeed)
                    else if rule <> null then Anon.Apply(rule, value, ownSeed & "|" & key, key, tableKey, ownSeed)
                    // A parent's alumnus record is the parent, so it gets the parent's fake name.
                    else if key = "alumnus" and value is record then Anon.Record(value, ownSeed, key, tableKey, ownSeed)
                    else Anon.Value(value, ownSeed & "/" & key, key, tableKey)
        )
    in
        Record.FromList(values, names);

// A UUID identifies one person wherever they appear, so it is the seed on its
// own. Numeric ids are only unique within their own kind of record.
Anon.SeedFor = (source as record, seed as text, parent as text) as text =>
    let
        id = Record.FieldOrDefault(source, "id", null),
        // An event attendee who is a parent carries the parent's id, and is the same person.
        person = if Anon.IsPerson(source) then Record.FieldOrDefault(source, "parent", null) else null
    in
        if person is text and Text.Length(person) >= 32 then person
        else if id is text and Text.Length(id) >= 32 then id
        else if id is text or id is number then parent & ":" & Text.From(id)
        else seed;

Anon.IsPerson = (source as record) as logical =>
    Record.HasFields(source, "first_name") or Record.HasFields(source, "last_name") or Record.HasFields(source, "full_name");

Anon.RuleFor = (parent as text, key as text, isPerson as logical) as nullable text =>
    let
        context = parent & "." & key
    in
        if List.Contains(Anon.Keep, context) then null
        else if Record.HasFields(Anon.ContextRules, context) then Record.Field(Anon.ContextRules, context)
        else if isPerson and Record.HasFields(Anon.PersonRules, key) then Record.Field(Anon.PersonRules, key)
        else if Record.HasFields(Anon.KeyRules, key) then Record.Field(Anon.KeyRules, key)
        // Every entry under cms_images is a file link.
        else if parent = "cms_images" then "FileKey"
        // custom_field_1, custom_field_2, ... hold answers as text, a list or
        // a nested object.
        else if Text.StartsWith(key, "custom_field_") then "RedactAll"
        // religion_other, how_hear_other, ... are typed in by parents, and on
        // live data some contain the family's own names.
        else if Text.EndsWith(key, "_other") then "Redact"
        else null;

Anon.Apply = (rule as text, value as any, seed as text, key as text, tableKey as text, recordSeed as text) as any =>
    if value = null then null
    else if rule = "Null" then null
    else if rule = "EmptyJson" then (if value is list then {} else if value is record then [] else "{}")
    else if rule = "EmptyValues" then Mask.EmptyValues(value)
    else if rule = "RedactAll" then Mask.RedactLeaves(value)
    else if value is list then
        List.Transform(
            List.Positions(value),
            (index) => Anon.Apply(rule, value{index}, seed & "/" & Text.From(index), key, tableKey, recordSeed)
        )
    // A record where text was expected: nothing in it is kept.
    else if value is record then Mask.RedactLeaves(value)
    else Anon.Scalar(rule, Text.From(value), seed, key, recordSeed);

// Empty stays empty, as in the SQL masks.
Anon.Scalar = (rule as text, value as text, seed as text, key as text, recordSeed as text) as text =>
    if rule = "Blank" or value = "" then ""
    else if rule = "FirstName" then Mask.Pick(Names.First, seed)
    else if rule = "LastName" then Mask.Pick(Names.Last, seed)
    // full_name is the record's own person, so it matches their first_name and
    // last_name. Other name fields, such as case_manager_name, are someone else.
    else if rule = "FullName" then Mask.FullName(if key = "full_name" then recordSeed else seed)
    else if rule = "MobilePhone" then Mask.Phone(seed, "mobile")
    else if rule = "HomePhone" or rule = "BusinessPhone" then Mask.Phone(seed, "landline")
    else if rule = "Email" then Mask.Email(seed)
    else if rule = "Digits" then Mask.Digits(value, seed)
    else if rule = "Redact" then "Redacted"
    else if rule = "FileKey" then "anonymized/removed-file" & Mask.Extension(value)
    else if rule = "FileName" then "removed-file" & Mask.Extension(value)
    else if rule = "Street" then "Fake Street, 42"
    else if rule = "Apartment" then "F101"
    else error Error.Record("Expression.Error", "Unknown anonymisation rule.", rule);

// ── Anonymisation: verification ──────────────────────────────────────────────

// Walks masked output and returns the path of every field that has a masking
// rule but does not look masked. Paths only; values are never reported.
Anon.Violations = (value as any, path as text, parent as text, tableKey as text) as list =>
    if value is record then
        let
            isPerson = Anon.IsPerson(value)
        in
            List.Combine(
                List.Transform(
                    Record.FieldNames(value),
                    (key) =>
                        let
                            field = Record.Field(value, key),
                            rule = Anon.RuleFor(parent, key, isPerson),
                            fieldPath = path & "." & key
                        in
                            if rule <> null then Anon.RuleViolations(rule, field, fieldPath, key, tableKey)
                            else Anon.Violations(field, fieldPath, key, tableKey)
                )
            )
    else if value is list then
        List.Combine(List.Transform(value, (item) => Anon.Violations(item, path & "[]", parent, tableKey)))
    else
        {};

Anon.RuleViolations = (rule as text, value as any, path as text, key as text, tableKey as text) as list =>
    if value = null then {}
    else if rule = "Null" then {path}
    else if rule = "EmptyJson" then
        (if (value is list and List.IsEmpty(value)) or (value is record and Record.FieldCount(value) = 0) or value = "{}" then {} else {path})
    else if rule = "EmptyValues" then (if value = Mask.EmptyValues(value) then {} else {path})
    else if rule = "RedactAll" then (if value = Mask.RedactLeaves(value) then {} else {path})
    else if value is list then
        List.Combine(List.Transform(value, (item) => Anon.RuleViolations(rule, item, path & "[]", key, tableKey)))
    else if value is record then (if value = Mask.RedactLeaves(value) then {} else {path})
    else if not (value is text) then {path}
    else if Anon.Conforms(rule, value) then {}
    else {path};

Anon.Conforms = (rule as text, value as text) as logical =>
    if value = "" then true
    else if rule = "Blank" then false
    else if rule = "FirstName" then List.Contains(Names.First, value)
    else if rule = "LastName" then List.Contains(Names.Last, value)
    else if rule = "FullName" then
        let
            parts = Text.Split(value, " ")
        in
            List.Count(parts) = 2 and List.Contains(Names.First, parts{0}) and List.Contains(Names.Last, parts{1})
    else if rule = "MobilePhone" then List.Contains(Mask.FictionalMobiles, value)
    else if rule = "HomePhone" or rule = "BusinessPhone" then
        Text.Length(value) = 12 and List.Contains(Mask.FictionalLandlinePrefixes, Text.Start(value, 8)) and Mask.IsDigits(Text.Middle(value, 8))
    else if rule = "Email" then
        List.AnyTrue(List.Transform(Mask.EmailDomains, (domain) => Text.EndsWith(value, "@" & domain)))
    else if rule = "Digits" then Mask.IsDigits(value)
    else if rule = "Sid" then
        let
            parts = Text.Split(value, "-"),
            count = List.Count(parts)
        in
            count >= 3
            and Mask.IsDigits(parts{count - 1})
            and List.Contains(List.Transform(Names.First, each Text.Start(_, 1)), parts{count - 2})
            and List.Contains(List.Transform(Names.Last, Text.Upper), parts{count - 3})
    else if rule = "Redact" then value = "Redacted"
    else if rule = "FileKey" then Text.StartsWith(value, "anonymized/removed-file")
    else if rule = "FileName" then Text.StartsWith(value, "removed-file")
    else if rule = "Street" then value = "Fake Street, 42"
    else if rule = "Apartment" then value = "F101"
    else false;

// ── Anonymisation: rules ─────────────────────────────────────────────────────

// Applied wherever the key appears.
Anon.KeyRules = [
    // Names
    first_name = "FirstName",
    last_name = "LastName",
    snapshot_first_name = "FirstName",
    snapshot_last_name = "LastName",
    full_name = "FullName",
    case_manager_name = "FullName",
    parish_priest_name = "FullName",
    student_profile_name = "FullName",
    parent_name = "FullName",
    // Staff named on a campus
    principal_name = "FullName",
    registrar_name = "FullName",
    // Built from the real surname and first initial
    sid = "Sid",
    // Contact
    email = "Email",
    mobile_phone = "MobilePhone",
    contact_phone = "MobilePhone",
    home_phone = "HomePhone",
    business_phone = "BusinessPhone",
    // Government, health and other identifiers
    usi = "Digits",
    vsn = "Digits",
    nesa_number = "Digits",
    medicare_number = "Digits",
    medicare_card_reference_number = "Digits",
    health_fund_member_number = "Digits",
    ambulance_fund_number = "Digits",
    health_care_card_number = "Digits",
    passport_number = "Digits",
    working_with_children_number = "Digits",
    // Free text
    custody_details = "Redact",
    cultural_requirements = "Redact",
    symptoms_treatment = "Redact",
    medication = "Redact",
    other_label = "Redact",
    enrichments_details = "Redact",
    music_details = "Redact",
    sport_details = "Redact",
    missed_periods = "Redact",
    special_needs_other = "Redact",
    current_school_provides_other = "Redact",
    current_school_provides_required_other = "Redact",
    professional_support_other = "Redact",
    undiagnosed_learning_difficulties = "Redact",
    dietary_requirements_other = "Redact",
    custom_block_1_details = "Redact",
    custom_block_2_details = "Redact",
    custom_block_3_details = "Redact",
    current_school_history = "Redact",
    postal_address = "Redact",
    form_note = "Redact",
    comment = "Redact",
    // Document groups sit under a different parent key for each kind of document.
    note = "Redact",
    medical_conditions_other = "Redact",
    // Free text and computed values that normally contain a person's name
    parents_salutation = "Redact",
    correspondence_addressee_other = "Redact",
    student_resides_with_other = "Redact",
    how_hear_personal_referral = "Redact",
    // Often a family business named after the parent
    employer = "Redact",
    updated_by = "Redact",
    deleted_by = "Redact",
    // Written by staff for families joining an interview: links, meeting
    // ids and staff contact details
    meeting_instructions = "Redact",
    custom_form_payer_names = "RedactAll",
    // Payment references embed the sid, and for some gateways the payer's
    // name, email or date of birth
    reference_id = "Redact",
    transaction_number = "Redact",
    result_text = "Redact",
    // File references
    avatar = "FileKey",
    avatar_src = "FileKey",
    image_src = "FileKey",
    attachment_src = "FileKey",
    principal_signature_src = "FileKey",
    logo_image_src = "FileKey",
    header_image_src = "FileKey",
    enrolment_review_scan_pdf = "FileKey",
    file = "FileKey",
    image = "FileKey",
    attachment = "FileKey",
    principal_signature = "FileKey",
    export_file = "FileKey",
    header_image = "FileKey",
    logo_image = "FileKey",
    hint_image = "FileKey",
    form_pdf = "FileKey",
    event_booking_pdf = "FileKey",
    filename = "FileName",
    attachment_file_names = "FileName",
    // Addresses: suburb, state and postcode are kept for statistics
    street_address = "Street",
    apartment = "Apartment",
    // IP addresses
    submission_ip = "Null",
    ip_addr = "Null",
    user_parent_acceptance_ip = "Null",
    non_user_parent_acceptance_ip = "Null",
    // Signed links that let the holder accept an offer or sign a form
    enrolment_accept_link = "Null",
    gpa_accept_link = "Null",
    accept_link = "Null",
    // A link that lets the holder join a meeting, and the signed token
    // that lets the holder book into an event from its kiosk page
    online_meeting_link = "Null",
    public_token = "Null",
    // Free-form JSON
    payload = "EmptyJson",
    initial_payload = "EmptyJson",
    token_request_data = "EmptyJson",
    request_data = "EmptyJson",
    // Questionnaire answers: keys kept, values cleared
    questionnaire = "EmptyValues",
    preschool_questionnaire = "EmptyValues",
    abilities_questionnaire = "EmptyValues",
    event_booking_questionnaire = "EmptyValues"
];

// Applied only on records that have a first or last name.
Anon.PersonRules = [
    middle_name = "Blank",
    preferred_name = "Blank",
    traditional_name = "Blank"
];

// Keys whose meaning depends on where they sit, as "parent.key". For top-level
// fields the parent is the table key.
Anon.ContextRules = [
    #"applications.title" = "Blank",
    #"application_details.title" = "Blank",
    #"student_profile.title" = "Blank",
    #"student.title" = "Blank",
    #"doctor.name" = "FullName",
    #"doctors.name" = "FullName",
    #"doctor.telephone" = "BusinessPhone",
    #"doctors.telephone" = "BusinessPhone",
    #"dentist.name" = "FullName",
    #"dentist.telephone" = "BusinessPhone",
    #"signature.name" = "FullName",
    #"signatures.name" = "FullName",
    #"notes.text" = "Redact",
    #"activity_log.description" = "Redact",
    #"interview.notes" = "Redact",
    #"interviews.notes" = "Redact",
    #"document_group.note" = "Redact",
    #"document_groups.note" = "Redact",
    #"agreement_documents.link" = "FileKey",
    // Written by staff for families coming to an interview
    #"location.hint_text" = "Redact",
    #"email_log.subject" = "Redact"
];

// Not personal: a campus's own contact address.
Anon.Keep = {"campuses.email", "campus.email"};

// ── Anonymisation: masks ─────────────────────────────────────────────────────

Mask.EmailDomains = {"example.com", "example.net", "example.org"};
// Numbers the ACMA sets aside for fiction, so a fake number never rings a real person.
Mask.FictionalMobiles = {
    "+61491570006", "+61491570156", "+61491570157", "+61491570158", "+61491570159", "+61491570110", "+61491570313",
    "+61491570737", "+61491571266", "+61491571491", "+61491571804", "+61491572549", "+61491572665", "+61491572983",
    "+61491573770", "+61491573087", "+61491574118", "+61491574632", "+61491575254", "+61491575789", "+61491576398"
};
Mask.FictionalLandlinePrefixes = {"+6125550", "+6127010", "+6135550", "+6137010", "+6175550", "+6177010", "+6185550", "+6187010"};
// Extensions kept on a removed file's name. Anything else after a dot may be part of a name.
Mask.FileExtensions = {
    "pdf", "doc", "docx", "xls", "xlsx", "csv", "txt", "rtf", "odt", "ods", "ppt", "pptx", "pages",
    "jpg", "jpeg", "png", "gif", "bmp", "tif", "tiff", "heic", "heif", "webp", "svg",
    "mp4", "mov", "m4a", "mp3", "wav", "zip"
};
Mask.Alphanumeric = Text.ToList("abcdefghijklmnopqrstuvwxyz0123456789");

Mask.Bytes = (seed as text) as list =>
    Binary.ToList(Crypto.CreateHash(CryptoAlgorithm.SHA256, Text.ToBinary(seed, TextEncoding.Utf8)));

Mask.IsDigits = (value as text) as logical => Text.Select(value, {"0".."9"}) = value;

Mask.DigitString = (bytes as list, offset as number, length as number) as text =>
    Text.Combine(
        List.Transform(
            List.Numbers(offset, length),
            (position) => Text.From(Number.Mod(bytes{Number.Mod(position, List.Count(bytes))}, 10))
        )
    );

// Fake values never look at the real value, so they say nothing about it. By
// chance one can be the same as the real value.
Mask.Pick = (items as list, seed as text) as text =>
    let
        bytes = Mask.Bytes(seed)
    in
        items{Number.Mod(bytes{0} * 65536 + bytes{1} * 256 + bytes{2}, List.Count(items))};

// The same as the first_name and last_name fields with this seed would get.
Mask.FullName = (seed as text) as text =>
    Mask.Pick(Names.First, seed & "|first_name") & " " & Mask.Pick(Names.Last, seed & "|last_name");

Mask.Phone = (seed as text, kind as text) as text =>
    let
        bytes = Mask.Bytes(seed)
    in
        if kind = "mobile" then Mask.FictionalMobiles{Number.Mod(bytes{0}, List.Count(Mask.FictionalMobiles))}
        else Mask.FictionalLandlinePrefixes{Number.Mod(bytes{0}, List.Count(Mask.FictionalLandlinePrefixes))} & Mask.DigitString(bytes, 1, 4);

Mask.Email = (seed as text) as text =>
    let
        bytes = Mask.Bytes(seed),
        name = Text.Combine(List.Transform(List.FirstN(bytes, 20), (byte) => Mask.Alphanumeric{Number.Mod(byte, 36)})),
        domain = Mask.EmailDomains{Number.Mod(bytes{20}, 3)}
    in
        name & "@" & domain;

// Random digits of the same length as the original.
Mask.Digits = (original as text, seed as text) as text => Mask.DigitString(Mask.Bytes(seed), 0, Text.Length(original));

// The real sid is built from the date of birth, entry grade, entry year,
// surname and first initial. This rebuilds it from the record's own kept
// fields and its fake names, and never reads the original sid. The trailing
// number keeps it unique, like the counter EnrolHQ adds to duplicates.
Mask.Sid = (original as any, source as record, seed as text) as any =>
    if original = null or original = "" then
        original
    else
        let
            field = (name as text) as text =>
                let
                    value = Record.FieldOrDefault(source, name, null)
                in
                    if value = null then "" else Text.From(value),
            first = Mask.Pick(Names.First, seed & "|first_name"),
            last = Mask.Pick(Names.Last, seed & "|last_name"),
            bytes = Mask.Bytes(seed & "|sid"),
            counter = Text.From(1 + Number.Mod(bytes{0} * 256 + bytes{1}, 999)),
            kept = List.Select({Text.Remove(field("dob"), "-"), field("entry_grade"), field("entry_year")}, each _ <> "")
        in
            Text.Combine(kept & {Text.Upper(last), Text.Start(first, 1), counter}, "-");

// Keeps the keys of an answers object and clears every value.
Mask.EmptyValues = (value as any) as any =>
    if value is record then Record.FromList(List.Transform(Record.FieldNames(value), each null), Record.FieldNames(value))
    else if value is list then {}
    else null;

// Redacts every piece of text and every number inside a value, however it is
// nested. Yes/no answers are kept.
Mask.RedactLeaves = (value as any) as any =>
    if value is record then
        Record.FromList(List.Transform(Record.FieldValues(value), Mask.RedactLeaves), Record.FieldNames(value))
    else if value is list then List.Transform(value, Mask.RedactLeaves)
    else if value is text and value <> "" then "Redacted"
    else if value is number then "Redacted"
    else value;

// ".pdf" from a file key, file name or URL; "" unless it is a known file extension.
Mask.Extension = (value as text) as text =>
    let
        path = List.First(Text.SplitAny(value, "?#")),
        name = List.Last(Text.Split(path, "/")),
        extension = if Text.Contains(name, ".") then List.Last(Text.Split(name, ".")) else ""
    in
        if List.Contains(Mask.FileExtensions, Text.Lower(extension)) then "." & extension else "";

Names.First = {
    "Adam", "Aiden", "Alex", "Alice", "Amelia", "Andrew", "Anna", "Archie", "Aria", "Ava",
    "Bella", "Benjamin", "Blake", "Caleb", "Callum", "Charlie", "Charlotte", "Chloe", "Claire", "Connor",
    "Daniel", "David", "Dylan", "Edward", "Eleanor", "Eliza", "Ella", "Emily", "Emma", "Ethan",
    "Eva", "Evelyn", "Felix", "Finn", "Freya", "Gabriel", "George", "Georgia", "Grace", "Hannah",
    "Harper", "Harriet", "Harry", "Harvey", "Hazel", "Henry", "Holly", "Hugo", "Hunter", "Imogen",
    "Isaac", "Isabel", "Isla", "Ivy", "Jack", "Jacob", "James", "Jasmine", "Jasper", "Jessica",
    "Joseph", "Joshua", "Julia", "Kate", "Lachlan", "Laura", "Leo", "Levi", "Liam", "Lily",
    "Logan", "Louis", "Lucas", "Lucy", "Luke", "Madeline", "Marcus", "Maria", "Mason", "Matilda",
    "Matthew", "Max", "Maya", "Mia", "Michael", "Mila", "Molly", "Nathan", "Nicholas", "Noah",
    "Oliver", "Olivia", "Oscar", "Patrick", "Penelope", "Phoebe", "Poppy", "Riley", "Robert", "Rose",
    "Ruby", "Ryan", "Samuel", "Sarah", "Scarlett", "Sebastian", "Sienna", "Sophie", "Stella", "Summer",
    "Theodore", "Thomas", "Toby", "Victoria", "Violet", "William", "Willow", "Xavier", "Zachary", "Zoe"
};

Names.Last = {
    "Adams", "Allen", "Anderson", "Armstrong", "Bailey", "Baker", "Barnes", "Bell", "Bennett", "Black",
    "Bradley", "Brooks", "Brown", "Burns", "Butler", "Cameron", "Campbell", "Carter", "Chapman", "Clark",
    "Cole", "Collins", "Cook", "Cooper", "Cox", "Crawford", "Davies", "Davis", "Dawson", "Dixon",
    "Douglas", "Edwards", "Elliott", "Ellis", "Evans", "Ferguson", "Fisher", "Fletcher", "Ford", "Foster",
    "Fraser", "Gibson", "Gordon", "Graham", "Grant", "Gray", "Green", "Griffiths", "Hall", "Hamilton",
    "Harris", "Harrison", "Hart", "Harvey", "Hayes", "Henderson", "Hill", "Holmes", "Howard", "Hughes",
    "Hunt", "Hunter", "Jackson", "James", "Jenkins", "Johnson", "Johnston", "Jones", "Kelly", "Kennedy",
    "King", "Knight", "Lawrence", "Lee", "Lewis", "Lloyd", "Marshall", "Martin", "Mason", "Matthews",
    "Miller", "Mills", "Mitchell", "Moore", "Morgan", "Morris", "Murphy", "Murray", "Nelson", "Newman",
    "Palmer", "Parker", "Pearce", "Perry", "Phillips", "Powell", "Price", "Reid", "Reynolds", "Richards",
    "Richardson", "Roberts", "Robertson", "Robinson", "Rogers", "Ross", "Russell", "Ryan", "Scott", "Shaw",
    "Simpson", "Smith", "Spencer", "Stevens", "Stewart", "Stone", "Sullivan", "Taylor", "Thomas", "Thompson",
    "Turner", "Walker", "Wallace", "Walsh", "Ward", "Watson", "Webb", "White", "Williams", "Wilson"
};

// ── Helpers ──────────────────────────────────────────────────────────────────

// Calls producer(attempt) until it returns a non-null value, waiting
// interval(attempt) before each call, for at most count attempts. Returns the
// first non-null value, or null when every attempt returned null.
Value.WaitFor = (producer as function, interval as function, optional count as number) as any =>
    let
        list = List.Generate(
            () => {0, null},
            (state) => state{0} <> null,
            (state) =>
                if state{1} <> null or (count <> null and state{0} >= count) then {null, state{1}}
                else {1 + state{0}, Function.InvokeAfter(() => producer(state{0}), interval(state{0}))},
            (state) => state{1}
        )
    in
        List.Last(list);

Table.ToNavigationTable = (
    table as table,
    keyColumns as list,
    nameColumn as text,
    dataColumn as text,
    itemKindColumn as text,
    itemNameColumn as text,
    isLeafColumn as text
) as table =>
    let
        tableType = Value.Type(table),
        newTableType = Type.AddTableKey(tableType, keyColumns, true) meta [
            NavigationTable.NameColumn = nameColumn,
            NavigationTable.DataColumn = dataColumn,
            NavigationTable.ItemKindColumn = itemKindColumn,
            Preview.DelayColumn = itemNameColumn,
            NavigationTable.IsLeafColumn = isLeafColumn
        ],
        navigationTable = Value.ReplaceType(table, newTableType)
    in
        navigationTable;
