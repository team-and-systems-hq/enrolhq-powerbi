using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;

namespace EnrolHQ.Sync;

/// <summary>
/// Talks to the EnrolHQ API one request at a time and keeps to its limits:
/// 5 data requests a second and 5 sign-ins a minute, with each sign-in
/// cancelling the access token issued before it.
/// </summary>
internal sealed class ApiClient : IDisposable
{
    private const int MaxAttempts = 10;
    private static readonly TimeSpan TokenLifetime = TimeSpan.FromMinutes(50);
    private static readonly TimeSpan SignInWait = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan LongestWait = TimeSpan.FromSeconds(60);

    private readonly HttpClient _http;
    private readonly string _apiToken;
    private readonly TimeSpan _pace;
    private readonly Action<string> _notice;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Func<DateTimeOffset> _now;

    private string? _accessToken;
    private DateTimeOffset _tokenIssuedAt;
    private DateTimeOffset _lastRequestAt = DateTimeOffset.MinValue;

    public ApiClient(
        HttpClient http,
        string apiToken,
        TimeSpan pace,
        Action<string> notice,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        Func<DateTimeOffset>? now = null)
    {
        _http = http;
        _apiToken = apiToken;
        _pace = pace;
        _notice = notice;
        _delay = delay ?? Task.Delay;
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    public int Requests { get; private set; }

    public int Retries { get; private set; }

    public int SignIns { get; private set; }

    /// <summary>EnrolHQ's clock, from the Date header of the last answer, if it sent one.</summary>
    public DateTimeOffset? ServerTime { get; private set; }

    /// <summary>When the last answer arrived, by this computer's clock.</summary>
    public DateTimeOffset AnsweredAt { get; private set; }

    public static HttpClient CreateHttpClient(Uri baseUri)
    {
        var handler = new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All };
        var http = new HttpClient(handler) { BaseAddress = baseUri, Timeout = TimeSpan.FromMinutes(3) };
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        http.DefaultRequestHeaders.UserAgent.ParseAdd("enrolhq-sync/0.1");
        return http;
    }

