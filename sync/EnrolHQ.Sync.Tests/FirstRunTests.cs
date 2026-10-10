namespace EnrolHQ.Sync.Tests;

public sealed class FirstRunTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "enrolhq-sync-tests", Guid.NewGuid().ToString("N"));

    public FirstRunTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    /// <summary>Runs the questions with these typed answers, in order, and returns what was asked.</summary>
    private static (FirstRun.Answers Answers, string Screen) Run(string secret, params string?[] typed)
    {
        var queue = new Queue<string?>(typed);
        var screen = new StringWriter();
        var answers = FirstRun.Ask(screen, () => queue.Count > 0 ? queue.Dequeue() : "", () => secret);
        return (answers, screen.ToString());
    }

    [Fact]
    public void Asks_for_the_address_the_token_and_whether_to_anonymise()
    {
        var (answers, screen) = Run("ekt_live_secret", "enrol.school.edu.au", "yes");

        Assert.Equal(new FirstRun.Answers("enrol.school.edu.au", "ekt_live_secret", true), answers);
        Assert.Contains("for example enrol.yourschool.edu.au", screen);
        Assert.Contains("not shown as you type", screen);
        Assert.Contains("Yes is recommended [Yes]", screen);
        Assert.DoesNotContain("ekt_live_secret", screen);
    }

    [Fact]
    public void Pressing_enter_on_the_last_question_means_yes()
    {
        var (answers, _) = Run("token", "enrol.school.edu.au", "");

        Assert.True(answers.Anonymise);
    }

    [Theory]
    [InlineData("no")]
    [InlineData("N")]
    public void No_means_real_data(string typed)
    {
        var (answers, _) = Run("token", "enrol.school.edu.au", typed);

        Assert.False(answers.Anonymise);
    }

    [Fact]
    public void Tidies_the_address_and_asks_again_when_it_is_not_one()
    {
        var (answers, screen) = Run("token", "", "not a valid address at all", "https://Enrol.School.edu.au/api/v2/", "yes");

        Assert.Equal("enrol.school.edu.au", answers.Instance);
        Assert.Contains("The address is needed", screen);
        Assert.Contains("not a valid address", screen);
    }

    [Fact]
    public void Asks_again_until_the_anonymise_answer_is_clear()
    {
        var (answers, screen) = Run("token", "enrol.school.edu.au", "maybe", "yep", "y");

        Assert.True(answers.Anonymise);
        Assert.Equal(2, screen.Split("Please answer Yes or No").Length - 1);
    }

    [Fact]
    public void Saves_a_file_the_program_reads_back()
    {
        var path = Path.Combine(_folder, ".env");

        FirstRun.Save(path, new FirstRun.Answers("enrol.school.edu.au", "ekt_live_secret", false));
        var settings = Settings.Load(path, _ => null);

        Assert.Equal("enrol.school.edu.au", settings.Instance);
        Assert.Equal("ekt_live_secret", settings.ApiToken);
        Assert.False(settings.Anonymise);
        Assert.Contains("ENROLHQ_ANONYMISE=no", File.ReadAllText(path));
    }

    [Fact]
    public void Only_the_current_user_can_read_the_saved_file()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var path = Path.Combine(_folder, ".env");
        FirstRun.Save(path, new FirstRun.Answers("enrol.school.edu.au", "secret", true));

        var rules = new FileInfo(path).GetAccessControl()
            .GetAccessRules(includeExplicit: true, includeInherited: true, typeof(System.Security.Principal.SecurityIdentifier))
            .Cast<System.Security.AccessControl.FileSystemAccessRule>()
            .ToList();
        var me = System.Security.Principal.WindowsIdentity.GetCurrent().User!;
        Assert.All(rules, rule => Assert.Equal(me, rule.IdentityReference));
        Assert.All(rules, rule => Assert.False(rule.IsInherited));
    }
}
