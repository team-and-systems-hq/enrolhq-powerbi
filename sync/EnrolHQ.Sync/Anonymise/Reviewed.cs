namespace EnrolHQ.Sync.Anonymise;

/// <summary>
/// Fields with no masking rule whose text has been looked at and is kept:
/// choices from a list, places, codes and the values listed under "Kept on
/// purpose" in the README. Text in any other field without a rule is redacted
/// and reported, because nobody has checked what it holds.
///
/// Entries are "parent.key", with the parent written the way the rules write
/// it: the table name for top-level fields, otherwise the key the field sits
/// under. "*.key" allows the key under any parent.
///
/// Dates, times and ids need no entry. To keep a field that a sync reports as
/// not reviewed, look at what it holds and, if it can never carry a name,
/// contact detail or free text about a person, add it here with a test.
/// </summary>
internal static class Reviewed
{
    private static readonly HashSet<string> Keys = new(StringComparer.Ordinal)
    {
        // Choices, kinds and statuses
        "*.kind", "*.payment_kind", "*.title", "*.slug", "*.form_slug", "*.connection_type", "*.house",
        "*.status_label", "*.default_status_label", "*.booking_status", "*.lead_status", "*.login_requirement",
        "*.latest_scheduled_sync_status", "*.first_parent_field", "*.correspondence_addressee", "*.how_hear",
        "*.relationship_to_student", "*.religion", "*.educational_institution_type", "*.visa_subclass",
        "*.HH", "*.mm",
        // Places and codes
        "*.suburb", "*.state", "*.postcode", "*.city", "*.alpha_2_code", "*.alpha_3_code",
        "born_country.name", "country.name", "nationalities.name", "home_language.name", "current_school.name",
        "*.what_school", "application_details.current_school_preschool",
        // Ids from the school's other systems, kept so records can be matched
        "*.external_id", "*.student_code", "*.alumni_number", "*.receipt_number",
        // Money
        "*.amount", "*.gst", "*.surcharge_amount", "*.summary",
        // Kept on purpose: occupation, interests, medical condition names
        "*.occupation", "*.current_occupation", "*.interests", "medical_data.medical_conditions",
        "abilities.current_school_provides", "abilities.current_school_provides_required", "abilities.professional_support",
        "abilities.special_needs",
        // A business, not a person
        "agent_details.company",
        // The school's own names for things
        "agreement_documents.label", "attendance_types.name", "campuses.name", "campus.name", "campuses.email", "campus.email",
        "campuses.from_email", "campuses.telephone", "campuses.website", "campuses.principal_title", "campuses.registrar_title",
        "events.name", "events.description", "events.location", "sessions.name", "location.name", "forms.title",
        "lead_references.name",
        // Leads
        "leads.reference", "student.questions",
        // Ids written as short text
        "*.id", "*.form",
    };

    /// <param name="path">The field's full path, for example applications.user_parent.residential_address.suburb.</param>
    public static bool Allows(string path)
    {
        var parts = path.Replace("[]", "", StringComparison.Ordinal).Split('.');
        var key = parts[^1];
        var parent = parts.Length > 1 ? parts[^2] : "";
        return Keys.Contains(parent + "." + key)
            || Keys.Contains("*." + key)
            // Links to other records, such as campus_id
            || key.EndsWith("_id", StringComparison.Ordinal);
    }
}
