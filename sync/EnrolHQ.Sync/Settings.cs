namespace EnrolHQ.Sync;

/// <summary>What to sync and where to keep it, read from a .env file and environment variables.</summary>
internal sealed record Settings(string Instance, string ApiToken, bool Anonymise, string DataDirectory)
{
    private static readonly string[] Names = ["ENROLHQ_INSTANCE", "ENROLHQ_API_TOKEN", "ENROLHQ_ANONYMISE", "ENROLHQ_DATA_DIR"];

    public Uri BaseUri => new($"https://{Instance}/api/v2/");

    public string DatabasePath => Path.Combine(DataDirectory, "enrolhq.db");

    public string ParquetDirectory => Path.Combine(DataDirectory, "parquet");

    public string LogDirectory => Path.Combine(DataDirectory, "logs");

    /// <summary>
    /// Loads settings from <paramref name="envPath"/>, or from the nearest .env in
    /// the current directory or one of its parents. An environment variable of
    /// the same name wins over the file, so the API token need not be kept in
    /// a file at all.
    /// </summary>
    public static Settings Load(string? envPath, Func<string, string?>? environment = null)
    {
        environment ??= Environment.GetEnvironmentVariable;
        if (envPath is not null && !File.Exists(envPath))
        {
            throw new SettingsException($"No .env file at {envPath}.");
        }

        var path = envPath ?? FindEnvFile(Directory.GetCurrentDirectory());
        var values = path is null ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) : Parse(File.ReadAllLines(path));
        foreach (var name in Names)
        {
            if (environment(name) is { Length: > 0 } value)
            {
                values[name] = value.Trim();
            }
        }

        if (values.Count == 0)
        {
            throw new SettingsException(
                "No settings found. Copy .env.example to .env and fill it in, pass --env <path>, "
                + "or set ENROLHQ_INSTANCE and ENROLHQ_API_TOKEN as environment variables.");
        }

        var instance = NormaliseInstance(Required(values, "ENROLHQ_INSTANCE"));
        var apiToken = Required(values, "ENROLHQ_API_TOKEN");
        var anonymise = ParseAnonymise(values.GetValueOrDefault("ENROLHQ_ANONYMISE"));

        // Real and anonymised data never share a folder, so a change of setting
        // can never mix the two in one database.
        var beside = path is null ? Directory.GetCurrentDirectory() : Path.GetDirectoryName(Path.GetFullPath(path))!;
        var root = values.GetValueOrDefault("ENROLHQ_DATA_DIR") is { Length: > 0 } custom
            ? Path.GetFullPath(custom, beside)
            : Path.Combine(beside, "data");
        var dataDirectory = Path.Combine(root, instance, anonymise ? "anonymised" : "real");

        return new Settings(instance, apiToken, anonymise, dataDirectory);
    }

    internal static Dictionary<string, string> Parse(IEnumerable<string> lines)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#') || !line.Contains('='))
            {
                continue;
            }

            var separator = line.IndexOf('=');
            var name = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim().Trim('"', '\'');
            if (name.Length > 0)
            {
                values[name] = value;
            }
        }

        return values;
    }

    /// <summary>
    /// Anonymisation is on unless it is clearly switched off. Anything other
    /// than yes or no is refused, so a typo can never store real data.
    /// </summary>
    internal static bool ParseAnonymise(string? value)
    {
        var normalised = (value ?? "").Trim().ToLowerInvariant();
        return normalised switch
        {
            "" or "yes" => true,
            "no" => false,
            _ => throw new SettingsException($"ENROLHQ_ANONYMISE must be yes or no, not '{value}'."),
        };
    }

    /// <summary>Keeps only the host, so the token is only ever sent to the instance's own API.</summary>
    internal static string NormaliseInstance(string value)
    {
        var trimmed = value.Trim();
        var withScheme = trimmed.Contains("://", StringComparison.Ordinal) ? trimmed : "https://" + trimmed;
        if (!Uri.TryCreate(withScheme, UriKind.Absolute, out var uri) || uri.Host.Length == 0)
        {
            throw new SettingsException($"ENROLHQ_INSTANCE is not a valid address: '{value}'.");
        }

        var host = uri.Host.ToLowerInvariant();
        return host.Contains('.') ? host : host + ".enrolhq.com.au";
    }

    private static string Required(Dictionary<string, string> values, string name) =>
        values.GetValueOrDefault(name) is { Length: > 0 } value
            ? value
            : throw new SettingsException($"{name} is missing. Set it in .env or as an environment variable.");

    private static string? FindEnvFile(string start)
    {
        for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, ".env");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}

internal sealed class SettingsException(string message) : Exception(message);
