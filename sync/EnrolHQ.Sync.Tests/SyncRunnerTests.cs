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
        Assert.Equal(1, runner.SafetyNetHits["staff.roles[]"]);
    }
}
