using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using EnrolHQ.Sync.Anonymise;

namespace EnrolHQ.Sync;

/// <summary>Downloads tables into the store, one page at a time, and picks up where a stopped run left off.</summary>
internal sealed class SyncRunner(bool anonymise, Store store, ApiClient api, Reporter reporter, Func<DateTimeOffset>? clock = null)
{
    /// <summary>An unfinished download older than this starts again, because pages will have shifted.</summary>
    private static readonly TimeSpan ResumeWindow = TimeSpan.FromHours(6);

    /// <summary>Changes are requested from a little before the last sync started, in case clocks differ.</summary>
    private static readonly TimeSpan Overlap = TimeSpan.FromMinutes(5);

    private readonly Func<DateTimeOffset> _now = clock ?? (() => DateTimeOffset.UtcNow);

    /// <summary>Values the safety net redacted, counted by field path.</summary>
    public Dictionary<string, int> SafetyNetHits { get; } = new(StringComparer.Ordinal);

    public async Task SyncAsync(Endpoint endpoint, bool full, CancellationToken cancel)
    {
        var state = store.GetState(endpoint.Table);
        var resuming = !full
            && state is { Cycle: > 0, CompletedAt: null, NextPage: > 1, StartedAt: not null }
            && _now() - state.StartedAt < ResumeWindow;

        if (!resuming)
        {
            var incremental = endpoint.UpdatedAfter && !full && state.Watermark is not null && store.Count(endpoint.Table) > 0;
            var updatedAfter = incremental
                ? (state.Watermark!.Value - Overlap).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'+00:00'", CultureInfo.InvariantCulture)
                : null;
            state = store.StartCycle(endpoint.Table, updatedAfter, _now());
        }

        var what = state.UpdatedAfter is null ? "" : "changes since last sync, ";
        if (resuming)
        {
            reporter.Line($"{endpoint.Table}: carrying on from page {state.NextPage}");
        }

        var timer = Stopwatch.StartNew();
        var firstPage = state.NextPage;
        var saved = 0;
        for (var page = state.NextPage; ; page++)
        {
            var query = new Dictionary<string, string>(endpoint.Query ?? new Dictionary<string, string>())
            {
                ["page"] = page.ToString(CultureInfo.InvariantCulture),
                ["page_size"] = endpoint.PageSize.ToString(CultureInfo.InvariantCulture),
            };
            if (state.UpdatedAfter is not null)
            {
                query["updated_after"] = state.UpdatedAfter;
            }

            var body = await api.GetAsync(endpoint.Path, query, cancel);
            var (records, total, hasNext) = ReadPage(body, endpoint);
            var prepared = records.Select(record => (endpoint.KeyOf(record), Prepare(record, endpoint))).ToList();
            store.SavePage(endpoint.Table, state.Cycle, page, total, prepared, _now());
            saved += prepared.Count;

            reporter.Progress(Describe(endpoint, what, page, firstPage, total, timer.Elapsed));
            if (!hasNext)
            {
                break;
            }
        }

        var removed = store.CompleteCycle(endpoint.Table, removeUnseen: state.UpdatedAfter is null, _now());
        var summary = $"{endpoint.Table}: {Reporter.Number(store.Count(endpoint.Table))} records"
            + (state.UpdatedAfter is null ? "" : $", {Reporter.Number(saved)} changed")
            + (removed > 0 ? $", {Reporter.Number(removed)} removed" : "")
            + $" ({Reporter.Duration(timer.Elapsed)})";
        reporter.Line(summary);
    }

    /// <summary>Masks the record when anonymisation is on. Nothing unmasked is ever handed to the store.</summary>
    private JsonObject Prepare(JsonObject record, Endpoint endpoint)
    {
        if (!anonymise)
        {
            return record;
        }

        var masked = Anonymiser.Mask(record, endpoint.Table, $"{endpoint.Table}#{endpoint.KeyOf(record)}");
        var violations = Anonymiser.Violations(masked, endpoint.Table);
        if (violations.Count > 0)
        {
            throw new AnonymisationException(endpoint.Table, violations);
        }

        foreach (var path in SafetyNet.Apply(record, masked, endpoint.Table))
        {
            SafetyNetHits[path] = SafetyNetHits.GetValueOrDefault(path) + 1;
        }

        return masked;
    }

    /// <summary>
    /// Most endpoints answer { count, next, previous, results }. One that
    /// answers with a bare list is treated as a single page.
    /// </summary>
    private static (List<JsonObject> Records, int? Total, bool HasNext) ReadPage(JsonNode? body, Endpoint endpoint)
    {
        switch (body)
        {
            case JsonArray list:
                return (list.OfType<JsonObject>().ToList(), list.Count, false);
            case JsonObject page when page["results"] is JsonArray results:
                var total = page["count"] is JsonValue count ? count.GetValue<int>() : (int?)null;
                return (results.OfType<JsonObject>().ToList(), total, page["next"] is not null);
            default:
                throw new InvalidOperationException($"{endpoint.Path} answered with something that is not a list of records.");
        }
    }

    private static string Describe(Endpoint endpoint, string what, int page, int firstPage, int? total, TimeSpan elapsed)
    {
        if (total is null or 0)
        {
            return $"{endpoint.Table}: {what}page {page}";
        }

        var pages = (int)Math.Ceiling(total.Value / (double)endpoint.PageSize);
        var done = Math.Min((long)page * endpoint.PageSize, total.Value);
        var remaining = pages - page;
        var perPage = elapsed / (page - firstPage + 1);
        var left = remaining > 0 ? $", about {Reporter.Duration(perPage * remaining)} left" : "";
        return $"{endpoint.Table}: {what}{Reporter.Number(done)} of {Reporter.Number(total.Value)} · page {page} of {pages}{left}";
    }
}

internal sealed class AnonymisationException(string table, IReadOnlyList<string> fields)
    : Exception(
        $"Anonymisation check failed for {table}, so nothing from this page was stored. "
        + $"Fields that did not look masked: {string.Join(", ", fields.Distinct().Take(20))}")
{
    public IReadOnlyList<string> Fields { get; } = fields;
}
