using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace EnrolHQ.Sync.Tests;

/// <summary>
/// Stands in for EnrolHQ. Serves pages of records, signs in, and can be told
/// to answer the next requests with an error.
/// </summary>
internal sealed class FakeApi : HttpMessageHandler
{
    private readonly Dictionary<string, List<JsonObject>> _records = new();
    private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage?>> _interruptions = new();
    private int _tokens;

    public List<string> Requests { get; } = [];

    public List<TimeSpan> Waits { get; } = [];

    public string CurrentToken => $"access-{_tokens}";

    public DateTimeOffset Now { get; private set; } = new(2026, 9, 29, 9, 0, 0, TimeSpan.Zero);

    /// <summary>How far EnrolHQ's clock is behind this computer's. Sent in each answer's Date header.</summary>
    public TimeSpan ServerBehind { get; set; }

    /// <summary>Runs before each answer to a data request, for changing the records mid-download.</summary>
    public Action<string>? BeforeAnswer { get; set; }

    public List<JsonObject> Records(string path) => _records[path];

    public void Serve(string path, IEnumerable<JsonObject> records) => _records[path] = records.ToList();

    public void Serve(string path, int count) =>
        Serve(path, Enumerable.Range(1, count).Select(number => new JsonObject { ["id"] = $"r{number:000}", ["number"] = number }));

    /// <summary>Answers the next request with this, whatever it asks for.</summary>
    public void FailNext(HttpStatusCode status, TimeSpan? retryAfter = null) =>
        _interruptions.Enqueue(_ =>
        {
            var response = new HttpResponseMessage(status) { Content = new StringContent("""{"detail":"no"}""") };
            if (retryAfter is { } wait)
            {
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(wait);
            }

            return response;
        });

    /// <summary>Stops the run at the next request, as Ctrl+C or a dropped connection would.</summary>
    public void StopAtRequest(int number, CancellationTokenSource cancellation)
    {
        var seen = 0;
        _stopper = () =>
        {
            if (++seen == number)
            {
                cancellation.Cancel();
            }
        };
    }

    private Action? _stopper;

    /// <summary>Cancels the current access token, as a sign-in made somewhere else does.</summary>
    public void SignInElsewhere() => _tokens++;

    public Task Delay(TimeSpan wait, CancellationToken cancel)
    {
        cancel.ThrowIfCancellationRequested();
        Waits.Add(wait);
        Now += wait;
        return Task.CompletedTask;
    }

    public ApiClient Client(TimeSpan? pace = null) =>
        new(
            new HttpClient(this) { BaseAddress = new Uri("https://school.example/api/v2/") },
            apiToken: "api-token",
            pace ?? TimeSpan.Zero,
            notice: _ => { },
            delay: Delay,
            now: () => Now);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
    {
        _stopper?.Invoke();
        cancel.ThrowIfCancellationRequested();

        var path = request.RequestUri!.AbsolutePath["/api/v2/".Length..];
        Requests.Add($"{request.Method} {path}{request.RequestUri.Query}");

        if (_interruptions.Count > 0 && _interruptions.Dequeue()(request) is { } interruption)
        {
            return Task.FromResult(interruption);
        }

        var token = request.Headers.Authorization?.Parameter;
        if (path == "accounts/refresh/")
        {
            if (token != "api-token")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
            }

            _tokens++;
            return Task.FromResult(Json(new JsonObject { ["access_token"] = CurrentToken, ["refresh_token"] = "api-token" }));
        }

        if (token != CurrentToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
        }

        BeforeAnswer?.Invoke(path);

        // applications/{id}/ answers one record.
        var parts = path.TrimEnd('/').Split('/');
        if (parts.Length == 2 && _records.TryGetValue(parts[0] + "/", out var all))
        {
            var one = all.FirstOrDefault(record => (string?)record["id"] == parts[1]);
            return Task.FromResult(one is null ? new HttpResponseMessage(HttpStatusCode.NotFound) : Dated(Json(one.DeepClone())));
        }

        if (!_records.TryGetValue(path, out var records))
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query);
        var page = int.Parse(query["page"] ?? "1");
        var size = int.Parse(query["page_size"] ?? "25");
        var matching = query["updated_after"] is { } after
            ? records.Where(record => string.CompareOrdinal((string?)record["updated_at"], after) >= 0).ToList()
            : records;
        if (page > 1 && (page - 1) * size >= matching.Count)
        {
            // As EnrolHQ does for a page past the end.
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("""{"detail":"Invalid page."}""") });
        }

        var results = matching.Skip((page - 1) * size).Take(size).Select(record => record.DeepClone()).ToArray();
        return Task.FromResult(Dated(Json(new JsonObject
        {
            ["count"] = matching.Count,
            ["next"] = page * size < matching.Count ? "more" : null,
            ["previous"] = null,
            ["results"] = new JsonArray(results),
        })));
    }

    private HttpResponseMessage Dated(HttpResponseMessage response)
    {
        response.Headers.Date = Now - ServerBehind;
        return response;
    }

    private static HttpResponseMessage Json(JsonNode body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
}
