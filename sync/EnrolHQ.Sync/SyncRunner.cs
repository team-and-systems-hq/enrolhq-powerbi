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

    /// <summary>A list that keeps changing while it is downloaded is downloaded at most this many times in one run.</summary>
    private const int MostAttempts = 3;

    /// <summary>More applications than this missing from application_details is a job for --full, not for fetching one by one.</summary>
    private const int MostToFetchOneByOne = 2000;

    /// <summary>
    /// Meta keys: the rules version a table was last fully masked under, the
    /// one its current download started under, and the last download this
    /// version of the tool started, which shows whether an older version has
    /// written to the copy since.
    /// </summary>
    private const string MaskedUnder = "masking_rules:";
    private const string StartedUnder = "masking_rules_started:";
    private const string StartedByThisTool = "masking_rules_cycle:";

    /// <summary>A copy made before versions were recorded was masked under the first rules.</summary>
    private const int FirstRulesVersion = 1;

    private readonly Func<DateTimeOffset> _now = clock ?? (() => DateTimeOffset.UtcNow);

    /// <summary>Tables downloaded in full in this run while their list stayed the same.</summary>
    private readonly HashSet<string> _complete = new(StringComparer.Ordinal);

    /// <summary>Values the safety net redacted, counted by field path.</summary>
    public Dictionary<string, int> SafetyNetHits { get; } = new(StringComparer.Ordinal);

    /// <summary>Values redacted because their field is not on the reviewed list, counted by field path.</summary>
    public Dictionary<string, int> UnreviewedHits { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// True when the table holds records masked under older rules than this
    /// version of the tool has, or an older version has downloaded into it
    /// since. Only meaningful for an anonymised copy.
    /// </summary>
    public static bool IsOutdated(Store store, string table)
    {
        if (store.Count(table) == 0)
        {
            return false;
        }

        var cycle = Number(store, StartedByThisTool + table);
        return (Number(store, MaskedUnder + table) ?? FirstRulesVersion) < Rules.Version
            || (cycle is not null && cycle != store.GetState(table).Cycle);
    }

    public async Task SyncAsync(Endpoint endpoint, bool full, CancellationToken cancel)
    {
        for (var attempt = 1; ; attempt++)
        {
            var (finished, changed) = await DownloadAsync(endpoint, full, restart: attempt > 1, cancel);
            if (finished)
            {
                return;
            }

            if (attempt == MostAttempts)
            {
                await FinishAnywayAsync(endpoint, changed);
                return;
            }

            reporter.Line($"{endpoint.Table}: the list changed in EnrolHQ during the download, so it is downloaded again");
        }
    }

    /// <summary>
    /// application_details is downloaded by changes only, which can neither
    /// notice an application deleted in EnrolHQ nor one skipped because the
    /// list moved during a download. The applications list, downloaded in full
    /// each run, says which applications exist: anything missing is fetched on
    /// its own, and anything extra is removed.
    /// </summary>
    public async Task ReconcileDetailsAsync(Endpoint details, CancellationToken cancel)
    {
        if (!_complete.Contains("applications") || store.GetState(details.Table).CompletedAt is null)
        {
            reporter.Line($"{details.Table}: not checked against the applications list, which was not downloaded in full in this run");
            return;
        }

        var listed = store.Ids("applications");
        var held = store.Ids(details.Table);
        var extra = held.Where(id => !listed.Contains(id)).ToList();
        var missing = listed.Where(id => !held.Contains(id)).ToList();
        if (missing.Count > MostToFetchOneByOne)
        {
            reporter.Line($"{details.Table}: {Reporter.Number(missing.Count)} applications are missing. Run enrolhq-sync --full --only {details.Table}");
            return;
        }

        var cycle = store.GetState(details.Table).Cycle;
        var fetched = 0;
        foreach (var id in missing)
        {
            JsonNode? body;
            try
            {
                body = await api.GetAsync($"{details.Path}{Uri.EscapeDataString(id)}/", new Dictionary<string, string>(), cancel);
            }
            catch (ApiException error) when (error.Status == 404)
            {
                // Deleted since the list was downloaded.
                continue;
            }

            if (body is JsonObject record)
            {
                store.Save(details.Table, cycle, [(details.KeyOf(record), Prepare(record, details))], _now());
                fetched++;
            }
        }

        var removed = store.Remove(details.Table, extra);
        if (fetched > 0 || removed > 0)
        {
            reporter.Line($"{details.Table}: checked against the applications list: {Reporter.Number(fetched)} fetched that were missing, {Reporter.Number(removed)} removed that no longer exist");
        }
    }

    /// <returns>Whether the table is finished, and whether its list changed during the download.</returns>
    private async Task<(bool Finished, bool Changed)> DownloadAsync(Endpoint endpoint, bool full, bool restart, CancellationToken cancel)
    {
        var state = store.GetState(endpoint.Table);
        var outdated = anonymise && IsOutdated(store, endpoint.Table);
        var resuming = !restart
            && state is { Cycle: > 0, CompletedAt: null, NextPage: > 1, StartedAt: not null }
            && _now() - state.StartedAt < ResumeWindow
            // --full carries on with an unfinished full download, not one of changes only.
            && (!full || state.UpdatedAfter is null)
            // An unfinished download made under older rules is not worth finishing.
            && (!anonymise || (Number(store, StartedUnder + endpoint.Table) == Rules.Version
                && Number(store, StartedByThisTool + endpoint.Table) == state.Cycle));

        if (!resuming)
        {
            // A copy masked under older rules still holds whatever those rules
            // left unmasked, so changes alone are not enough.
            var incremental = endpoint.UpdatedAfter && !full && !outdated && state.Watermark is not null && store.Count(endpoint.Table) > 0;
            if (outdated && endpoint.UpdatedAfter && !full && state.Watermark is not null)
            {
                reporter.Line($"{endpoint.Table}: the masking rules have changed since this was downloaded, so all of it is downloaded again");
            }

            var updatedAfter = incremental
                ? (state.Watermark!.Value - Overlap).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'+00:00'", CultureInfo.InvariantCulture)
                : null;
            if (anonymise && store.Count(endpoint.Table) == 0)
            {
                // Every record an empty table receives is masked by this
                // version, even if its first download is stopped part-way.
                store.SetMeta(MaskedUnder + endpoint.Table, Rules.Version.ToString(CultureInfo.InvariantCulture));
            }

            state = store.StartCycle(endpoint.Table, updatedAfter, _now());
            StampStart(endpoint.Table);
        }

        var what = state.UpdatedAfter is null ? "" : "changes since last sync, ";
        if (resuming)
        {
            reporter.Line($"{endpoint.Table}: carrying on from page {state.NextPage}");
        }

        var timer = Stopwatch.StartNew();
        var firstPage = state.NextPage;
        // The number of records the list held when the download started. A
        // different number later means records were added or removed, and the
        // pages moved under the download.
        var expected = resuming ? state.Total : null;
        var changed = false;
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

            JsonNode? body;
            try
            {
                body = await api.GetAsync(endpoint.Path, query, cancel);
            }
            catch (ApiException error) when (error.Status == 404 && page > 1)
            {
                // The list got shorter and this page no longer exists.
                return (false, true);
            }

            if (page == 1)
            {
                AllowForClocks(endpoint.Table, state);
            }

            var (records, total, hasNext) = ReadPage(body, endpoint);
            expected ??= total;
            changed |= total != expected;
            var prepared = records.Select(record => (endpoint.KeyOf(record), Prepare(record, endpoint))).ToList();
            store.SavePage(endpoint.Table, state.Cycle, page, total, prepared, _now());
            saved += prepared.Count;

            reporter.Progress(Describe(endpoint, what, page, firstPage, total, timer.Elapsed));
            if (!hasNext)
            {
                break;
            }
        }

        var fullCycle = state.UpdatedAfter is null;
        if (changed && fullCycle && !endpoint.UpdatedAfter)
        {
            // Records may have been skipped, and removing what was not seen
            // could delete records that still exist. Download it again.
            return (false, true);
        }

        // A full download of application_details that saw the list change
        // keeps what it did not see: checking against the applications list
        // removes what no longer exists. Changes only, when the list changed,
        // asks for the same changes again next time, in case some were skipped.
        var removed = store.CompleteCycle(endpoint.Table, state.Cycle, removeUnseen: fullCycle && !changed, _now(), advanceWatermark: !changed);
        Finish(endpoint, state, fullCycle, changed, saved, removed, timer.Elapsed);
        return (true, changed);
    }

    private Task FinishAnywayAsync(Endpoint endpoint, bool changed)
    {
        var state = store.GetState(endpoint.Table);
        store.CompleteCycle(endpoint.Table, state.Cycle, removeUnseen: false, _now(), advanceWatermark: !changed);
        reporter.Line($"{endpoint.Table}: the list kept changing in EnrolHQ during the download. Some records may be missing or out of date until the next sync");
        return Task.CompletedTask;
    }

    private void Finish(Endpoint endpoint, SyncState state, bool fullCycle, bool changed, int saved, int removed, TimeSpan elapsed)
    {
        if (anonymise && fullCycle)
        {
            // Every record left in the table was masked by this download.
            store.SetMeta(MaskedUnder + endpoint.Table, Rules.Version.ToString(CultureInfo.InvariantCulture));
        }

        if (fullCycle && !changed)
        {
            _complete.Add(endpoint.Table);
        }

        var summary = $"{endpoint.Table}: {Reporter.Number(store.Count(endpoint.Table))} records"
            + (state.UpdatedAfter is null ? "" : $", {Reporter.Number(saved)} changed")
            + (removed > 0 ? $", {Reporter.Number(removed)} removed" : "")
            + $" ({Reporter.Duration(elapsed)})";
        reporter.Line(summary);
    }

    private void StampStart(string table)
    {
        store.SetMeta(StartedUnder + table, Rules.Version.ToString(CultureInfo.InvariantCulture));
        store.SetMeta(StartedByThisTool + table, store.GetState(table).Cycle.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Changes are asked for by EnrolHQ's clock. When it is behind this
    /// computer's, the download is treated as starting that much earlier, so
    /// the next run's changes start early enough.
    /// </summary>
    private void AllowForClocks(string table, SyncState state)
    {
        if (api.ServerTime is { } server && state.StartedAt is { } started && api.AnsweredAt - server > TimeSpan.FromSeconds(2))
        {
            store.MoveStart(table, started - (api.AnsweredAt - server));
        }
    }

    private static int? Number(Store store, string key) =>
        int.TryParse(store.GetMeta(key), NumberStyles.None, CultureInfo.InvariantCulture, out var version) ? version : null;

    /// <summary>Masks the record when anonymisation is on. Nothing unmasked is ever handed to the store.</summary>
    private JsonObject Prepare(JsonObject record, Endpoint endpoint)
    {
        if (!anonymise)
        {
            return record;
        }

        var unreviewed = new HashSet<string>(StringComparer.Ordinal);
        var masked = Anonymiser.Mask(record, endpoint.Table, $"{endpoint.Table}#{endpoint.KeyOf(record)}", unreviewed);
        var violations = Anonymiser.Violations(masked, endpoint.Table);
        if (violations.Count > 0)
        {
            throw new AnonymisationException(endpoint.Table, violations);
        }

        foreach (var path in unreviewed)
        {
            UnreviewedHits[path] = UnreviewedHits.GetValueOrDefault(path) + 1;
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
                throw new ApiException(endpoint.Path, 200, "The answer was not a list of records.");
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
