using System.Text.Json;
using System.Text.Json.Nodes;

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
/// First names are not matched. Too many are ordinary words (Grace, Hope,
/// Summer), and redacting on those would remove data for no benefit.
/// </summary>
internal static class SafetyNet
{
    private const int ShortestSecret = 5;

    /// <summary>Redacts in place.</summary>
    /// <param name="original">The record as the API returned it.</param>
    /// <param name="masked">The same record after masking. Modified.</param>
    /// <param name="table">The table the record belongs to, used as the start of each path.</param>
    /// <returns>The path of each value that was redacted. Paths only, never values.</returns>
    public static List<string> Apply(JsonObject original, JsonObject masked, string table)
    {
        var secrets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CollectSecrets(original, masked, secrets);
        var redacted = new List<string>();
        if (secrets.Count > 0)
        {
            Walk(original, masked, table, [.. secrets], redacted);
        }

        return redacted;
    }

    /// <summary>
    /// Surnames and email addresses that masking replaced. One that the rules
    /// kept on purpose, such as a campus's own email address, is not a secret.
    /// </summary>
    private static void CollectSecrets(JsonNode? original, JsonNode? masked, HashSet<string> secrets)
    {
        switch (original, masked)
        {
            case (JsonObject originalRecord, JsonObject maskedRecord):
                foreach (var (key, field) in originalRecord)
                {
                    var maskedField = maskedRecord[key];
                    if (field is JsonValue leaf && leaf.GetValueKind() == JsonValueKind.String)
                    {
                        var text = leaf.GetValue<string>().Trim();
                        var replaced = maskedField is not JsonValue maskedLeaf
                            || maskedLeaf.GetValueKind() != JsonValueKind.String
                            || maskedLeaf.GetValue<string>() != leaf.GetValue<string>();
                        var parts = key switch
                        {
                            _ when !replaced => [],
                            "last_name" or "email" => [text],
                            // The surname is every word after the first.
                            "full_name" => text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1),
                            _ => Enumerable.Empty<string>(),
                        };
                        foreach (var part in parts.Where(part => part.Length >= ShortestSecret))
                        {
                            secrets.Add(part);
                        }
                    }
                    else
                    {
                        CollectSecrets(field, maskedField, secrets);
                    }
                }

                break;
            case (JsonArray originalList, JsonArray maskedList) when originalList.Count == maskedList.Count:
                for (var index = 0; index < originalList.Count; index++)
                {
                    CollectSecrets(originalList[index], maskedList[index], secrets);
                }

                break;
        }
    }

    private static void Walk(JsonNode? original, JsonNode? masked, string path, string[] secrets, List<string> redacted)
    {
        switch (original, masked)
        {
            case (JsonObject originalRecord, JsonObject maskedRecord):
                foreach (var (key, field) in originalRecord)
                {
                    if (!maskedRecord.TryGetPropertyValue(key, out var maskedField))
                    {
                        continue;
                    }

                    var fieldPath = $"{path}.{key}";
                    if (IsKeptText(field, maskedField, key, out var text) && ContainsSecret(text, secrets))
                    {
                        maskedRecord[key] = Masks.Redacted;
                        redacted.Add(fieldPath);
                    }
                    else
                    {
                        Walk(field, maskedField, fieldPath, secrets, redacted);
                    }
                }

                break;
            case (JsonArray originalList, JsonArray maskedList) when originalList.Count == maskedList.Count:
                for (var index = 0; index < originalList.Count; index++)
                {
                    if (IsKeptText(originalList[index], maskedList[index], "", out var text) && ContainsSecret(text, secrets))
                    {
                        maskedList[index] = Masks.Redacted;
                        redacted.Add(path + "[]");
                    }
                    else
                    {
                        Walk(originalList[index], maskedList[index], path + "[]", secrets, redacted);
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

    private static bool ContainsSecret(string text, string[] secrets) =>
        secrets.Any(secret => text.Contains(secret, StringComparison.OrdinalIgnoreCase));
}
