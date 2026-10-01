using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace EnrolHQ.Sync.Anonymise;

/// <summary>
/// Masks personal data in a record as the API returned it. Masking runs on the
/// nested record, before it is flattened into columns, because the rules
/// depend on where a field sits.
///
/// Text in a field with no rule is kept only when the field is on the reviewed
/// list (Reviewed.cs), or when it is a date, a time or an id. Any other text is
/// redacted, so a field EnrolHQ adds later is never stored unmasked unnoticed.
///
/// Masks as Anon.* in connector/EnrolHQ.pq does, except for the reviewed list,
/// which the connector does not have.
/// </summary>
internal static class Anonymiser
{
    /// <param name="record">The record as the API returned it. Not modified.</param>
    /// <param name="table">The table the record belongs to.</param>
    /// <param name="fallbackSeed">Seeds the fake values when the record has no id of its own.</param>
    /// <param name="unreviewed">Receives the path of each field redacted because it is not on the reviewed list.</param>
    public static JsonObject Mask(JsonObject record, string table, string fallbackSeed, ICollection<string>? unreviewed = null) =>
        MaskRecord(record, fallbackSeed, table, table, unreviewed ?? [], sameAs: null);

    /// <summary>
    /// Returns the path of every field that does not look masked. Paths only;
    /// values are never reported.
    /// </summary>
    public static List<string> Violations(JsonObject masked, string table)
    {
        var found = new List<string>();
        CollectViolations(masked, table, table, found);
        return found;
    }

