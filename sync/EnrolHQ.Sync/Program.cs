using EnrolHQ.Sync;

const string Usage =
    """
    enrolhq-sync: keeps a local copy of a school's EnrolHQ data for Power BI.

    Usage:
      enrolhq-sync [sync] [options]   Download what is new, then write the Parquet files
      enrolhq-sync export [options]   Write the Parquet files again from the local copy
      enrolhq-sync status [options]   Show what the local copy holds

    Options:
      --full             Download everything again, not only what changed
      --new-project      Replace the Power BI project with a fresh one. Anything added to it is lost.
                         Close the project in Power BI Desktop first
      --only <tables>    Comma-separated tables to download, for example events,event_bookings
      --skip <tables>    Comma-separated tables to leave out
      --pace <ms>        Shortest time between requests, in milliseconds (default 300)
      --env <path>       The .env file to use (default: the nearest .env at or above the current folder)
      --help             Show this help

    Settings, in .env or as environment variables (an environment variable wins):
      ENROLHQ_INSTANCE   The school's EnrolHQ address, for example enrol.yourschool.edu.au
      ENROLHQ_API_TOKEN  The school's API token
      ENROLHQ_ANONYMISE  yes (default) or no. When yes, personal data is masked before it is stored
      ENROLHQ_DATA_DIR   Where to keep the local copy (default: a data folder beside the .env file,
                         or in the current folder when there is no .env file)

    The API token is not accepted as an option, because other programs can see a command line.

    Stopping a download with Ctrl+C is safe. Run the same command again and it carries on.
    """;

Options options;
try
{
    options = Options.Parse(args);
}
catch (SettingsException error)
{
    Console.Error.WriteLine(error.Message);
    Console.Error.WriteLine("Run enrolhq-sync --help for usage.");
    return 1;
}

if (options.Help)
{
    Console.WriteLine(Usage);
    return 0;
}

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, pressed) =>
{
    pressed.Cancel = true;
    cancellation.Cancel();
};

var startedAt = DateTimeOffset.UtcNow;
Reporter? reporter = null;
try
{
    var settings = Settings.Load(options.EnvPath);
    if (options.Command is "status" or "export" && !File.Exists(settings.DatabasePath))
    {
        // Nothing to show, and no reason to create an empty copy for a mistyped address.
        Console.Error.WriteLine($"There is no local copy for {settings.Instance} at {settings.DataDirectory}. Run enrolhq-sync to make one.");
        return 1;
    }

    reporter = new Reporter(options.Command == "status" ? null : settings.LogDirectory, startedAt);
    using var store = new Store(settings.DatabasePath, exclusive: options.Command != "status");
    CheckStoreMatches(settings, store);

    reporter.Line($"{settings.Instance} · {(settings.Anonymise ? "anonymised" : "REAL DATA, not anonymised")}");
    reporter.Line($"Settings: {settings.Source}");
    reporter.Line($"Local copy: {settings.DataDirectory}");

    switch (options.Command)
    {
        case "status":
            ShowStatus(store, reporter);
            WarnIfOutdated(settings, store, reporter);
            break;
        case "export":
            WarnIfOutdated(settings, store, reporter);
            WriteProject(settings, await Exporter.ExportAsync(settings, store, reporter, options.NewProject, cancellation.Token), options, reporter);
            break;
        default:
            var skipped = await SyncAsync(settings, store, options, reporter, cancellation.Token);
            // With --only or --skip, tables that were left out may still be out of date.
            WarnIfOutdated(settings, store, reporter);
            WriteProject(settings, await Exporter.ExportAsync(settings, store, reporter, options.NewProject, cancellation.Token), options, reporter);
            if (skipped.Count > 0)
            {
                reporter.Line($"Not downloaded, because EnrolHQ refused: {string.Join(", ", skipped)}. Check what the API token is allowed to read.");
                return 1;
            }

            break;
    }

    return 0;
}
catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
{
    Report("Stopped. Everything downloaded so far is saved; run the same command again to carry on.");
    return 2;
}
catch (Exception error) when (error is SettingsException or ApiException or AnonymisationException)
{
    Report(error.Message);
    return 1;
}
catch (Exception error)
{
    // Anything else, for example a locked database or a full disk. The type
    // and message say what happened; a stack trace would not help a user.
    Report($"Stopped by an unexpected error ({error.GetType().Name}): {error.Message}");
    Report("Everything downloaded so far is saved. Run the same command again to carry on.");
    return 1;
}
finally
{
    reporter?.Dispose();
}

