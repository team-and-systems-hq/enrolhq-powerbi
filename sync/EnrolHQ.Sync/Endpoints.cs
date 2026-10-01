using System.Text.Json.Nodes;

namespace EnrolHQ.Sync;

/// <summary>One list endpoint of the EnrolHQ API and the table it fills.</summary>
/// <param name="Table">Table name, which is also the key the anonymisation rules use for top-level fields.</param>
/// <param name="Path">Path under /api/v2/.</param>
/// <param name="PageSize">Rows per request. The API's maximum is 1000, or 100 for applications/.</param>
/// <param name="Query">Fixed query parameters.</param>
/// <param name="UpdatedAfter">True when the endpoint can return only what changed since a given time.</param>
internal sealed record Endpoint(
    string Table,
    string Path,
    int PageSize,
    IReadOnlyDictionary<string, string>? Query = null,
    bool UpdatedAfter = false)
{
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
        new("staff", "staff/", 1000),
        new("forms", "forms/staff/", 1000),
        new("events", "staff-events/", 200),
        new("event_bookings", "staff-event-bookings/", 200),
        // Without a fixed order, rows created during a download can be skipped or repeated.
        new("leads", "leads/", 200, Ordered),
        new("form_submissions", "forms/staff-submits/", 200, Ordered),
        new("applications", "applications-list/", 200, Ordered),
        // The full record for every application: guardians, siblings, medical
        // data, payments and so on. Slow, but it can fetch only what changed.
        new("application_details", "applications/", 100, UpdatedAfter: true),
    ];

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
