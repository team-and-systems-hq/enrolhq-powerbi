using System.Net;
using System.Text.Json.Nodes;

namespace EnrolHQ.Sync.Tests;

public sealed class SyncRunnerTests : IDisposable
{
    private static readonly Endpoint Staff = new("staff", "staff/", PageSize: 10);
    private static readonly Endpoint Details = new("application_details", "applications/", PageSize: 10, UpdatedAfter: true);

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "enrolhq-sync-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeApi _api = new();
    private readonly Store _store;

    public SyncRunnerTests()
    {
        _store = new Store(Path.Combine(_folder, "enrolhq.db"));
    }

    public void Dispose()
    {
        _store.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_folder, recursive: true);
    }

    private SyncRunner Runner(ApiClient client, bool anonymise = false) =>
        new(anonymise, _store, client, new Reporter(logDirectory: null, _api.Now), clock: () => _api.Now);

    private List<string> DataRequests() => _api.Requests.Where(request => request.StartsWith("GET")).ToList();

    [Fact]
    public async Task Downloads_every_page()
    {
        _api.Serve("staff/", 25);
        using var client = _api.Client();

        await Runner(client).SyncAsync(Staff, full: false, CancellationToken.None);

        Assert.Equal(25, _store.Count("staff"));
        Assert.Equal(3, DataRequests().Count);
        Assert.NotNull(_store.GetState("staff").CompletedAt);
    }

    [Fact]
    public async Task Carries_on_from_the_page_it_stopped_at()
    {
        _api.Serve("staff/", 45);
        using var client = _api.Client();
        using var cancellation = new CancellationTokenSource();
        // Request 1 signs in, 2 and 3 fetch pages 1 and 2, and the run stops at request 4.
        _api.StopAtRequest(4, cancellation);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Runner(client).SyncAsync(Staff, full: false, cancellation.Token));

        Assert.Equal(20, _store.Count("staff"));
        Assert.Equal(3, _store.GetState("staff").NextPage);
        Assert.Null(_store.GetState("staff").CompletedAt);

        _api.Requests.Clear();
        _api.StopAtRequest(int.MaxValue, cancellation);
        await Runner(client).SyncAsync(Staff, full: false, CancellationToken.None);

        Assert.Equal(45, _store.Count("staff"));
        Assert.Equal(["page=3", "page=4", "page=5"], DataRequests().Select(request => request.Split('?')[1].Split('&')[0]));
    }

    [Fact]
    public async Task Starts_again_when_the_unfinished_download_is_old()
    {
        _api.Serve("staff/", 45);
        using var client = _api.Client();
        using var cancellation = new CancellationTokenSource();
        _api.StopAtRequest(4, cancellation);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Runner(client).SyncAsync(Staff, full: false, cancellation.Token));

        await _api.Delay(TimeSpan.FromHours(7), CancellationToken.None);
        _api.Requests.Clear();
        _api.StopAtRequest(int.MaxValue, cancellation);
        await Runner(client).SyncAsync(Staff, full: false, CancellationToken.None);

        Assert.Equal(5, DataRequests().Count);
        Assert.Equal(45, _store.Count("staff"));
    }

    [Fact]
    public async Task Survives_the_api_slowing_it_down_mid_download()
    {
        _api.Serve("staff/", 25);
        using var client = _api.Client();
        // Sign in first, so the failures below land on requests for data.
        await client.GetAsync("staff/", new Dictionary<string, string>(), CancellationToken.None);
        _api.Waits.Clear();

        _api.FailNext(HttpStatusCode.TooManyRequests, retryAfter: TimeSpan.FromSeconds(3));
        _api.FailNext(HttpStatusCode.ServiceUnavailable);
        await Runner(client).SyncAsync(Staff, full: false, CancellationToken.None);

        Assert.Equal(25, _store.Count("staff"));
        Assert.Equal([TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(4)], _api.Waits);
    }

    [Fact]
    public async Task Removes_records_that_no_longer_exist()
    {
        _api.Serve("staff/", 25);
        using var client = _api.Client();
        await Runner(client).SyncAsync(Staff, full: false, CancellationToken.None);

        _api.Serve("staff/", 22);
        await Runner(client).SyncAsync(Staff, full: false, CancellationToken.None);

        Assert.Equal(22, _store.Count("staff"));
    }

    [Fact]
    public async Task Fetches_only_what_changed_where_the_api_allows_it()
    {
        var records = Enumerable.Range(1, 30)
            .Select(number => new JsonObject { ["id"] = $"a{number:00}", ["updated_at"] = "2026-09-01T00:00:00+00:00", ["entry_year"] = 2027 })
            .ToList();
        _api.Serve("applications/", records);
        using var client = _api.Client();
        await Runner(client).SyncAsync(Details, full: false, CancellationToken.None);
        Assert.Equal(30, _store.Count("application_details"));

        await _api.Delay(TimeSpan.FromHours(24), CancellationToken.None);
        records[4]["updated_at"] = "2026-09-29T20:00:00+00:00";
        records[4]["entry_year"] = 2028;
        _api.Requests.Clear();
        await Runner(client).SyncAsync(Details, full: false, CancellationToken.None);

        var request = Assert.Single(DataRequests());
        // Five minutes before the first sync started, in case clocks differ.
        Assert.Contains("updated_after=2026-09-29T08%3A55%3A00%2B00%3A00", request);
        // Nothing is removed by a download of changes only.
        Assert.Equal(30, _store.Count("application_details"));
        Assert.Equal(2028, (int)_store.Read("application_details").Single(record => (string)record["id"]! == "a05")["entry_year"]!);
    }

    [Fact]
    public async Task Downloads_everything_again_when_asked()
    {
        _api.Serve("applications/", Enumerable.Range(1, 30).Select(number => new JsonObject { ["id"] = $"a{number:00}", ["updated_at"] = "2026-09-01T00:00:00+00:00" }));
        using var client = _api.Client();
        await Runner(client).SyncAsync(Details, full: false, CancellationToken.None);

        _api.Requests.Clear();
        await Runner(client).SyncAsync(Details, full: true, CancellationToken.None);

        Assert.Equal(3, DataRequests().Count);
        Assert.DoesNotContain(DataRequests(), request => request.Contains("updated_after"));
    }

    [Fact]
    public async Task Downloads_everything_again_when_the_masking_rules_have_changed()
    {
        _api.Serve("applications/", Enumerable.Range(1, 30).Select(number => new JsonObject { ["id"] = $"a{number:00}", ["updated_at"] = "2026-09-01T00:00:00+00:00" }));
        using var client = _api.Client();
        await Runner(client, anonymise: true).SyncAsync(Details, full: false, CancellationToken.None);
        Assert.False(SyncRunner.IsOutdated(_store, "application_details"));

        // As a copy masked by an older version of the tool would be.
        _store.SetMeta("masking_rules:application_details", "1");
        Assert.True(SyncRunner.IsOutdated(_store, "application_details"));
        _api.Requests.Clear();
        await Runner(client, anonymise: true).SyncAsync(Details, full: false, CancellationToken.None);

        Assert.Equal(3, DataRequests().Count);
        Assert.DoesNotContain(DataRequests(), request => request.Contains("updated_after"));
        Assert.False(SyncRunner.IsOutdated(_store, "application_details"));

        // Once it is up to date, only changes are fetched again.
        _api.Requests.Clear();
        await Runner(client, anonymise: true).SyncAsync(Details, full: false, CancellationToken.None);
        Assert.Contains("updated_after", Assert.Single(DataRequests()));
    }

    [Fact]
    public async Task A_copy_made_before_rules_had_versions_counts_as_outdated()
    {
        _api.Serve("applications/", Enumerable.Range(1, 5).Select(number => new JsonObject { ["id"] = $"a{number:00}" }));
        using var client = _api.Client();
        _store.StartCycle("application_details", updatedAfter: null, _api.Now);
        _store.SavePage("application_details", 1, 1, 1, [("a01", new JsonObject { ["id"] = "a01" })], _api.Now);
        _store.CompleteCycle("application_details", 1, removeUnseen: true, _api.Now);

        Assert.True(SyncRunner.IsOutdated(_store, "application_details"));
        await Runner(client, anonymise: true).SyncAsync(Details, full: false, CancellationToken.None);

        Assert.DoesNotContain(DataRequests(), request => request.Contains("updated_after"));
        Assert.Equal(5, _store.Count("application_details"));
        Assert.False(SyncRunner.IsOutdated(_store, "application_details"));
    }

    [Fact]
    public async Task A_first_download_that_was_stopped_is_not_reported_as_outdated()
    {
        _api.Serve("applications/", Enumerable.Range(1, 45).Select(number => new JsonObject { ["id"] = $"a{number:00}" }));
        using var client = _api.Client();
        using var cancellation = new CancellationTokenSource();
        // Request 1 signs in, 2 and 3 fetch pages 1 and 2, and the run stops at request 4.
        _api.StopAtRequest(4, cancellation);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Runner(client, anonymise: true).SyncAsync(Details, full: false, cancellation.Token));

        Assert.Equal(20, _store.Count("application_details"));
        Assert.False(SyncRunner.IsOutdated(_store, "application_details"));
    }

    [Fact]
    public void A_replaced_or_removed_record_does_not_linger_in_the_database_file()
    {
        const string old = "kept-by-older-rules-0123456789";
        _store.StartCycle("events", updatedAfter: null, _api.Now);
        _store.SavePage("events", 1, 1, 2, [("e1", new JsonObject { ["id"] = "e1", ["public_token"] = old }), ("e2", new JsonObject { ["id"] = "e2", ["public_token"] = old })], _api.Now);
        _store.CompleteCycle("events", _store.GetState("events").Cycle, removeUnseen: true, _api.Now);

        _store.StartCycle("events", updatedAfter: null, _api.Now);
        _store.SavePage("events", 2, 1, 1, [("e1", new JsonObject { ["id"] = "e1", ["public_token"] = null })], _api.Now);
        _store.CompleteCycle("events", _store.GetState("events").Cycle, removeUnseen: true, _api.Now);
        _store.Compact();

        _store.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Assert.All(
            Directory.EnumerateFiles(_folder).Select(File.ReadAllBytes).Select(System.Text.Encoding.UTF8.GetString),
            content => Assert.DoesNotContain(old, content));
    }

    [Fact]
    public async Task A_stopped_download_under_new_rules_carries_on_rather_than_starting_again()
    {
        _api.Serve("applications/", Enumerable.Range(1, 45).Select(number => new JsonObject { ["id"] = $"a{number:00}", ["updated_at"] = "2026-09-01T00:00:00+00:00" }));
        using var client = _api.Client();
        await Runner(client, anonymise: true).SyncAsync(Details, full: false, CancellationToken.None);
        _store.SetMeta("masking_rules:application_details", "1");

        using var cancellation = new CancellationTokenSource();
        // Already signed in, so requests 1 and 2 fetch pages 1 and 2, and the run stops at request 3.
        _api.StopAtRequest(3, cancellation);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Runner(client, anonymise: true).SyncAsync(Details, full: false, cancellation.Token));
        Assert.True(SyncRunner.IsOutdated(_store, "application_details"));

        _api.Requests.Clear();
        _api.StopAtRequest(int.MaxValue, cancellation);
        await Runner(client, anonymise: true).SyncAsync(Details, full: false, CancellationToken.None);

        Assert.Equal(["page=3", "page=4", "page=5"], DataRequests().Select(request => request.Split('?')[1].Split('&')[0]));
        Assert.DoesNotContain(DataRequests(), request => request.Contains("updated_after"));
        Assert.False(SyncRunner.IsOutdated(_store, "application_details"));
    }

    [Fact]
    public async Task Stores_nothing_real_when_anonymising()
    {
        _api.Serve("staff/", [new JsonObject
        {
            ["id"] = "22222222-1111-4222-8333-444455556666",
            ["first_name"] = "Staffirst",
            ["last_name"] = "Stafflast",
            ["email"] = "staff@school.edu.au",
            ["roles"] = new JsonArray("Registrar for the Stafflast campus"),
        }]);
        using var client = _api.Client();
        var runner = Runner(client, anonymise: true);

        await runner.SyncAsync(Staff, full: false, CancellationToken.None);

        _store.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        var everythingOnDisk = Directory.EnumerateFiles(_folder).Select(File.ReadAllBytes).Select(System.Text.Encoding.UTF8.GetString);
        Assert.All(everythingOnDisk, content =>
        {
            Assert.DoesNotContain("Staffirst", content);
            Assert.DoesNotContain("Stafflast", content);
            Assert.DoesNotContain("staff@school", content);
        });
        // A role is not on the reviewed list, so its text is redacted before the safety net sees it.
        Assert.Equal(1, runner.UnreviewedHits["staff.roles[]"]);
    }

    private static readonly Endpoint Applications = new("applications", "applications-list/", PageSize: 10);

    [Fact]
    public async Task Starts_again_from_page_one_when_a_resumed_page_no_longer_exists()
    {
        _api.Serve("staff/", 41);
        using var client = _api.Client();
        using var cancellation = new CancellationTokenSource();
        // Signs in, fetches pages 1 to 4, and stops before page 5.
        _api.StopAtRequest(6, cancellation);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Runner(client).SyncAsync(Staff, full: false, cancellation.Token));

        _api.Records("staff/").RemoveAt(0);
        _api.Requests.Clear();
        _api.StopAtRequest(int.MaxValue, cancellation);
        await Runner(client).SyncAsync(Staff, full: false, CancellationToken.None);

        Assert.Equal(40, _store.Count("staff"));
        Assert.Equal("page=5", DataRequests()[0].Split('?')[1].Split('&')[0]);
        Assert.Equal("page=1", DataRequests()[1].Split('?')[1].Split('&')[0]);
    }

    [Fact]
    public async Task Full_carries_on_with_its_own_unfinished_download()
    {
        _api.Serve("staff/", 45);
        using var client = _api.Client();
        using var cancellation = new CancellationTokenSource();
        _api.StopAtRequest(4, cancellation);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Runner(client).SyncAsync(Staff, full: true, cancellation.Token));

        _api.Requests.Clear();
        _api.StopAtRequest(int.MaxValue, cancellation);
        await Runner(client).SyncAsync(Staff, full: true, CancellationToken.None);

        Assert.Equal(["page=3", "page=4", "page=5"], DataRequests().Select(request => request.Split('?')[1].Split('&')[0]));
    }

    [Fact]
    public async Task Downloads_again_when_a_record_is_removed_during_the_download()
    {
        _api.Serve("staff/", 30);
        using var client = _api.Client();
        var removed = false;
        _api.BeforeAnswer = path =>
        {
            // After page 1, the first record is removed, so record 11 moves onto page 1.
            if (!removed && DataRequests().Count == 2)
            {
                _api.Records("staff/").RemoveAt(0);
                removed = true;
            }
        };

        await Runner(client).SyncAsync(Staff, full: false, CancellationToken.None);

        Assert.Equal(29, _store.Count("staff"));
        Assert.Contains("r011", _store.Ids("staff"));
        Assert.DoesNotContain("r001", _store.Ids("staff"));
    }

    [Fact]
    public async Task Fetches_applications_missing_from_details_and_removes_ones_that_no_longer_exist()
    {
        var records = Enumerable.Range(1, 12).Select(number => new JsonObject { ["id"] = $"a{number:00}", ["updated_at"] = "2026-09-01T00:00:00+00:00" }).ToList();
        _api.Serve("applications-list/", records.Select(record => (JsonObject)record.DeepClone()));
        _api.Serve("applications/", records.Select(record => (JsonObject)record.DeepClone()));
        using var client = _api.Client();
        var runner = Runner(client);
        await runner.SyncAsync(Applications, full: false, CancellationToken.None);
        await runner.SyncAsync(Details, full: false, CancellationToken.None);

        // a03 was skipped by an earlier download, and a07 has been deleted in EnrolHQ.
        _store.Remove("application_details", ["a03"]);
        _api.Records("applications-list/").RemoveAll(record => (string?)record["id"] == "a07");
        _api.Records("applications/").RemoveAll(record => (string?)record["id"] == "a07");
        await _api.Delay(TimeSpan.FromHours(24), CancellationToken.None);
        runner = Runner(client);
        await runner.SyncAsync(Applications, full: false, CancellationToken.None);
        await runner.SyncAsync(Details, full: false, CancellationToken.None);
        await runner.ReconcileDetailsAsync(Details, CancellationToken.None);

        Assert.Equal(_store.Ids("applications"), _store.Ids("application_details"));
        Assert.Contains("a03", _store.Ids("application_details"));
        Assert.DoesNotContain("a07", _store.Ids("application_details"));
    }

    [Fact]
    public async Task Allows_for_enrolhq_clock_being_behind()
    {
        _api.ServerBehind = TimeSpan.FromMinutes(20);
        _api.Serve("applications/", [new JsonObject { ["id"] = "a01", ["updated_at"] = "2026-09-01T00:00:00+00:00" }]);
        using var client = _api.Client();

        await Runner(client).SyncAsync(Details, full: false, CancellationToken.None);

        Assert.Equal(_api.Now - TimeSpan.FromMinutes(20), _store.GetState("application_details").Watermark);
    }

    [Fact]
    public void Two_runs_cannot_use_one_local_copy_at_once()
    {
        var path = Path.Combine(_folder, "locked", "enrolhq.db");
        using var first = new Store(path, exclusive: true);

        var error = Assert.Throws<SettingsException>(() => new Store(path, exclusive: true));

        Assert.Contains("Another enrolhq-sync", error.Message);
    }

    [Fact]
    public void Completing_a_download_another_run_has_since_restarted_is_refused()
    {
        _store.StartCycle("staff", updatedAfter: null, _api.Now);
        _store.SavePage("staff", 1, 1, 2, [("r1", new JsonObject { ["id"] = "r1" }), ("r2", new JsonObject { ["id"] = "r2" })], _api.Now);
        _store.StartCycle("staff", updatedAfter: null, _api.Now);

        Assert.Throws<InvalidOperationException>(() => _store.CompleteCycle("staff", 1, removeUnseen: true, _api.Now));
        Assert.Equal(2, _store.Count("staff"));
    }

    [Fact]
    public async Task A_download_by_an_older_version_of_the_tool_counts_as_outdated()
    {
        _api.Serve("staff/", 5);
        using var client = _api.Client();
        await Runner(client, anonymise: true).SyncAsync(Staff, full: false, CancellationToken.None);
        Assert.False(SyncRunner.IsOutdated(_store, "staff"));

        // An older version downloads without recording anything about its rules.
        _store.StartCycle("staff", updatedAfter: null, _api.Now);

        Assert.True(SyncRunner.IsOutdated(_store, "staff"));
    }
}
