namespace EnrolHQ.Sync.Tests;

public class PowerBiProjectTests
{
    private static ExportedTable Table(string name, params string[] columns) =>
        new(name, Rows: 1, columns.Select(column => new Column(column, ColumnKind.Text)).ToList());

    private static readonly ExportedTable[] School =
    [
        Table("application_statuses", "id", "application_status", "status_label"),
        Table("campuses", "id", "name"),
        Table("attendance_types", "id", "name"),
        Table("events", "id", "campus", "name"),
        Table("events_sessions", "event_id", "id", "name"),
        Table("event_bookings", "id", "session", "event"),
        Table("event_bookings_attendees", "event_booking_id", "id", "first_name"),
        Table("event_bookings_student_profiles", "event_booking_id", "student_profile_id"),
        Table("forms", "id", "title"),
        Table("form_submissions", "form_id", "student_profile_id"),
        Table("applications", "id", "application_status", "campus", "attendance_type"),
        Table("application_details", "id", "first_name"),
        Table("application_details_guardians", "application_detail_id", "id", "first_name"),
        Table("application_details_interviews", "application_detail_id", "id"),
        Table("application_details_interviews_panel", "application_details_interview_id", "name"),
    ];

    private static string Describe((string FromTable, string FromColumn, string ToTable, string ToColumn, bool Active) relationship) =>
        $"{relationship.FromTable}.{relationship.FromColumn} -> {relationship.ToTable}.{relationship.ToColumn}";

    /// <summary>
    /// Power BI refuses to open a model in which filters can travel between
    /// two tables by more than one route.
    /// </summary>
    [Fact]
    public void Filters_never_have_two_routes_between_the_same_tables()
    {
        var active = PowerBiProject.Relationships(School).Where(relationship => relationship.Active).ToList();

        foreach (var start in School.Select(table => table.Name))
        {
            var routes = new Dictionary<string, int> { [start] = 1 };
            var queue = new Queue<string>([start]);
            while (queue.Count > 0)
            {
                var table = queue.Dequeue();
                foreach (var next in active.Where(relationship => relationship.ToTable == table).Select(relationship => relationship.FromTable))
                {
                    routes[next] = routes.GetValueOrDefault(next) + routes[table];
                    queue.Enqueue(next);
                }
            }

            Assert.All(routes, route => Assert.True(route.Value == 1, $"{route.Value} routes from {start} to {route.Key}"));
        }
    }

    [Fact]
    public void Keeps_the_links_that_reports_depend_on()
    {
        var active = PowerBiProject.Relationships(School).Where(relationship => relationship.Active).Select(Describe).ToList();

        Assert.Contains("applications.application_status -> application_statuses.application_status", active);
        Assert.Contains("applications.campus -> campuses.id", active);
        Assert.Contains("application_details.id -> applications.id", active);
        Assert.Contains("event_bookings.session -> events_sessions.id", active);
        Assert.Contains("events_sessions.event_id -> events.id", active);
        Assert.Contains("event_bookings_student_profiles.student_profile_id -> applications.id", active);
        Assert.Contains("event_bookings_student_profiles.event_booking_id -> event_bookings.id", active);
        Assert.Contains("event_bookings_attendees.event_booking_id -> event_bookings.id", active);
        Assert.Contains("form_submissions.student_profile_id -> applications.id", active);
        Assert.Contains("application_details_guardians.application_detail_id -> application_details.id", active);
        Assert.Contains("application_details_interviews_panel.application_details_interview_id -> application_details_interviews.id", active);
    }

    [Fact]
    public void Switches_off_the_less_important_link_when_two_would_clash()
    {
        var inactive = PowerBiProject.Relationships(School).Where(relationship => !relationship.Active).Select(Describe).ToList();

        // Bookings already reach events through sessions.
        Assert.Contains("event_bookings.event -> events.id", inactive);
        // Applications and events both belong to a campus, and bookings join students to events.
        Assert.Contains("events.campus -> campuses.id", inactive);
        Assert.Equal(2, inactive.Count);
    }

    [Fact]
    public void Leaves_out_links_to_tables_or_columns_this_school_does_not_have()
    {
        var tables = School.Where(table => table.Name is not ("campuses" or "forms")).ToList();

        var relationships = PowerBiProject.Relationships(tables).Select(Describe).ToList();

        Assert.DoesNotContain(relationships, relationship => relationship.Contains("campuses") || relationship.Contains("forms."));
        Assert.Contains("applications.application_status -> application_statuses.application_status", relationships);
    }
}
