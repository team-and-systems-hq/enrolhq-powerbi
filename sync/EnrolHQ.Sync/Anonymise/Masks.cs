using System.Security.Cryptography;
using System.Text;

namespace EnrolHQ.Sync.Anonymise;

/// <summary>
/// Produces the fake values. Each is worked out from a hash of a seed (the
/// record's id and the field name) and never looks at the real value, so the
/// same person gets the same fake values in every table and on every sync, and
/// a fake value says nothing about the real one. By chance a fake value can be
/// the same as the real one; avoiding that would make it depend on the real one.
/// </summary>
internal static class Masks
{
    public const string Redacted = "Redacted";
    public const string Street = "Fake Street, 42";
    public const string Apartment = "F101";
    public const string RemovedFileKey = "anonymized/removed-file";
    public const string RemovedFileName = "removed-file";

    public static readonly string[] EmailDomains = ["example.com", "example.net", "example.org"];

    /// <summary>
    /// Mobile numbers the ACMA sets aside for fiction, so a fake number never
    /// rings a real person.
    /// </summary>
    public static readonly string[] FictionalMobiles =
    [
        "+61491570006", "+61491570156", "+61491570157", "+61491570158", "+61491570159", "+61491570110", "+61491570313",
        "+61491570737", "+61491571266", "+61491571491", "+61491571804", "+61491572549", "+61491572665", "+61491572983",
        "+61491573770", "+61491573087", "+61491574118", "+61491574632", "+61491575254", "+61491575789", "+61491576398",
    ];

    /// <summary>The ACMA's fictional landline ranges: (0x) 5550 xxxx and (0x) 7010 xxxx in each area.</summary>
    public static readonly string[] FictionalLandlinePrefixes =
    [
        "+6125550", "+6127010", "+6135550", "+6137010", "+6175550", "+6177010", "+6185550", "+6187010",
    ];

    /// <summary>Extensions kept on a removed file's name. Anything else after a dot may be part of a name.</summary>
    public static readonly string[] FileExtensions =
    [
        "pdf", "doc", "docx", "xls", "xlsx", "csv", "txt", "rtf", "odt", "ods", "ppt", "pptx", "pages",
        "jpg", "jpeg", "png", "gif", "bmp", "tif", "tiff", "heic", "heif", "webp", "svg",
        "mp4", "mov", "m4a", "mp3", "wav", "zip",
    ];

    private const string Alphanumeric = "abcdefghijklmnopqrstuvwxyz0123456789";

    public static byte[] Bytes(string seed) => SHA256.HashData(Encoding.UTF8.GetBytes(seed));

    public static bool IsDigits(string value) => value.All(character => character is >= '0' and <= '9');

    public static string Pick(string[] items, string seed)
    {
        var bytes = Bytes(seed);
        return items[((bytes[0] * 65536) + (bytes[1] * 256) + bytes[2]) % items.Length];
    }

    /// <summary>A fake first and last name, the same as the first_name and last_name fields with this seed would get.</summary>
    public static string FullName(string seed) =>
        Pick(Names.First, seed + "|first_name") + " " + Pick(Names.Last, seed + "|last_name");

    public static string Phone(string seed, bool mobile)
    {
        var bytes = Bytes(seed);
        return mobile
            ? FictionalMobiles[bytes[0] % FictionalMobiles.Length]
            : FictionalLandlinePrefixes[bytes[0] % FictionalLandlinePrefixes.Length] + DigitString(bytes, 1, 4);
    }

    public static bool IsFictionalPhone(string value, bool mobile) =>
        mobile
            ? FictionalMobiles.Contains(value)
            : value.Length == 12 && FictionalLandlinePrefixes.Contains(value[..8]) && IsDigits(value[8..]);

    public static string Email(string seed)
    {
        var bytes = Bytes(seed);
        var name = new string(bytes.Take(20).Select(value => Alphanumeric[value % 36]).ToArray());
        return name + "@" + EmailDomains[bytes[20] % 3];
    }

    /// <summary>Random digits of the same length as the original.</summary>
    public static string Digits(string original, string seed) => DigitString(Bytes(seed), 0, original.Length);

    /// <summary>
    /// The real sid is built from the date of birth, entry grade, entry year,
    /// surname and first initial. This rebuilds it from the record's own kept
    /// fields and its fake names, and never reads the original sid. The
    /// trailing number keeps it unique, like the counter EnrolHQ adds to
    /// duplicates.
    /// </summary>
    public static string Sid(Func<string, string> field, string seed)
    {
        var first = Pick(Names.First, seed + "|first_name");
        var last = Pick(Names.Last, seed + "|last_name");
        var bytes = Bytes(seed + "|sid");
        var counter = (1 + (((bytes[0] * 256) + bytes[1]) % 999)).ToString();
        var kept = new[] { field("dob").Replace("-", ""), field("entry_grade"), field("entry_year") }
            .Where(part => part.Length > 0);
        return string.Join("-", kept.Concat([last.ToUpperInvariant(), first[..1], counter]));
    }

    /// <summary>".pdf" from a file key, file name or URL; "" unless it is a known file extension.</summary>
    public static string Extension(string value)
    {
        var path = value.Split('?', '#')[0];
        var name = path.Split('/')[^1];
        var extension = name.Contains('.') ? name.Split('.')[^1] : "";
        return FileExtensions.Contains(extension.ToLowerInvariant()) ? "." + extension : "";
    }

    private static string DigitString(byte[] bytes, int offset, int length) =>
        new(Enumerable.Range(offset, length).Select(position => (char)('0' + (bytes[position % bytes.Length] % 10))).ToArray());
}