void Report(string message)
{
    if (reporter is not null)
    {
        reporter.Line(message);
    }
    else
    {
        Console.Error.WriteLine(message);
    }
}

// Returns the tables EnrolHQ refused to serve.
static async Task<List<string>> SyncAsync(Settings settings, Store store, Options options, Reporter reporter, CancellationToken cancel)
{
    using var api = new ApiClient(
        ApiClient.CreateHttpClient(settings.BaseUri),
        settings.ApiToken,
        options.Pace,
        notice: reporter.Line);
    var runner = new SyncRunner(settings.Anonymise, store, api, reporter);
    var timer = System.Diagnostics.Stopwatch.StartNew();
    var outdated = settings.Anonymise && options.Select(Endpoint.All).Any(endpoint => SyncRunner.IsOutdated(store, endpoint.Table));
    var skipped = new List<string>();

    try
    {
        foreach (var endpoint in options.Select(Endpoint.All))
        {
            try
            {
                await runner.SyncAsync(endpoint, options.Full, cancel);
            }
            catch (ApiException error) when (error.Status is 403 or 404)
            {
                // One endpoint this token may not read should not stop the others.
                reporter.Line(error.Message);
                skipped.Add(endpoint.Table);
                continue;
            }

            if (endpoint.Table == "application_details")
            {
                await runner.ReconcileDetailsAsync(endpoint, cancel);
            }
        }

        if (outdated)
        {
            // Downloading again replaces the records, but what they held
            // before is still in the file's unused space until it is rebuilt.
            reporter.Line("Compacting the local copy, so nothing the older masking rules left unmasked remains in the file");
            store.Compact();
        }
    }
    finally
    {
        reporter.Line(
            $"{Reporter.Number(api.Requests)} requests, {Reporter.Number(api.Retries)} waits, "
            + $"{Reporter.Number(api.SignIns)} sign-in{(api.SignIns == 1 ? "" : "s")}, {Reporter.Duration(timer.Elapsed)}");
        if (runner.SafetyNetHits.Count > 0)
        {
            reporter.Line("Kept fields that contained a family's own surname or email, and were redacted:");
            foreach (var (path, count) in runner.SafetyNetHits.OrderByDescending(hit => hit.Value).ThenBy(hit => hit.Key))
            {
                reporter.Line($"  {Reporter.Number(count),7}  {path}");
            }
        }

        if (runner.UnreviewedHits.Count > 0)
        {
            reporter.Line("Fields with no masking rule that are not on the reviewed list, so their text was redacted (see Reviewed.cs):");
            foreach (var (path, count) in runner.UnreviewedHits.OrderByDescending(hit => hit.Value).ThenBy(hit => hit.Key))
            {
                reporter.Line($"  {Reporter.Number(count),7}  {path}");
            }
        }
    }

    return skipped;
}

static void WriteProject(Settings settings, IReadOnlyList<ExportedTable> tables, Options options, Reporter reporter)
{
    var written = PowerBiProject.Write(settings, tables, overwrite: options.NewProject);
    if (written)
    {
        reporter.Line($"Wrote the Power BI project: {PowerBiProject.ProjectFileFor(settings)}");
        return;
    }

    var added = PowerBiProject.AddTables(settings, tables);
    reporter.Line($"Kept the existing Power BI project: {PowerBiProject.ProjectFileFor(settings)}");
    if (added.Count > 0)
    {
        reporter.Line($"  Added tables to it: {string.Join(", ", added)}. Open the project again in Power BI Desktop and click Refresh to load them.");
    }

    reporter.Line("  New columns are not added to it. To replace it with a fresh one, close it in Power BI Desktop and run: enrolhq-sync export --new-project");
}

