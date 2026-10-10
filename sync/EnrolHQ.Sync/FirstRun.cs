using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace EnrolHQ.Sync;

/// <summary>
/// Asks for the settings the first time the program runs with none, and
/// saves them to a .env file in the current folder.
/// </summary>
internal static class FirstRun
{
    /// <summary>Answers to the three questions.</summary>
    internal sealed record Answers(string Instance, string ApiToken, bool Anonymise);

    /// <summary>
    /// True when it makes sense to ask: nothing named a settings file, and a
    /// person is at the keyboard rather than a scheduled task or a script.
    /// </summary>
    public static bool CanAsk(Options options) =>
        options.EnvPath is null && options.Command == "sync" && !Console.IsInputRedirected && !Console.IsOutputRedirected;

    /// <summary>Asks at the console and saves the answers to .env in the current folder.</summary>
    /// <returns>The path of the file written.</returns>
    public static string AskAndSave()
    {
        var path = Path.Combine(Directory.GetCurrentDirectory(), ".env");
        Console.WriteLine("No settings found. Answer three questions and they are saved to:");
        Console.WriteLine($"  {path}");
        Console.WriteLine();
        var answers = Ask(Console.Out, Console.ReadLine, ReadSecret);
        Save(path, answers);
        Console.WriteLine();
        Console.WriteLine($"Saved. Only your Windows account can read the file. To change the settings later, edit it or delete it and run the program again.");
        Console.WriteLine();
        return path;
    }

    /// <summary>The questions, kept apart from the console so they can be tested.</summary>
    internal static Answers Ask(TextWriter output, Func<string?> readLine, Func<string> readSecret)
    {
        string instance;
        while (true)
        {
            output.Write("EnrolHQ address, for example enrol.yourschool.edu.au: ");
            var answer = (readLine() ?? "").Trim();
            if (answer.Length == 0)
            {
                output.WriteLine("  The address is needed. It is the start of the web address staff use to sign in to EnrolHQ.");
                continue;
            }

            try
            {
                instance = Settings.NormaliseInstance(answer);
                break;
            }
            catch (SettingsException error)
            {
                output.WriteLine($"  {error.Message}");
            }
        }

        string token;
        while (true)
        {
            output.Write("API token (not shown as you type): ");
            token = readSecret().Trim();
            output.WriteLine();
            if (token.Length > 0)
            {
                break;
            }

            output.WriteLine("  The token is needed. An EnrolHQ administrator makes one under their profile.");
        }

        bool anonymise;
        while (true)
        {
            output.Write("Anonymise personal data? Yes is recommended [Yes]: ");
            var answer = (readLine() ?? "").Trim().ToLowerInvariant();
            if (answer is "" or "yes" or "y")
            {
                anonymise = true;
                break;
            }

            if (answer is "no" or "n")
            {
                anonymise = false;
                break;
            }

            output.WriteLine("  Please answer Yes or No.");
        }

        return new Answers(instance, token, anonymise);
    }

    internal static void Save(string path, Answers answers)
    {
        var content = new StringBuilder()
            .AppendLine("# Settings for enrolhq-sync. The token is a secret: keep this file to yourself.")
            .AppendLine($"ENROLHQ_INSTANCE={answers.Instance}")
            .AppendLine($"ENROLHQ_API_TOKEN={answers.ApiToken}")
            .AppendLine()
            .AppendLine("# yes masks personal data before it is stored. no stores real data.")
            .AppendLine($"ENROLHQ_ANONYMISE={(answers.Anonymise ? "yes" : "no")}")
            .ToString();
        File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        RestrictToCurrentUser(path);
    }

    /// <summary>The file holds the token, so nobody else on the computer should be able to read it.</summary>
    private static void RestrictToCurrentUser(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            var security = new FileSecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.FullControl, AccessControlType.Allow));
            new FileInfo(path).SetAccessControl(security);
        }
        catch (Exception error) when (error is UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // The file is still written; the folder's own permissions apply.
        }
    }

    /// <summary>Reads a line without echoing it, so the token does not stay on screen.</summary>
    private static string ReadSecret()
    {
        var secret = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                return secret.ToString();
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (secret.Length > 0)
                {
                    secret.Length--;
                }
            }
            else if (!char.IsControl(key.KeyChar))
            {
                secret.Append(key.KeyChar);
            }
        }
    }
}
