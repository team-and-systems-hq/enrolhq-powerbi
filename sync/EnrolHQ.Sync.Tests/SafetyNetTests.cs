using System.Text.Json.Nodes;
using EnrolHQ.Sync.Anonymise;

namespace EnrolHQ.Sync.Tests;

public class SafetyNetTests
{
    private static (JsonObject Masked, List<string> Redacted) Run(string json)
    {
        var original = (JsonObject)JsonNode.Parse(json)!;
        var masked = Anonymiser.Mask(original, "applications", "applications#0");
        return (masked, SafetyNet.Apply(original, masked, "applications"));
    }

    [Fact]
    public void Redacts_a_kept_field_that_contains_the_family_surname()
    {
        var (masked, redacted) = Run(
            """{"id":"a1","first_name":"Olivia","last_name":"Whitlock","user_parent":{"id":"p1","last_name":"Whitlock","occupation":"Director, Whitlock Holdings"}}""");

        Assert.Equal("Redacted", (string)masked["user_parent"]!["occupation"]!);
        Assert.Equal(["applications.user_parent.occupation"], redacted);
    }

    [Fact]
    public void Redacts_a_kept_field_that_contains_an_email_address()
    {
        var (masked, redacted) = Run(
            """{"id":"a1","user_parent":{"id":"p1","email":"jo@whitlock.example","occupation":"ask jo@whitlock.example"}}""");

        Assert.Equal("Redacted", (string)masked["user_parent"]!["occupation"]!);
        Assert.Single(redacted);
    }

    [Fact]
    public void Matches_whatever_the_case()
    {
        var (masked, _) = Run("""{"id":"a1","last_name":"Whitlock","house":"WHITLOCK HOUSE"}""");

        Assert.Equal("Redacted", (string)masked["house"]!);
    }

    [Fact]
    public void Looks_inside_lists()
    {
        var (masked, redacted) = Run("""{"id":"a1","last_name":"Whitlock","interests":["Chess","Whitlock family band"]}""");

        Assert.Equal("Chess", (string)masked["interests"]![0]!);
        Assert.Equal("Redacted", (string)masked["interests"]![1]!);
        Assert.Equal(["applications.interests[]"], redacted);
    }

    [Fact]
    public void Leaves_ordinary_values_alone()
    {
        var (masked, redacted) = Run(
            """{"id":"a1","first_name":"Grace","last_name":"Whitlock","dob":"2015-03-04","current_school":{"name":"Grace Example College"},"residential_address":{"suburb":"Kew"}}""");

        // First names are not matched: too many are ordinary words.
        Assert.Equal("Grace Example College", (string)masked["current_school"]!["name"]!);
        Assert.Equal("Kew", (string)masked["residential_address"]!["suburb"]!);
        Assert.Equal("2015-03-04", (string)masked["dob"]!);
        Assert.Empty(redacted);
    }

    [Fact]
    public void A_value_the_rules_keep_on_purpose_is_not_a_secret()
    {
        var original = (JsonObject)JsonNode.Parse(
            """{"id":"c1","name":"Kew Campus","email":"kew@school.edu.au","from_email":"kew@school.edu.au","website":"https://school.edu.au"}""")!;
        var masked = Anonymiser.Mask(original, "campuses", "campuses#0");

        var redacted = SafetyNet.Apply(original, masked, "campuses");

        Assert.Equal("kew@school.edu.au", (string)masked["email"]!);
        Assert.Equal("kew@school.edu.au", (string)masked["from_email"]!);
        Assert.Empty(redacted);
    }

    [Fact]
    public void Ignores_short_surnames_that_would_match_by_chance()
    {
        var (masked, redacted) = Run("""{"id":"a1","last_name":"Lee","house":"Fleet"}""");

        Assert.Equal("Fleet", (string)masked["house"]!);
        Assert.Empty(redacted);
    }

    [Fact]
    public void Matches_a_short_surname_only_as_a_whole_word()
    {
        var (masked, redacted) = Run("""{"id":"a1","last_name":"Lee","user_parent":{"id":"p1","occupation":"Owner, Lee Trading"},"house":"Leeds"}""");

        Assert.Equal("Redacted", (string)masked["user_parent"]!["occupation"]!);
        Assert.Equal("Leeds", (string)masked["house"]!);
        Assert.Equal(["applications.user_parent.occupation"], redacted);
    }

    [Fact]
    public void Each_part_of_a_hyphenated_surname_counts()
    {
        var (masked, _) = Run("""{"id":"a1","last_name":"Whitlock-Parker","user_parent":{"id":"p1","occupation":"Director, Whitlock Holdings"}}""");

        Assert.Equal("Redacted", (string)masked["user_parent"]!["occupation"]!);
    }

    [Theory]
    [InlineData("parent_name", "Jo Whitlock")]
    [InlineData("student_profile_name", "Jo Whitlock")]
    [InlineData("case_manager_name", "Jo Whitlock")]
    [InlineData("snapshot_last_name", "Whitlock")]
    public void Takes_surnames_from_every_name_field(string field, string name)
    {
        var (masked, _) = Run($$$"""{"id":"a1","{{{field}}}":"{{{name}}}","agent_details":{"company":"Whitlock Holdings"}}""");

        Assert.Equal("Redacted", (string)masked["agent_details"]!["company"]!);
    }

    [Fact]
    public void Never_redacts_ids()
    {
        var (masked, redacted) = Run(
            """{"id":"whitlock-1","last_name":"Whitlock","external_id":"WHITLOCK01","student_code":"WHITLOCK-7","campus_id":"whitlock-campus","latest_event":"3f2b8c1e-1111-4222-8333-444455556666"}""");

        Assert.Equal("whitlock-1", (string)masked["id"]!);
        Assert.Equal("WHITLOCK01", (string)masked["external_id"]!);
        Assert.Equal("WHITLOCK-7", (string)masked["student_code"]!);
        Assert.Equal("whitlock-campus", (string)masked["campus_id"]!);
        Assert.Equal("3f2b8c1e-1111-4222-8333-444455556666", (string)masked["latest_event"]!);
        Assert.Empty(redacted);
    }

    [Fact]
    public void Does_not_touch_values_the_rules_already_replaced()
    {
        // A fake surname can happen to equal a relative's real one. It is still fake.
        var original = (JsonObject)JsonNode.Parse("""{"id":"a1","last_name":"Whitlock","user_parent":{"id":"p1","last_name":"Whitlock"}}""")!;
        var masked = Anonymiser.Mask(original, "applications", "applications#0");
        masked["user_parent"]!["last_name"] = "Whitlock-Parker";

        var redacted = SafetyNet.Apply(original, masked, "applications");

        Assert.Equal("Whitlock-Parker", (string)masked["user_parent"]!["last_name"]!);
        Assert.Empty(redacted);
    }

    [Fact]
    public void Never_redacts_a_field_with_a_rule_even_when_its_fake_value_matches()
    {
        // Find a record whose fake surname happens to be the real one.
        var id = Enumerable.Range(0, 5000).Select(number => $"a{number}")
            .First(candidate => (string)Anonymiser.Mask((JsonObject)JsonNode.Parse($$"""{"id":"{{candidate}}","last_name":"Smith"}""")!, "applications", "x")["last_name"]! == "Smith");

        var (masked, redacted) = Run($$$"""{"id":"{{{id}}}","last_name":"Smith","user_parent":{"id":"p1","last_name":"Smith"}}""");

        // Redacting it would tell anyone that the fake surname is the real one.
        Assert.Equal("Smith", (string)masked["last_name"]!);
        Assert.Empty(redacted);
    }
}
