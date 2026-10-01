using System.Text.Json.Nodes;

namespace EnrolHQ.Sync.Tests;

public class FlattenerTests
{
    private static List<FlatTable> Flatten(string table, params string[] records) =>
        Flattener.Flatten(table, records.Select(json => (JsonObject)JsonNode.Parse(json)!));

    private static object? Value(FlatTable table, int row, string column)
    {
        var index = table.Columns.ToList().FindIndex(candidate => candidate.Name == column);
        Assert.True(index >= 0, $"No column {column} in {table.Name}. Columns: {string.Join(", ", table.Columns.Select(c => c.Name))}");
        return table.Rows[row][index];
    }

    private static ColumnKind KindOf(FlatTable table, string column) =>
        table.Columns.Single(candidate => candidate.Name == column).Kind;

    [Fact]
    public void Nested_records_become_prefixed_columns()
    {
        var table = Flatten("applications", """{"id":"a1","user_parent":{"id":"p1","residential_address":{"suburb":"Kew"}}}""").Single();

        Assert.Equal(["id", "user_parent_id", "user_parent_residential_address_suburb"], table.Columns.Select(column => column.Name));
        Assert.Equal("Kew", Value(table, 0, "user_parent_residential_address_suburb"));
    }

    [Fact]
    public void Column_types_come_from_the_values()
    {
        var table = Flatten(
            "applications",
            """{"id":"a1","entry_year":2027,"fee":250.5,"is_favorite":true,"dob":"2015-03-04","created_at":"2026-05-01T09:30:15.123456+08:00","status":3,"notes":"x"}""",
            """{"id":"a2","entry_year":2028,"fee":300,"is_favorite":false,"dob":"","created_at":null,"status":"3a","notes":null}""").Single();

        Assert.Equal(ColumnKind.Integer, KindOf(table, "entry_year"));
        Assert.Equal(ColumnKind.Number, KindOf(table, "fee"));
        Assert.Equal(ColumnKind.Logical, KindOf(table, "is_favorite"));
        Assert.Equal(ColumnKind.Date, KindOf(table, "dob"));
        Assert.Equal(ColumnKind.DateTime, KindOf(table, "created_at"));
        // A column with both numbers and text is text.
        Assert.Equal(ColumnKind.Text, KindOf(table, "status"));

        Assert.Equal(300d, Value(table, 1, "fee"));
        Assert.Equal(new DateTime(2015, 3, 4), Value(table, 0, "dob"));
        Assert.Null(Value(table, 1, "dob"));
        Assert.Equal("3", Value(table, 0, "status"));
    }

    [Fact]
    public void Times_keep_the_school_clock_time_and_drop_the_offset()
    {
        var table = Flatten("events", """{"id":"e1","start_datetime":"2026-05-01T09:30:15+08:00"}""").Single();

        var time = Assert.IsType<DateTime>(Value(table, 0, "start_datetime"));
        Assert.Equal(new DateTime(2026, 5, 1, 9, 30, 15), time);
        Assert.Equal(DateTimeKind.Unspecified, time.Kind);
    }

    [Fact]
    public void A_list_of_records_becomes_its_own_table_linked_by_id()
    {
        var tables = Flatten(
            "event_bookings",
            """{"id":"b1","attendees":[{"id":"t1","first_name":"Ava"},{"id":"t2","first_name":"Leo"}]}""",
            """{"id":"b2","attendees":[]}""");

        var bookings = tables.Single(table => table.Name == "event_bookings");
        var attendees = tables.Single(table => table.Name == "event_bookings_attendees");

        Assert.Equal(2L, Value(bookings, 0, "attendees_count"));
        Assert.Equal(0L, Value(bookings, 1, "attendees_count"));
        Assert.Equal(["event_booking_id", "id", "first_name"], attendees.Columns.Select(column => column.Name));
        Assert.Equal(2, attendees.Rows.Count);
        Assert.Equal("b1", Value(attendees, 1, "event_booking_id"));
        Assert.Equal("t2", Value(attendees, 1, "id"));
    }

