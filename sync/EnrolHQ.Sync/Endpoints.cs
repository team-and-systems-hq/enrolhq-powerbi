using System.Text.Json.Nodes;

namespace EnrolHQ.Sync;

/// <summary>One list endpoint of the EnrolHQ API and the table it fills.</summary>
/// <param name="Table">Table name, which is also the key the anonymisation rules use for top-level fields.</param>
/// <param name="Path">Path under /api/v2/, or from the site root when it starts with a slash.</param>
/// <param name="PageSize">Rows per request. The API's maximum is 1000, or 100 for applications/.</param>
/// <param name="Query">Fixed query parameters.</param>
/// <param name="UpdatedAfter">
/// True when the endpoint can return only what changed since a given time.
/// Such an endpoint must be keyed by application id, because what it holds is
/// checked against the applications list after each run.
/// </param>
/// <param name="FullEvery">How often everything is downloaded again, for an endpoint whose changes since a given time can miss some.</param>
/// <param name="Keep">The only fields kept from each record. The rest is dropped before it is masked or stored.</param>
/// <param name="RowsFrom">
/// A list in each record whose entries are the table's rows, each with the
/// record's id as student_profile. Without it, each record is one row.
/// </param>
/// <param name="Feature">
/// An EnrolHQ feature the endpoint belongs to. A school that does not use it
/// answers 403, and the feature's other endpoints are then left out.
/// </param>
internal sealed record Endpoint(
    string Table,
    string Path,
    int PageSize,
    IReadOnlyDictionary<string, string>? Query = null,
    bool UpdatedAfter = false,
    TimeSpan? FullEvery = null,
    IReadOnlyList<string>? Keep = null,
    string? RowsFrom = null,
    string? Feature = null)
{
    private const string AdmissionProcesses = "admission processes";

    // Declared before All: static fields are initialised in the order they appear.
    private static readonly IReadOnlyDictionary<string, string> Ordered =
        new Dictionary<string, string> { ["ordering"] = "created_at" };

    /// <summary>
    /// Small reference lists first, so an interrupted first run still leaves
    /// something useful, and the slowest endpoint last.
    /// </summary>
    public static readonly IReadOnlyList<Endpoint> All =
    [
        new("application_statuses", "application-status-settings/", 1000),
        new("campuses", "school-campuses/", 1000),
        new("attendance_types", "attendance-types/", 1000),
        new("lead_references", "lead-references/", 1000),
        // The kinds of application a school accepts, such as a junior school
        // application and a scholarship application, each made of custom forms.
        new("admission_processes", "admission-processes/", 100, Feature: AdmissionProcesses),
        new("staff", "staff/", 1000),
        new("forms", "forms/staff/", 1000),
        new("events", "staff-events/", 200),
        new("event_bookings", "staff-event-bookings/", 200),
        // Without a fixed order, rows created during a download can be skipped or repeated.
        new("leads", "leads/", 200, Ordered),
        new("form_submissions", "forms/staff-submits/", 200, Ordered),
        // Communications with each family: staff notes, logged calls, meetings and
        // letters, and every email sent. All are small requests but the email log
        // runs to many pages, so they take 1000 rows at a time.
        new("notes", "notes/", 1000),
        new("activity_log", "activity-log/", 1000),
        new("email_log", "email-log/", 1000),
        new("applications", "applications-list/", 200, Ordered),
        // The full record for every application: guardians, siblings, medical
        // data, payments and so on. Slow, but it can fetch only what changed.
        new("application_details", "applications/", 100, UpdatedAfter: true),
        // A student's applications, one per admission process, each with its
        // own status and entry details. Only the v3alpha record has them, and
        // that record is otherwise different from v2's, so only they are kept.
        // Changing an application does not always count as a change to the
        // student, so changes alone are not enough for long.
        new(
            "student_applications",
            "/api/v3alpha/applications/",
            100,
            UpdatedAfter: true,
            FullEvery: TimeSpan.FromDays(7),
            Keep: ["id", "applications"],
            RowsFrom: "applications",
            Feature: AdmissionProcesses),
    ];

    /// <summary>
    /// True for the first endpoint of a feature, whose 403 means the school
    /// does not use it. A 403 from a later one is an ordinary refusal.
    /// </summary>
    public bool DecidesFeature => Feature is not null && ReferenceEquals(this, All.First(other => other.Feature == Feature));

    /// <summary>The record with only the fields the endpoint keeps.</summary>
    public JsonObject Kept(JsonObject record) =>
        Keep is null
            ? record
            : new JsonObject(record.Where(field => Keep.Contains(field.Key)).Select(field => KeyValuePair.Create(field.Key, field.Value?.DeepClone())));

    /// <summary>The rows a stored record gives the table.</summary>
    public IEnumerable<JsonObject> Rows(JsonObject record)
    {
        if (RowsFrom is null)
        {
            yield return record;
            yield break;
        }

        if (record[RowsFrom] is not JsonArray entries)
        {
            yield break;
        }

        foreach (var entry in entries.OfType<JsonObject>())
        {
            var row = (JsonObject)entry.DeepClone();
            row["student_profile"] = record["id"]?.DeepClone();
            yield return row;
        }
    }

    /// <summary>
    /// A stable key for a record. Form submission rows have no id of their own,
    /// so theirs is built from the form, the student and when it was started.
    /// </summary>
    public string KeyOf(JsonObject record)
    {
        if (record["id"] is JsonValue id)
        {
            return id.ToString();
        }

        if (Table == "form_submissions")
        {
            var student = (record["student_profile"] as JsonObject)?["id"]?.ToString();
            return $"{record["form_id"]}|{student}|{record["created_at"]}";
        }

        throw new InvalidOperationException($"A record from {Path} has no id.");
    }
}
