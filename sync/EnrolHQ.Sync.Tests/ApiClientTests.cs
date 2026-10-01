using System.Net;

namespace EnrolHQ.Sync.Tests;

public class ApiClientTests
{
    private static readonly Dictionary<string, string> NoQuery = [];

    [Fact]
    public async Task Signs_in_once_however_many_requests_it_makes()
    {
        var api = new FakeApi();
        api.Serve("staff/", 3);
        using var client = api.Client();

        for (var request = 0; request < 5; request++)
        {
            await client.GetAsync("staff/", NoQuery, CancellationToken.None);
        }

        Assert.Equal(1, client.SignIns);
        Assert.Single(api.Requests, request => request.StartsWith("POST accounts/refresh/"));
    }

    [Fact]
    public async Task Waits_as_long_as_the_api_asks_when_it_is_told_to_slow_down()
    {
        var api = new FakeApi();
        api.Serve("staff/", 3);
        using var client = api.Client();
        await client.GetAsync("staff/", NoQuery, CancellationToken.None);
        api.Waits.Clear();

        api.FailNext(HttpStatusCode.TooManyRequests, retryAfter: TimeSpan.FromSeconds(7));
        var body = await client.GetAsync("staff/", NoQuery, CancellationToken.None);

        Assert.Equal(3, (int)body!["count"]!);
        Assert.Equal([TimeSpan.FromSeconds(7)], api.Waits);
        Assert.Equal(1, client.Retries);
    }

    [Fact]
    public async Task Backs_off_further_each_time_when_the_api_gives_no_wait()
    {
        var api = new FakeApi();
        api.Serve("staff/", 3);
        using var client = api.Client();
        await client.GetAsync("staff/", NoQuery, CancellationToken.None);
        api.Waits.Clear();

        api.FailNext(HttpStatusCode.TooManyRequests);
        api.FailNext(HttpStatusCode.ServiceUnavailable);
        api.FailNext(HttpStatusCode.TooManyRequests);
        await client.GetAsync("staff/", NoQuery, CancellationToken.None);

        Assert.Equal([TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8)], api.Waits);
    }

    [Fact]
    public async Task Gives_up_with_a_clear_error_when_the_api_never_recovers()
    {
        var api = new FakeApi();
        api.Serve("staff/", 3);
        using var client = api.Client();
        await client.GetAsync("staff/", NoQuery, CancellationToken.None);
        foreach (var _ in Enumerable.Range(0, 20))
        {
            api.FailNext(HttpStatusCode.TooManyRequests);
        }

        var error = await Assert.ThrowsAsync<ApiException>(() => client.GetAsync("staff/", NoQuery, CancellationToken.None));

        Assert.Equal(429, error.Status);
        Assert.Contains("staff/", error.Message);
        Assert.All(api.Waits, wait => Assert.True(wait <= TimeSpan.FromSeconds(60)));
    }

    [Fact]
    public async Task Signs_in_again_when_its_token_has_been_cancelled()
    {
        var api = new FakeApi();
        api.Serve("staff/", 3);
        using var client = api.Client();
        await client.GetAsync("staff/", NoQuery, CancellationToken.None);

        api.SignInElsewhere();
        var body = await client.GetAsync("staff/", NoQuery, CancellationToken.None);

        Assert.Equal(3, (int)body!["count"]!);
        Assert.Equal(2, client.SignIns);
    }

    [Fact]
    public async Task Signs_in_again_before_the_token_runs_out()
    {
        var api = new FakeApi();
        api.Serve("staff/", 3);
        using var client = api.Client();
        await client.GetAsync("staff/", NoQuery, CancellationToken.None);

        await api.Delay(TimeSpan.FromMinutes(51), CancellationToken.None);
        await client.GetAsync("staff/", NoQuery, CancellationToken.None);

        Assert.Equal(2, client.SignIns);
        // It did not wait to be refused first: sign in, fetch, sign in, fetch.
        Assert.Equal(4, api.Requests.Count);
    }

    [Fact]
    public async Task Waits_when_sign_ins_are_limited()
    {
        var api = new FakeApi();
        api.Serve("staff/", 3);
        using var client = api.Client();

        api.FailNext(HttpStatusCode.TooManyRequests);
        await client.GetAsync("staff/", NoQuery, CancellationToken.None);

        Assert.Equal([TimeSpan.FromSeconds(15)], api.Waits);
    }

    [Fact]
    public async Task Says_so_when_the_api_token_is_wrong()
    {
        var api = new FakeApi();
        using var client = api.Client();
        api.FailNext(HttpStatusCode.Unauthorized);

        var error = await Assert.ThrowsAsync<ApiException>(() => client.GetAsync("staff/", NoQuery, CancellationToken.None));

        Assert.Contains("did not accept the API token", error.Message);
    }

    [Fact]
    public async Task Leaves_a_gap_between_requests()
    {
        var api = new FakeApi();
        api.Serve("staff/", 3);
        using var client = api.Client(pace: TimeSpan.FromMilliseconds(300));

        for (var request = 0; request < 4; request++)
        {
            await client.GetAsync("staff/", NoQuery, CancellationToken.None);
        }

        // The fake clock only moves when the client waits, so every request after the first had to wait the full gap.
        Assert.Equal(Enumerable.Repeat(TimeSpan.FromMilliseconds(300), 3), api.Waits);
    }

    [Fact]
    public async Task Does_not_retry_a_request_the_api_refuses()
    {
        var api = new FakeApi();
        using var client = api.Client();

        var error = await Assert.ThrowsAsync<ApiException>(() => client.GetAsync("no-such-endpoint/", NoQuery, CancellationToken.None));

        Assert.Equal(404, error.Status);
        Assert.Single(api.Requests, request => request.StartsWith("GET"));
    }
}