    [Fact]
    public void A_list_inside_a_nested_record_is_named_by_its_path()
    {
        var tables = Flatten(
            "applications",
            """{"id":"a1","progress":{"enrolment_offer":{"agreement_documents":[{"id":"d1","label":"Fees"}]}}}""");

        var documents = tables.Single(table => table.Name == "applications_progress_enrolment_offer_agreement_documents");
        Assert.Equal("a1", Value(documents, 0, "application_id"));
        Assert.Equal(1L, Value(tables.Single(table => table.Name == "applications"), 0, "progress_enrolment_offer_agreement_documents_count"));
    }

    [Fact]
    public void A_list_inside_a_child_links_to_the_child()
    {
        var tables = Flatten(
            "application_details",
            """{"id":"a1","interviews":[{"id":"i1","panel":[{"name":"Room 2"}]}],"siblings":[{"first_name":"Ava","schools":[{"name":"Kew Primary"}]}]}""");

        var panel = tables.Single(table => table.Name == "application_details_interviews_panel");
        Assert.Equal("i1", Value(panel, 0, "application_details_interview_id"));

        // A sibling has no id of its own, so its children link to the application.
        var schools = tables.Single(table => table.Name == "application_details_siblings_schools");
        Assert.Equal("a1", Value(schools, 0, "application_detail_id"));
    }

    [Fact]
    public void A_list_of_plain_values_becomes_text()
    {
        var table = Flatten("leads", """{"id":"l1","how_hear":["Friend","Open day"],"questions":[]}""").Single();

        Assert.Equal("Friend, Open day", Value(table, 0, "how_hear"));
        Assert.Null(Value(table, 0, "questions"));
    }

    [Fact]
    public void A_list_of_ids_also_becomes_a_table_for_joining()
    {
        var tables = Flatten(
            "event_bookings",
            """{"id":"b1","student_profiles":["3f2b8c1e-1111-4222-8333-444455556666","99999999-1111-4222-8333-444455556666"]}""");

        var students = tables.Single(table => table.Name == "event_bookings_student_profiles");
        Assert.Equal(["event_booking_id", "student_profile_id"], students.Columns.Select(column => column.Name));
        Assert.Equal(2, students.Rows.Count);
        Assert.Equal("99999999-1111-4222-8333-444455556666", Value(students, 1, "student_profile_id"));
    }

    [Fact]
    public void Fields_missing_from_some_records_are_blank()
    {
        var table = Flatten("staff", """{"id":"s1","mobile_phone":"0400"}""", """{"id":"s2","roles":"admin"}""").Single();

        Assert.Equal(["id", "mobile_phone", "roles"], table.Columns.Select(column => column.Name));
        Assert.Null(Value(table, 0, "roles"));
        Assert.Null(Value(table, 1, "mobile_phone"));
    }

    [Fact]
    public void A_record_that_is_sometimes_null_leaves_no_empty_column_behind()
    {
        var table = Flatten(
            "applications",
            """{"id":"a1","non_user_parent":null,"leaving_date":null}""",
            """{"id":"a2","non_user_parent":{"id":"p2"},"leaving_date":null}""").Single();

        Assert.Equal(["id", "leaving_date", "non_user_parent_id"], table.Columns.Select(column => column.Name).Order());
    }

    [Fact]
    public void A_child_field_named_like_the_link_is_kept_under_another_name()
    {
        var tables = Flatten("events", """{"id":"e1","sessions":[{"id":"s1","event_id":"from-the-api"}]}""");

        var sessions = tables.Single(table => table.Name == "events_sessions");
        Assert.Equal("e1", Value(sessions, 0, "event_id"));
        Assert.Equal("from-the-api", Value(sessions, 0, "event_id_value"));
    }

    [Fact]
    public void A_table_with_no_records_is_left_out()
    {
        Assert.Empty(Flatten("leads"));
    }

    [Theory]
    [InlineData("events", "event")]
    [InlineData("event_bookings", "event_booking")]
    [InlineData("campuses", "campus")]
    [InlineData("custom_categories", "custom_category")]
    [InlineData("application_statuses", "application_status")]
    [InlineData("staff", "staff")]
    [InlineData("staff_with_access", "staff_with_access")]
    public void Link_columns_use_the_singular(string plural, string singular)
    {
        Assert.Equal(singular, Flattener.Singular(plural));
    }
}