// A local copy is either real or anonymised, for one school, for its whole life.
static void CheckStoreMatches(Settings settings, Store store)
{
    var mode = settings.Anonymise ? "anonymised" : "real";
    var storedMode = store.GetMeta("mode");
    var storedInstance = store.GetMeta("instance");
    if (storedMode is null && storedInstance is null)
    {
        store.SetMeta("mode", mode);
        store.SetMeta("instance", settings.Instance);
        return;
    }

    if (storedMode != mode || storedInstance != settings.Instance)
    {
        throw new SettingsException(
            $"The local copy at {settings.DatabasePath} holds {storedMode} data for {storedInstance}, "
            + $"but the settings ask for {mode} data for {settings.Instance}. Delete that folder or use another ENROLHQ_DATA_DIR.");
    }
}

// Syncing a table puts it right; export and status only read the copy.
static void WarnIfOutdated(Settings settings, Store store, Reporter reporter)
{
    var outdated = settings.Anonymise
        ? Endpoint.All.Select(endpoint => endpoint.Table).Where(table => SyncRunner.IsOutdated(store, table)).ToList()
        : [];
    if (outdated.Count > 0)
    {
        reporter.Line("The masking rules have changed since these tables were downloaded, so they may hold fields that are now masked: "
            + string.Join(", ", outdated));
        reporter.Line("  Run enrolhq-sync to download them again.");
    }
}

static void ShowStatus(Store store, Reporter reporter)
{
    foreach (var endpoint in Endpoint.All)
    {
        var state = store.GetState(endpoint.Table);
        var when = state switch
        {
            { Cycle: 0 } => "never downloaded",
            { CompletedAt: null } => $"unfinished, stopped before page {state.NextPage}",
            _ => $"last synced {state.Watermark!.Value.ToLocalTime():yyyy-MM-dd HH:mm}",
        };
        reporter.Line($"{endpoint.Table,-22} {Reporter.Number(store.Count(endpoint.Table)),9} records   {when}");
    }
}

internal sealed record Options(
    string Command,
    bool Full,
    bool NewProject,
    bool Help,
    IReadOnlyList<string> Only,
    IReadOnlyList<string> Skip,
    TimeSpan Pace,
    string? EnvPath)
{
    public static Options Parse(string[] args)
    {
        var command = "sync";
        var full = false;
        var newProject = false;
        var help = false;
        var only = new List<string>();
        var skip = new List<string>();
        var pace = TimeSpan.FromMilliseconds(300);
        string? envPath = null;

        for (var index = 0; index < args.Length; index++)
        {
            string Next() => index + 1 < args.Length
                ? args[++index]
                : throw new SettingsException($"{args[index]} needs a value.");

            switch (args[index])
            {
                case "sync" or "export" or "status":
                    command = args[index];
                    break;
                case "--full":
                    full = true;
                    break;
                case "--new-project":
                    newProject = true;
                    break;
                case "--help" or "-h" or "-?":
                    help = true;
                    break;
                case "--only":
                    only.AddRange(Tables(Next()));
                    break;
                case "--skip":
                    skip.AddRange(Tables(Next()));
                    break;
                case "--env":
                    envPath = Next();
                    break;
                case "--pace":
                    var text = Next();
                    // The API allows 5 requests a second.
                    pace = int.TryParse(text, out var milliseconds) && milliseconds >= 200
                        ? TimeSpan.FromMilliseconds(milliseconds)
                        : throw new SettingsException($"--pace must be a number of milliseconds, 200 or more, not '{text}'.");
                    break;
                default:
                    throw new SettingsException($"Unknown option '{args[index]}'.");
            }
        }

        return new Options(command, full, newProject, help, only, skip, pace, envPath);
    }

    public IEnumerable<Endpoint> Select(IReadOnlyList<Endpoint> all) =>
        all.Where(endpoint => (Only.Count == 0 || Only.Contains(endpoint.Table)) && !Skip.Contains(endpoint.Table));

    private static IEnumerable<string> Tables(string value)
    {
        var names = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var known = Endpoint.All.Select(endpoint => endpoint.Table).ToList();
        foreach (var name in names)
        {
            if (!known.Contains(name))
            {
                throw new SettingsException($"Unknown table '{name}'. Tables: {string.Join(", ", known)}.");
            }
        }

        return names;
    }
}