    public async Task<JsonNode?> GetAsync(string path, IReadOnlyDictionary<string, string> query, CancellationToken cancel)
    {
        var url = path + ToQueryString(query);
        var signedInForThisRequest = false;

        for (var attempt = 1; ; attempt++)
        {
            await EnsureAccessTokenAsync(cancel);
            await PaceAsync(cancel);

            HttpResponseMessage response;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Token", _accessToken);
                Requests++;
                response = await _http.SendAsync(request, cancel);
            }
            catch (Exception error) when (IsUnknownAddress(error))
            {
                throw UnknownAddress();
            }
            catch (Exception error) when (IsTransient(error, cancel) && attempt < MaxAttempts)
            {
                await WaitAsync(Backoff(attempt), $"{path}: no response ({error.GetType().Name})", cancel);
                continue;
            }

            using (response)
            {
                ServerTime = response.Headers.Date;
                AnsweredAt = _now();
                if (response.IsSuccessStatusCode)
                {
                    var text = await response.Content.ReadAsStringAsync(cancel);
                    try
                    {
                        return JsonNode.Parse(text);
                    }
                    catch (System.Text.Json.JsonException)
                    {
                        // For example a sign-in or maintenance page from a proxy in front of EnrolHQ.
                        throw new ApiException(path, status: 200, "The answer was not JSON, so it did not come from the EnrolHQ API.");
                    }
                }

                var status = (int)response.StatusCode;
                if (status == 401 && !signedInForThisRequest)
                {
                    // Expired, or cancelled by a sign-in made somewhere else.
                    signedInForThisRequest = true;
                    _accessToken = null;
                    continue;
                }

                if ((status == 429 || status >= 500) && attempt < MaxAttempts)
                {
                    var wait = RetryAfter(response) ?? Backoff(attempt);
                    await WaitAsync(wait, $"{path}: EnrolHQ answered {status}", cancel);
                    continue;
                }

                throw new ApiException(path, status, await ReadDetailAsync(response, cancel));
            }
        }
    }

    public void Dispose() => _http.Dispose();

    private async Task EnsureAccessTokenAsync(CancellationToken cancel)
    {
        if (_accessToken is not null && _now() - _tokenIssuedAt < TokenLifetime)
        {
            return;
        }

        const string path = "accounts/refresh/";
        for (var attempt = 1; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Token", _apiToken);
            SignIns++;
            HttpResponseMessage sent;
            try
            {
                sent = await _http.SendAsync(request, cancel);
            }
            catch (Exception error) when (IsUnknownAddress(error))
            {
                throw UnknownAddress();
            }
            catch (Exception error) when (IsTransient(error, cancel) && attempt < MaxAttempts)
            {
                await WaitAsync(Backoff(attempt), $"sign-in: no response ({error.GetType().Name})", cancel);
                continue;
            }

            using var response = sent;
            var status = (int)response.StatusCode;

            if (response.IsSuccessStatusCode)
            {
                JsonNode? body;
                try
                {
                    body = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancel));
                }
                catch (System.Text.Json.JsonException)
                {
                    throw new ApiException(path, status, "The answer was not JSON. Check ENROLHQ_INSTANCE is the school's EnrolHQ address.");
                }

                _accessToken = body?["access_token"]?.GetValue<string>()
                    ?? throw new ApiException(path, status, "EnrolHQ did not return an access token.");
                _tokenIssuedAt = _now();
                return;
            }

            if (status is 401 or 403)
            {
                throw new ApiException(path, status, "EnrolHQ did not accept the API token.");
            }

            // 400 is what a sign-in gets when another one is in progress.
            if ((status is 400 or 429 || status >= 500) && attempt < MaxAttempts)
            {
                await WaitAsync(RetryAfter(response) ?? SignInWait, $"sign-in: EnrolHQ answered {status}", cancel);
                continue;
            }

            throw new ApiException(path, status, await ReadDetailAsync(response, cancel));
        }
    }

    private async Task PaceAsync(CancellationToken cancel)
    {
        var due = _lastRequestAt + _pace;
        var now = _now();
        if (due > now)
        {
            await _delay(due - now, cancel);
        }

        _lastRequestAt = _now();
    }

    private async Task WaitAsync(TimeSpan wait, string reason, CancellationToken cancel)
    {
        Retries++;
        _notice($"{reason}; waiting {wait.TotalSeconds:0}s before trying again");
        await _delay(wait, cancel);
    }

    private static TimeSpan Backoff(int attempt)
    {
        var seconds = Math.Pow(2, attempt);
        return seconds >= LongestWait.TotalSeconds ? LongestWait : TimeSpan.FromSeconds(seconds);
    }

    private TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        var wait = header?.Delta ?? (header?.Date is { } date ? date - _now() : null);
        if (wait is null || wait <= TimeSpan.Zero)
        {
            return null;
        }

        return wait > LongestWait ? LongestWait : wait;
    }

    /// <summary>A dropped connection or a timeout is worth trying again.</summary>
    private static bool IsTransient(Exception error, CancellationToken cancel) =>
        error is HttpRequestException or IOException
        || (error is TaskCanceledException && !cancel.IsCancellationRequested);

    /// <summary>An address that does not exist is not worth trying again: it is almost always a typo in ENROLHQ_INSTANCE.</summary>
    private static bool IsUnknownAddress(Exception error) =>
        error is HttpRequestException { InnerException: System.Net.Sockets.SocketException { SocketErrorCode: System.Net.Sockets.SocketError.HostNotFound } };

    private ApiException UnknownAddress() =>
        new(_http.BaseAddress?.Host ?? "", status: 0, "No such address. Check ENROLHQ_INSTANCE, and that this computer can reach the internet.");

    private static async Task<string> ReadDetailAsync(HttpResponseMessage response, CancellationToken cancel)
    {
        var text = await response.Content.ReadAsStringAsync(cancel);
        try
        {
            if (JsonNode.Parse(text) is JsonObject body && body["detail"] is { } detail)
            {
                return detail.ToJsonString();
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // Not JSON, for example an HTML error page from the proxy.
        }

        return text.Length > 300 ? text[..300] : text;
    }

    private static string ToQueryString(IReadOnlyDictionary<string, string> query) =>
        query.Count == 0
            ? ""
            : "?" + string.Join("&", query.Select(pair => $"{WebUtility.UrlEncode(pair.Key)}={WebUtility.UrlEncode(pair.Value)}"));
}

internal sealed class ApiException(string path, int status, string detail)
    : Exception((status == 0 ? $"Could not reach {path}. {detail}" : $"EnrolHQ answered {status} for {path}. {detail}").TrimEnd())
{
    public int Status { get; } = status;
}