    /// <summary>
    /// Text the reviewed list does not need to cover: empty, a date, a time or
    /// an id. None of these can carry a name or a contact detail.
    /// </summary>
    internal static bool NeedsNoReview(string value) =>
        value.Length == 0
        || value == Masks.Redacted
        || Guid.TryParse(value, out _)
        || DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
        || (value.Length >= 19 && value[10] == 'T' && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
        || (value.Length is 5 or 8 && TimeOnly.TryParseExact(value, ["HH:mm", "HH:mm:ss"], CultureInfo.InvariantCulture, DateTimeStyles.None, out _));

    private static JsonNode? MaskValue(JsonNode? value, string seed, string parent, string path, ICollection<string> unreviewed) => value switch
    {
        JsonObject record => MaskRecord(record, seed, parent, path, unreviewed, sameAs: null),
        JsonArray list => new JsonArray(list.Select((item, index) => MaskValue(item, $"{seed}/{index}", parent, path + "[]", unreviewed)).ToArray()),
        JsonValue leaf when IsText(leaf, out var text) && !NeedsNoReview(text) && !Reviewed.Allows(path) => Unreviewed(path, unreviewed),
        _ => value?.DeepClone(),
    };

    private static JsonNode Unreviewed(string path, ICollection<string> unreviewed)
    {
        unreviewed.Add(path);
        return Masks.Redacted;
    }

    /// <param name="sameAs">The seed of the person this record describes, when it is not identified by its own id.</param>
    private static JsonObject MaskRecord(JsonObject source, string seed, string parent, string path, ICollection<string> unreviewed, string? sameAs)
    {
        var ownSeed = sameAs ?? SeedFor(source, seed, parent);
        var isPerson = IsPerson(source);
        var masked = new JsonObject();
        foreach (var (key, value) in source)
        {
            var fieldPath = $"{path}.{key}";
            var rule = Rules.For(parent, key, isPerson);
            masked[key] = rule switch
            {
                // A parent's alumnus record is the parent, so it gets the parent's fake name.
                null when key == "alumnus" && value is JsonObject alumnus => MaskRecord(alumnus, ownSeed, key, fieldPath, unreviewed, sameAs: ownSeed),
                null => MaskValue(value, $"{ownSeed}/{key}", key, fieldPath, unreviewed),
                Rule.Sid => MaskSid(value, source, ownSeed),
                _ => Apply(rule.Value, value, $"{ownSeed}|{key}", key, ownSeed),
            };
        }

        return masked;
    }

    /// <summary>
    /// A UUID identifies one person wherever they appear, so it is the seed on
    /// its own. Numeric ids are only unique within their own kind of record.
    /// </summary>
    private static string SeedFor(JsonObject source, string seed, string parent)
    {
        // An event attendee who is a parent carries the parent's id, and is the same person.
        if (IsPerson(source) && source["parent"] is JsonValue person
            && person.GetValueKind() == JsonValueKind.String && person.GetValue<string>() is { Length: >= 32 } personId)
        {
            return personId;
        }

        if (source["id"] is not JsonValue id)
        {
            return seed;
        }

        var kind = id.GetValueKind();
        if (kind == JsonValueKind.String && id.GetValue<string>() is { Length: >= 32 } uuid)
        {
            return uuid;
        }

        return kind is JsonValueKind.String or JsonValueKind.Number ? $"{parent}:{TextOf(id)}" : seed;
    }

    private static bool IsPerson(JsonObject source) =>
        source.ContainsKey("first_name") || source.ContainsKey("last_name") || source.ContainsKey("full_name");

    /// <param name="recordSeed">The seed of the record the field belongs to.</param>
    private static JsonNode? Apply(Rule rule, JsonNode? value, string seed, string key, string recordSeed)
    {
        if (value is null || rule == Rule.Null)
        {
            return null;
        }

        return (rule, value) switch
        {
            (Rule.EmptyJson, JsonArray) => new JsonArray(),
            (Rule.EmptyJson, JsonObject) => new JsonObject(),
            (Rule.EmptyJson, _) => "{}",
            (Rule.EmptyValues, _) => EmptyValues(value),
            (Rule.RedactAll, _) => RedactLeaves(value),
            (_, JsonArray list) => new JsonArray(list.Select((item, index) => Apply(rule, item, $"{seed}/{index}", key, recordSeed)).ToArray()),
            // A record where text was expected: nothing in it is kept.
            (_, JsonObject) => RedactLeaves(value),
            _ => Scalar(rule, TextOf((JsonValue)value), seed, key, recordSeed),
        };
    }

    /// <summary>Empty stays empty, as in the SQL masks.</summary>
    private static string Scalar(Rule rule, string value, string seed, string key, string recordSeed)
    {
        if (rule == Rule.Blank || value.Length == 0)
        {
            return "";
        }

        return rule switch
        {
            Rule.FirstName => Masks.Pick(Names.First, seed),
            Rule.LastName => Masks.Pick(Names.Last, seed),
            // full_name is the record's own person, so it matches their first_name and last_name.
            // Other name fields, such as case_manager_name, are someone else.
            Rule.FullName => Masks.FullName(key == "full_name" ? recordSeed : seed),
            Rule.MobilePhone => Masks.Phone(seed, mobile: true),
            Rule.HomePhone or Rule.BusinessPhone => Masks.Phone(seed, mobile: false),
            Rule.Email => Masks.Email(seed),
            Rule.Digits => Masks.Digits(value, seed),
            Rule.Redact => Masks.Redacted,
            Rule.FileKey => Masks.RemovedFileKey + Masks.Extension(value),
            Rule.FileName => Masks.RemovedFileName + Masks.Extension(value),
            Rule.Street => Masks.Street,
            Rule.Apartment => Masks.Apartment,
            _ => throw new InvalidOperationException($"No mask for rule {rule}."),
        };
    }

    private static JsonNode? MaskSid(JsonNode? original, JsonObject source, string seed)
    {
        if (original is not JsonValue value || TextOf(value).Length == 0)
        {
            return original?.DeepClone();
        }

        return Masks.Sid(name => source[name] is JsonValue field ? TextOf(field) : "", seed);
    }

    /// <summary>Keeps the keys of an answers object and clears every value.</summary>
    private static JsonNode? EmptyValues(JsonNode value) => value switch
    {
        JsonObject record => new JsonObject(record.Select(field => KeyValuePair.Create<string, JsonNode?>(field.Key, null))),
        JsonArray => new JsonArray(),
        _ => null,
    };

    /// <summary>Redacts every piece of text and every number inside a value, however it is nested. Yes/no answers are kept.</summary>
    private static JsonNode? RedactLeaves(JsonNode? value) => value switch
    {
        JsonObject record => new JsonObject(record.Select(field => KeyValuePair.Create(field.Key, RedactLeaves(field.Value)))),
        JsonArray list => new JsonArray(list.Select(RedactLeaves).ToArray()),
        JsonValue leaf when leaf.GetValueKind() == JsonValueKind.String && leaf.GetValue<string>().Length > 0 => Masks.Redacted,
        JsonValue leaf when leaf.GetValueKind() == JsonValueKind.Number => Masks.Redacted,
        _ => value?.DeepClone(),
    };

    private static void CollectViolations(JsonNode? value, string path, string parent, List<string> found)
    {
        switch (value)
        {
            case JsonObject record:
                var isPerson = IsPerson(record);
                foreach (var (key, field) in record)
                {
                    var fieldPath = $"{path}.{key}";
                    if (Rules.For(parent, key, isPerson) is { } rule)
                    {
                        CollectRuleViolations(rule, field, fieldPath, key, found);
                    }
                    else
                    {
                        CollectViolations(field, fieldPath, key, found);
                    }
                }

                break;
            case JsonArray list:
                foreach (var item in list)
                {
                    CollectViolations(item, path + "[]", parent, found);
                }

                break;
            case JsonValue leaf when IsText(leaf, out var text) && !NeedsNoReview(text) && !Reviewed.Allows(path):
                found.Add(path);
                break;
        }
    }

    private static void CollectRuleViolations(Rule rule, JsonNode? value, string path, string key, List<string> found)
    {
        if (value is null)
        {
            return;
        }

        var conforms = (rule, value) switch
        {
            (Rule.Null, _) => false,
            (Rule.EmptyJson, JsonArray list) => list.Count == 0,
            (Rule.EmptyJson, JsonObject record) => record.Count == 0,
            (Rule.EmptyJson, _) => IsText(value, out var text) && text == "{}",
            (Rule.EmptyValues, _) => JsonNode.DeepEquals(value, EmptyValues(value)),
            (Rule.RedactAll, _) => JsonNode.DeepEquals(value, RedactLeaves(value)),
            (_, JsonObject) => JsonNode.DeepEquals(value, RedactLeaves(value)),
            (_, JsonArray) => true,
            _ => IsText(value, out var text) && Conforms(rule, text),
        };
        if (!conforms)
        {
            found.Add(path);
        }

        if (rule is not (Rule.EmptyJson or Rule.EmptyValues or Rule.RedactAll or Rule.Null) && value is JsonArray items)
        {
            foreach (var item in items)
            {
                CollectRuleViolations(rule, item, path + "[]", key, found);
            }
        }
    }

    private static bool Conforms(Rule rule, string value)
    {
        if (value.Length == 0)
        {
            return true;
        }

        switch (rule)
        {
            case Rule.FirstName:
                return Names.First.Contains(value);
            case Rule.LastName:
                return Names.Last.Contains(value);
            case Rule.FullName:
                var parts = value.Split(' ');
                return parts.Length == 2 && Names.First.Contains(parts[0]) && Names.Last.Contains(parts[1]);
            case Rule.MobilePhone:
                return Masks.IsFictionalPhone(value, mobile: true);
            case Rule.HomePhone or Rule.BusinessPhone:
                return Masks.IsFictionalPhone(value, mobile: false);
            case Rule.Email:
                return Masks.EmailDomains.Any(domain => value.EndsWith("@" + domain, StringComparison.Ordinal));
            case Rule.Digits:
                return Masks.IsDigits(value);
            case Rule.Sid:
                var pieces = value.Split('-');
                return pieces.Length >= 3
                    && Masks.IsDigits(pieces[^1])
                    && Names.First.Any(name => name[..1] == pieces[^2])
                    && Names.Last.Any(name => name.ToUpperInvariant() == pieces[^3]);
            case Rule.Redact:
                return value == Masks.Redacted;
            case Rule.FileKey:
                return value.StartsWith(Masks.RemovedFileKey, StringComparison.Ordinal);
            case Rule.FileName:
                return value.StartsWith(Masks.RemovedFileName, StringComparison.Ordinal);
            case Rule.Street:
                return value == Masks.Street;
            case Rule.Apartment:
                return value == Masks.Apartment;
            default:
                return false;
        }
    }

    private static bool IsText(JsonNode value, out string text)
    {
        if (value is JsonValue leaf && leaf.GetValueKind() == JsonValueKind.String)
        {
            text = leaf.GetValue<string>();
            return true;
        }

        text = "";
        return false;
    }

    /// <summary>A value as text: numbers as written in the JSON, true and false in lower case.</summary>
    internal static string TextOf(JsonValue value) => value.GetValueKind() switch
    {
        JsonValueKind.String => value.GetValue<string>(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => value.ToJsonString(),
    };
}
