using System.Security.Cryptography;
using System.Text;

namespace EnrolHQ.Sync.Anonymise;

/// <summary>
/// Produces the fake values. Each is worked out from a hash of a seed (the
/// record's id and the field name), never from the real value, so the same
/// person gets the same fake values in every table and on every sync.
/// </summary>
internal static class Masks
{
    public const string Redacted = "Redacted";
    public const string Street = "Fake Street, 42";
    public const string Apartment = "F101";
    public const string RemovedFileKey = "anonymized/removed-file";
    public const string RemovedFileName = "removed-file";

    public static readonly string[] EmailDomains = ["example.com", "example.net", "example.org"];
    public static readonly string[] LandlinePrefixes = ["+612", "+613", "+617", "+618"];

    private const string Alphanumeric = "abcdefghijklmnopqrstuvwxyz0123456789";

    public static byte[] Bytes(string seed) => SHA256.HashData(Encoding.UTF8.GetBytes(seed));

    public static bool IsDigits(string value) => value.All(character => character is >= '0' and <= '9');

    /// <summary>Never returns the original value.</summary>
    public static string Pick(string[] items, string seed, string original)
    {
        var bytes = Bytes(seed);
        var index = ((bytes[0] * 65536) + (bytes[1] * 256) + bytes[2]) % items.Length;
        var candidate = items[index];
        return string.Equals(candidate, original.Trim(), StringComparison.OrdinalIgnoreCase)
            ? items[(index + 1) % items.Length]
            : candidate;
    }

    public static string FullName(string original, string seed)
    {
        var parts = original.Trim().Split(' ');
        var first = parts[0];
        var last = parts.Length > 1 ? parts[1] : "";
        return Pick(Names.First, seed + "|first", first) + " " + Pick(Names.Last, seed + "|last", last);
    }

    public static string Phone(string original, string seed, bool mobile)
    {
        var bytes = Bytes(seed);
        var leading = (1 + (bytes[1] % 9)).ToString();
        var candidate = mobile
            ? "+6143" + leading + DigitString(bytes, 2, 6)
            : LandlinePrefixes[bytes[0] % 4] + leading + DigitString(bytes, 2, 7);
        return candidate == original ? candidate[..11] + ShiftDigits(candidate[^1..]) : candidate;
    }

    public static string Email(string seed)
    {
        var bytes = Bytes(seed);
        var name = new string(bytes.Take(20).Select(value => Alphanumeric[value % 36]).ToArray());
        return name + "@" + EmailDomains[bytes[20] % 3];
    }

    /// <summary>Random digits of the same length as the original.</summary>
    public static string Digits(string original, string seed)
    {
        var candidate = DigitString(Bytes(seed), 0, original.Length);
        return candidate == original ? ShiftDigits(candidate) : candidate;
    }

    /// <summary>
    /// The real sid is built from the date of birth, entry grade, entry year,
    /// surname and first initial. This rebuilds it from the record's own kept
    /// fields and its fake names, and never reads the original sid. The
    /// trailing number keeps it unique, like the counter EnrolHQ adds to
    /// duplicates.
    /// </summary>
    public static string Sid(Func<string, string> field, string seed)
    {
        var first = Pick(Names.First, seed + "|first_name", field("first_name"));
        var last = Pick(Names.Last, seed + "|last_name", field("last_name"));
        var bytes = Bytes(seed + "|sid");
        var counter = (1 + (((bytes[0] * 256) + bytes[1]) % 999)).ToString();
        var kept = new[] { field("dob").Replace("-", ""), field("entry_grade"), field("entry_year") }
            .Where(part => part.Length > 0);
        return string.Join("-", kept.Concat([last.ToUpperInvariant(), first[..1], counter]));
    }

    /// <summary>".pdf" from a file key, file name or URL; "" when there is no short extension.</summary>
    public static string Extension(string value)
    {
        var path = value.Split('?', '#')[0];
        var name = path.Split('/')[^1];
        var extension = name.Contains('.') ? name.Split('.')[^1] : "";
        var valid = extension.Length is >= 1 and <= 5 && extension.All(char.IsAsciiLetterOrDigit);
        return valid ? "." + extension : "";
    }

    /// <summary>Shifts every digit by one, so the result can never equal the input.</summary>
    private static string ShiftDigits(string value) =>
        new(value.Select(character => character is >= '0' and <= '9' ? (char)('0' + ((character - '0' + 1) % 10)) : character).ToArray());

    private static string DigitString(byte[] bytes, int offset, int length) =>
        new(Enumerable.Range(offset, length).Select(position => (char)('0' + (bytes[position % bytes.Length] % 10))).ToArray());
}
