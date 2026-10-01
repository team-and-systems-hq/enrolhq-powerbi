namespace EnrolHQ.Sync.Tests;

public sealed class SettingsTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "enrolhq-sync-tests", Guid.NewGuid().ToString("N"));

    public SettingsTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private Settings Load(params string[] lines)
    {
        var path = Path.Combine(_folder, ".env");
        File.WriteAllLines(path, lines);
        return Settings.Load(path);
    }

    [Fact]
    public void Anonymises_unless_told_not_to()
    {
        var settings = Load("ENROLHQ_INSTANCE=enrol.school.edu.au", "ENROLHQ_API_TOKEN=secret");

        Assert.True(settings.Anonymise);
    }

    [Theory]
    [InlineData("yes", true)]
    [InlineData(" Yes ", true)]
    [InlineData("no", false)]
    [InlineData("NO", false)]
    public void Reads_yes_and_no(string value, bool expected)
    {
        Assert.Equal(expected, Settings.ParseAnonymise(value));
    }

    [Theory]
    [InlineData("n")]
    [InlineData("false")]
    [InlineData("0")]
    [InlineData("off")]
    [InlineData("yes please")]
    public void Refuses_anything_unclear_so_a_typo_cannot_store_real_data(string value)
    {
        var error = Assert.Throws<SettingsException>(() => Settings.ParseAnonymise(value));

        Assert.Contains("must be yes or no", error.Message);
    }

    [Fact]
    public void Keeps_real_and_anonymised_data_in_separate_folders()
    {
        var anonymised = Load("ENROLHQ_INSTANCE=enrol.school.edu.au", "ENROLHQ_API_TOKEN=secret", "ENROLHQ_ANONYMISE=yes");
        var real = Load("ENROLHQ_INSTANCE=enrol.school.edu.au", "ENROLHQ_API_TOKEN=secret", "ENROLHQ_ANONYMISE=no");

        Assert.Equal(Path.Combine(_folder, "data", "enrol.school.edu.au", "anonymised"), anonymised.DataDirectory);
        Assert.Equal(Path.Combine(_folder, "data", "enrol.school.edu.au", "real"), real.DataDirectory);
    }

    [Theory]
    [InlineData("enrol.school.edu.au", "enrol.school.edu.au")]
    [InlineData("https://Enrol.School.edu.au/api/v2/", "enrol.school.edu.au")]
    [InlineData("http://enrol.school.edu.au", "enrol.school.edu.au")]
    [InlineData("demo", "demo.enrolhq.com.au")]
    public void Keeps_only_the_host_of_the_address(string value, string expected)
    {
        Assert.Equal(expected, Settings.NormaliseInstance(value));
        Assert.Equal($"https://{expected}/api/v2/", Load($"ENROLHQ_INSTANCE={value}", "ENROLHQ_API_TOKEN=secret").BaseUri.ToString());
    }

    [Fact]
    public void Reads_quoted_values_and_skips_comments()
    {
        var settings = Load("# the school", "ENROLHQ_INSTANCE = \"enrol.school.edu.au\"", "", "ENROLHQ_API_TOKEN='secret=with=equals'");

        Assert.Equal("enrol.school.edu.au", settings.Instance);
        Assert.Equal("secret=with=equals", settings.ApiToken);
    }

    [Fact]
    public void Says_which_setting_is_missing()
    {
        var error = Assert.Throws<SettingsException>(() => Load("ENROLHQ_INSTANCE=enrol.school.edu.au"));

        Assert.Contains("ENROLHQ_API_TOKEN", error.Message);
    }
}
