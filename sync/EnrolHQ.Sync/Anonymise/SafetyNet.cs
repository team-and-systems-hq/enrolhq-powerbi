using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace EnrolHQ.Sync.Anonymise;

/// <summary>
/// Catches real names hiding in fields the rules keep.
///
/// The rules work on field names, so they cannot see a surname typed into an
/// occupation, a school name or any other field that is normally harmless.
/// After masking, this looks through every text value the rules left
/// unchanged. If one contains the surname or email address of anyone named in
/// the same record, the whole value is redacted.
///
/// Surnames come from every field with a surname, full name or email rule.
/// Each part of a hyphenated or multi-word surname counts on its own. Surnames
/// of five letters or more match anywhere in the text; shorter ones (Lee, Ng,
/// Chen) only as a whole word, so Lee does not match Leeds.
///
/// First names are not matched. Too many are ordinary words (Grace, Hope,
/// Summer), and redacting on those would remove data for no benefit.
/// </summary>
internal static class SafetyNet
{
    private const int MatchAnywhereFrom = 5;
    private const int ShortestSecret = 2;

    /// <summary>Redacts in place.</summary>
    /// <param name="original">The record as the API returned it.</param>
    /// <param name="masked">The same record after masking. Modified.</param>
    /// <param name="table">The table the record belongs to, used as the start of each path.</param>
    /// <returns>The path of each value that was redacted. Paths only, never values.</returns>
    public static List<string> Apply(JsonObject original, JsonObject masked, string table)
    {
        var secrets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CollectSecrets(original, masked, table, secrets);
        var redacted = new List<string>();
        if (secrets.Count > 0)
        {
            var matchers = secrets.Select(Matcher).ToList();
            Walk(original, masked, table, table, matchers, redacted);
        }

        return redacted;
    }

    private static Func<string, bool> Matcher(string secret)
    {
        if (secret.Length >= MatchAnywhereFrom || secret.Contains('@'))
        {
            return text => text.Contains(secret, StringComparison.OrdinalIgnoreCase);
        }

        var word = new Regex($@"(?<![\p{{L}}\p{{N}}]){Regex.Escape(secret)}(?![\p{{L}}\p{{N}}])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return word.IsMatch;
    }

    /// <summary>
    /// Surnames and email addresses that masking replaced. One that the rules
    /// kept on purpose, such as a campus's own email address, is not a secret.
    /// </summary>
    private static void CollectSecrets(JsonNode? original, JsonNode? masked, string parent, HashSet<string> secrets)
    {
        switch (original, masked)
        {
            case (JsonObject originalRecord, JsonObject maskedRecord):
                var isPerson = originalRecord.ContainsKey("first_name") || originalRecord.ContainsKey("last_name") || originalRecord.ContainsKey("full_name");
                foreach (var (key, field) in originalRecord)
                {
                    var maskedField = maskedRecord[key];
                    if (field is JsonValue leaf && leaf.GetValueKind() == JsonValueKind.String)
                    {
                        var text = leaf.GetValue<string>().Trim();
                        var replaced = maskedField is not JsonValue maskedLeaf
                            || maskedLeaf.GetValueKind() != JsonValueKind.String
                            || maskedLeaf.GetValue<string>() != leaf.GetValue<string>();
                        var parts = replaced ? Rules.For(parent, key, isPerson) switch
                        {
                            Rule.Email => [text],
                            Rule.LastName => Words(text),
                            // The surname is every word after the first.
                            Rule.FullName => Words(text).Skip(1),
                            _ => [],
                        } : [];
                        foreach (var part in parts.Where(part => part.Length >= ShortestSecret))
                        {
                            secrets.Add(part);
                        }
                    }
                    else
                    {
                        CollectSecrets(field, maskedField, key, secrets);
                    }
                }

                break;
            case (JsonArray originalList, JsonArray maskedList) when originalList.Count == maskedList.Count:
                for (var index = 0; index < originalList.Count; index++)
                {
                    CollectSecrets(originalList[index], maskedList[index], parent, secrets);
                }

                break;
        }
    }

    /// <summary>"Whitlock-Parker" gives Whitlock-Parker, Whitlock and Parker.</summary>
    private static IEnumerable<string> Words(string text)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        foreach (var word in words)
        {
            yield return word;
            if (word.Contains('-'))
            {
                foreach (var piece in word.Split('-', StringSplitOptions.RemoveEmptyEntries))
                {
                    yield return piece;
                }
            }
        }
    }

    private static void Walk(JsonNode? original, JsonNode? masked, string path, string parent, List<Func<string, bool>> secrets, List<string> redacted)
    {
        switch (original, masked)
        {
            case (JsonObject originalRecord, JsonObject maskedRecord):
                var isPerson = originalRecord.ContainsKey("first_name") || originalRecord.ContainsKey("last_name") || originalRecord.ContainsKey("full_name");
                foreach (var (key, field) in originalRecord)
                {
                    // A field with a rule is already masked. A fake surname can
                    // by chance be the real one; redacting it would give that away.
                    if (Rules.For(parent, key, isPerson) is not null || !maskedRecord.TryGetPropertyValue(key, out var maskedField))
                    {
                        continue;
                    }

                    var fieldPath = $"{path}.{key}";
                    if (IsKeptText(field, maskedField, key, out var text) && secrets.Any(matches => matches(text)))
                    {
                        maskedRecord[key] = Masks.Redacted;
                        redacted.Add(fieldPath);
                    }
                    else
                    {
                        Walk(field, maskedField, fieldPath, key, secrets, redacted);
                    }
                }

                break;
            case (JsonArray originalList, JsonArray maskedList) when originalList.Count == maskedList.Count:
                for (var index = 0; index < originalList.Count; index++)
                {
                    if (IsKeptText(originalList[index], maskedList[index], "", out var text) && secrets.Any(matches => matches(text)))
                    {
                        maskedList[index] = Masks.Redacted;
                        redacted.Add(path + "[]");
                    }
                    else
                    {
                        Walk(originalList[index], maskedList[index], path + "[]", parent, secrets, redacted);
                    }
                }

                break;
        }
    }

    /// <summary>
    /// True for text the rules left exactly as it was. Ids are skipped,
    /// including external_id and student_code: they join tables together and
    /// match records to the school's own systems, so they are always kept.
    /// </summary>
    private static bool IsKeptText(JsonNode? original, JsonNode? masked, string key, out string text)
    {
        text = "";
        if (original is not JsonValue originalLeaf
            || masked is not JsonValue maskedLeaf
            || originalLeaf.GetValueKind() != JsonValueKind.String
            || maskedLeaf.GetValueKind() != JsonValueKind.String)
        {
            return false;
        }

        text = originalLeaf.GetValue<string>();
        return text == maskedLeaf.GetValue<string>()
            && key is not ("id" or "student_code")
            && !key.EndsWith("_id", StringComparison.Ordinal)
            && !Guid.TryParse(text, out _);
    }
}
