using System.Text.Json.Nodes;
using EnrolHQ.Sync.Anonymise;

namespace EnrolHQ.Sync.Tests;

public class AnonymiserTests
{
    private const string StudentId = "3f2b8c1e-1111-4222-8333-444455556666";
    private const string ParentId = "aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee";

    private static readonly JsonObject Fixture =
        (JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Parity", "fixture.json")))!;

    private static JsonObject Mask(string json, string table = "application_details") =>
        Anonymiser.Mask((JsonObject)JsonNode.Parse(json)!, table, $"{table}#0");

    private static List<JsonObject> MaskTable(string table) =>
        ((JsonArray)Fixture[table]!)
            .Select((row, index) => Anonymiser.Mask((JsonObject)row!, table, $"{table}#{index}"))
            .ToList();

    /// <summary>
    /// The connector and this tool must give the same person the same fake
    /// values, or a school moving from one to the other would see every name
    /// change. The expected file is the connector's own output for the fixture.
    /// </summary>
    [Fact]
    public void Masks_exactly_as_the_power_bi_connector_does()
    {
        var expected = (JsonObject)JsonNode.Parse(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Parity", "expected-from-connector.json")))!;

        foreach (var (table, rows) in Fixture)
        {
            var masked = new JsonArray(MaskTable(table).ToArray<JsonNode?>());
            var differences = Differences(expected[table], masked, table).ToList();
            Assert.True(differences.Count == 0, "Fields that differ from the connector: " + string.Join(", ", differences));
            Assert.Equal(((JsonArray)rows!).Count, masked.Count);
        }
    }

    [Fact]
    public void No_real_value_survives_masking_and_the_safety_net()
    {
        string[] secrets =
        [
            "Realfirst", "Reallast", "REALLAST", "Realmiddle", "Realpreferred", "Parentfirst", "Parentlast", "Parentmiddle",
            "real-family.com", "61411222333", "0398765432", "61400111222", "0298765432", "ABC1234567", "123456789A",
            "Sensitive custody text", "photo.JPG", "X-Amz-Signature", "1 Real Street", "Guardianfirst", "Guardianlast",
            "Guardianmiddle", "PO Box 1", "1234567A", "Contactfirst", "Contactlast", "0400000000", "2123456701",
            "Ventolin", "Realdoctor", "Realdentist", "clinic.com", "Real answer", "1.2.3.4", "5.6.7.8", "token=",
            "Siblingfirst", "Othersibling", "Secondchild", "Staffirst", "Stafflast", "staff@school", "Allergic",
            "fees.pdf", "principal.png", "favicon.png", "meet.example", "cannot join", "0411000000", "kiosk-signature",
        ];

        foreach (var (table, rows) in Fixture)
        {
            var masked = MaskTable(table);
            for (var index = 0; index < masked.Count; index++)
            {
                SafetyNet.Apply((JsonObject)((JsonArray)rows!)[index]!, masked[index], table);
                var json = masked[index].ToJsonString();
                Assert.Empty(secrets.Where(secret => json.Contains(secret, StringComparison.Ordinal)));
            }
        }
    }

    [Fact]
    public void Keeps_what_schools_need_for_reporting_and_matching()
    {
        var masked = MaskTable("application_details")[0];

        Assert.Equal(StudentId, (string)masked["id"]!);
        Assert.Equal("2015-03-04", (string)masked["dob"]!);
        Assert.Equal("S1234", (string)masked["external_id"]!);
        Assert.Equal("SCH-0042", (string)masked["student_code"]!);
        Assert.Equal(2027, (int)masked["entry_year"]!);
        Assert.Equal("Kew", (string)masked["residential_address"]!["suburb"]!);
        Assert.Equal("3101", (string)masked["residential_address"]!["postcode"]!);
        Assert.Equal("Engineer", (string)masked["user_parent"]!["occupation"]!);
        Assert.Equal("1980-01-02", (string)masked["user_parent"]!["alumnus"]!["dob"]!);
        Assert.Equal("2012-05-06", (string)masked["siblings"]![0]!["dob"]!);
        Assert.Equal("Asthma", (string)masked["medical_data"]!["medical_conditions"]![0]!);
        Assert.Equal("R-1001", (string)masked["payments"]![0]!["receipt_number"]!);
        Assert.Equal("Dr", (string)masked["user_parent"]!["title"]!);
    }

    [Fact]
    public void Removes_meeting_links_joining_instructions_and_event_tokens()
    {
        var booking = MaskTable("application_details")[0]["interviews"]![0]!["booking"]!;
        var openDay = MaskTable("events")[0];

        Assert.Null(booking["online_meeting_link"]);
        Assert.Equal("Redacted", (string)booking["meeting_instructions"]!);
        Assert.Equal("2026-06-01", (string)booking["date"]!);
        Assert.Null(openDay["public_token"]);
        Assert.Null(openDay["sessions"]![0]!["online_meeting_link"]);
        Assert.Equal("Open Day", (string)openDay["name"]!);
        Assert.Equal("open-day", (string)openDay["slug"]!);
    }

    [Fact]
    public void Gives_the_same_person_the_same_fake_values_everywhere()
    {
        var details = MaskTable("application_details");
        var booking = MaskTable("event_bookings")[0];
        var submission = MaskTable("form_submissions")[0];

        // Two children of the same parent.
        Assert.True(JsonNode.DeepEquals(details[0]["user_parent"]!["email"], details[1]["user_parent"]!["email"]));
        Assert.True(JsonNode.DeepEquals(details[0]["user_parent"]!["last_name"], details[1]["user_parent"]!["last_name"]));
        // The same parent as an event attendee.
        Assert.True(JsonNode.DeepEquals(details[0]["user_parent"]!["first_name"], booking["attendees"]![0]!["first_name"]));
        // The same student on a form submission.
        Assert.True(JsonNode.DeepEquals(details[0]["first_name"], submission["student_profile"]!["first_name"]));
        // Different people.
        Assert.False(JsonNode.DeepEquals(details[0]["email"], details[1]["email"]));
    }

    [Fact]
    public void Rebuilds_the_sid_from_fake_names_without_reading_the_original()
    {
        var masked = MaskTable("application_details");
        var first = masked[0];

        var expectedStart = $"20150304-7-2027-{((string)first["last_name"]!).ToUpperInvariant()}-{((string)first["first_name"]!)[..1]}-";
        Assert.StartsWith(expectedStart, (string)first["sid"]!);
        // A sid in an unexpected format gets the same treatment.
        Assert.DoesNotContain("REALLAST", (string)masked[1]["sid"]!);
    }

    [Theory]
    [InlineData("first_name", "Adam", "Zoe")]
    [InlineData("last_name", "Adams", "Wilson")]
    [InlineData("usi", "1234567890", "9876543210")]
    [InlineData("mobile_phone", "0400111222", "0411999888")]
    [InlineData("email", "a@one.example", "b@two.example")]
    public void A_fake_value_does_not_depend_on_the_real_one(string field, string one, string other)
    {
        // Anything that steered the fake value away from the real one would let
        // someone with this code work out the real value from the fake.
        foreach (var id in Enumerable.Range(0, 200))
        {
            var first = Mask($$"""{"id":"s{{id}}","{{field}}":"{{one}}"}""");
            var second = Mask($$"""{"id":"s{{id}}","{{field}}":"{{other}}"}""");
            Assert.Equal((string)first[field]!, (string)second[field]!);
        }
    }

    [Fact]
    public void Empty_stays_empty_and_null_stays_null()
    {
        var masked = Mask("""{"first_name":"","email":"","usi":"","custody_details":null,"avatar_src":null,"payload":null}""");

        Assert.Equal("", (string)masked["first_name"]!);
        Assert.Equal("", (string)masked["email"]!);
        Assert.Equal("", (string)masked["usi"]!);
        Assert.Null(masked["custody_details"]);
        Assert.Null(masked["avatar_src"]);
        Assert.Null(masked["payload"]);
    }

    [Fact]
    public void Does_not_change_the_record_it_was_given()
    {
        var original = (JsonObject)((JsonArray)Fixture["application_details"]!)[0]!;
        var before = original.ToJsonString();

        var masked = Anonymiser.Mask(original, "application_details", "application_details#0");
        SafetyNet.Apply(original, masked, "application_details");

        Assert.Equal(before, original.ToJsonString());
    }

    [Fact]
    public void Masked_output_passes_its_own_check()
    {
        foreach (var (table, _) in Fixture)
        {
            Assert.All(MaskTable(table), row => Assert.Empty(Anonymiser.Violations(row, table)));
        }
    }

    [Theory]
    [InlineData("first_name", "\"Realfirst\"", "application_details.first_name")]
    [InlineData("email", "\"real@school.edu.au\"", "application_details.email")]
    [InlineData("mobile_phone", "\"0400111222\"", "application_details.mobile_phone")]
    [InlineData("custody_details", "\"Something sensitive\"", "application_details.custody_details")]
    [InlineData("sid", "\"20150304-7-2027-REALLAST-R\"", "application_details.sid")]
    [InlineData("enrolment_accept_link", "\"https://enrol.example/accept?token=abc\"", "application_details.enrolment_accept_link")]
    [InlineData("questionnaire", "{\"hobbies\":\"Chess\"}", "application_details.questionnaire")]
    [InlineData("usi", "12345", "application_details.usi")]
    public void The_check_catches_a_value_that_was_not_masked(string field, string unmaskedJson, string expectedPath)
    {
        var masked = MaskTable("application_details")[0];
        masked[field] = JsonNode.Parse(unmaskedJson);

        Assert.Contains(expectedPath, Anonymiser.Violations(masked, "application_details"));
    }

    [Fact]
    public void The_check_looks_inside_lists_and_nested_records()
    {
        var masked = MaskTable("application_details")[0];
        masked["guardians"]![0]!["last_name"] = "Guardianlast";
        masked["medical_data"]!["doctor"]!["name"] = "Jane Realdoctor";

        var violations = Anonymiser.Violations(masked, "application_details");

        Assert.Contains("application_details.guardians[].last_name", violations);
        Assert.Contains("application_details.medical_data.doctor.name", violations);
    }

    private static IEnumerable<string> Differences(JsonNode? expected, JsonNode? actual, string path)
    {
        switch (expected, actual)
        {
            case (JsonObject left, JsonObject right):
                foreach (var key in left.Select(field => field.Key).Union(right.Select(field => field.Key)))
                {
                    foreach (var difference in Differences(left[key], right[key], $"{path}.{key}"))
                    {
                        yield return difference;
                    }
                }

                break;
            case (JsonArray left, JsonArray right) when left.Count == right.Count:
                for (var index = 0; index < left.Count; index++)
                {
                    foreach (var difference in Differences(left[index], right[index], $"{path}[{index}]"))
                    {
                        yield return difference;
                    }
                }

                break;
            default:
                if (!JsonNode.DeepEquals(expected, actual))
                {
                    yield return path;
                }

                break;
        }
    }

    [Fact]
    public void Text_in_a_field_nobody_has_reviewed_is_redacted_and_reported()
    {
        var unreviewed = new List<string>();
        var masked = Anonymiser.Mask(
            (JsonObject)JsonNode.Parse("""{"id":"a1","religion":"Anglican","new_field":"Call Jo on 0411 222 333","notes_list":["one","two"],"created_at":"2026-05-01T09:00:00+08:00","campus":"3f2b8c1e-1111-4222-8333-444455556666","count":3}""")!,
            "applications",
            "applications#0",
            unreviewed);

        Assert.Equal("Anglican", (string)masked["religion"]!);
        Assert.Equal("Redacted", (string)masked["new_field"]!);
        Assert.Equal(["Redacted", "Redacted"], masked["notes_list"]!.AsArray().Select(item => (string)item!));
        // Dates, ids and numbers carry no names, so they are kept without review.
        Assert.Equal("2026-05-01T09:00:00+08:00", (string)masked["created_at"]!);
        Assert.Equal("3f2b8c1e-1111-4222-8333-444455556666", (string)masked["campus"]!);
        Assert.Equal(3, (int)masked["count"]!);
        Assert.Equal(["applications.new_field", "applications.notes_list[]", "applications.notes_list[]"], unreviewed);
        Assert.Empty(Anonymiser.Violations(masked, "applications"));
    }

    [Fact]
    public void The_check_catches_text_in_a_field_nobody_has_reviewed()
    {
        var masked = MaskTable("application_details")[0];
        masked["new_field"] = "Call Jo on 0411 222 333";

        Assert.Contains("application_details.new_field", Anonymiser.Violations(masked, "application_details"));
    }

    [Fact]
    public void A_record_where_text_was_expected_is_redacted_whole_and_checked()
    {
        var masked = Mask("""{"custody_details":{"text":"Father has no contact","pages":2,"signed":true},"comment":[{"body":"Ring Jo"}],"custom_field_1":61411222333}""");

        Assert.Equal("""{"text":"Redacted","pages":"Redacted","signed":true}""", masked["custody_details"]!.ToJsonString());
        Assert.Equal("""[{"body":"Redacted"}]""", masked["comment"]!.ToJsonString());
        Assert.Equal("Redacted", (string)masked["custom_field_1"]!);
        Assert.Empty(Anonymiser.Violations(masked, "application_details"));

        masked["custody_details"]!["text"] = "Father has no contact";
        Assert.Contains("application_details.custody_details", Anonymiser.Violations(masked, "application_details"));
    }

    [Fact]
    public void A_full_name_matches_the_same_persons_first_and_last_name()
    {
        var masked = Mask("""{"id":"a1","first_name":"Realfirst","last_name":"Reallast","full_name":"Realfirst Reallast","case_manager_name":"Pat Manager","middle_name":"M"}""");

        Assert.Equal($"{masked["first_name"]} {masked["last_name"]}", (string)masked["full_name"]!);
        // Someone else named on the record keeps a different fake name.
        Assert.NotEqual((string)masked["full_name"]!, (string)masked["case_manager_name"]!);
    }

    [Fact]
    public void A_record_named_only_by_full_name_is_a_person()
    {
        var masked = Mask("""{"id":"c1","full_name":"Jo Whitlock","middle_name":"Anne","preferred_name":"Jojo"}""", "application_details");

        Assert.Equal("", (string)masked["middle_name"]!);
        Assert.Equal("", (string)masked["preferred_name"]!);
    }

    [Fact]
    public void Fake_phone_numbers_are_ones_set_aside_for_fiction()
    {
        foreach (var id in Enumerable.Range(0, 200))
        {
            var masked = Mask($$"""{"id":"s{{id}}","mobile_phone":"0400111222","home_phone":"0398765432"}""");
            Assert.Contains((string)masked["mobile_phone"]!, Masks.FictionalMobiles);
            Assert.Contains(((string)masked["home_phone"]!)[..8], Masks.FictionalLandlinePrefixes);
        }
    }

    [Theory]
    [InlineData("Passport J.Smith", "removed-file")]
    [InlineData("report.PDF", "removed-file.PDF")]
    [InlineData("scan.jpeg?sig=abc", "removed-file.jpeg")]
    public void A_file_name_keeps_only_a_real_file_extension(string original, string expected)
    {
        Assert.Equal(expected, (string)Mask($$"""{"filename":"{{original}}"}""")["filename"]!);
    }
}
