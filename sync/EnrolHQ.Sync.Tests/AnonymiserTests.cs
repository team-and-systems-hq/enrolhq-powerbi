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
            "fees.pdf", "principal.png", "favicon.png",
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
    [InlineData("""{"id":"s","first_name":"Adam"}""", "first_name", "Adam")]
    [InlineData("""{"id":"s","last_name":"Adams"}""", "last_name", "Adams")]
    [InlineData("""{"id":"s","usi":"0"}""", "usi", "0")]
    [InlineData("""{"id":"s","usi":"2123456701"}""", "usi", "2123456701")]
    public void A_fake_value_is_never_the_original(string json, string field, string original)
    {
        // Every id from s0 to s499, so the case where the first pick equals the original is covered.
        foreach (var id in Enumerable.Range(0, 500))
        {
            var masked = Mask(json.Replace("\"s\"", $"\"s{id}\""));
            Assert.NotEqual(original, (string)masked[field]!);
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
}
