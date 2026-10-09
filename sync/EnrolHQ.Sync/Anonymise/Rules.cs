namespace EnrolHQ.Sync.Anonymise;

internal enum Rule
{
    FirstName,
    LastName,
    FullName,
    Sid,
    Email,
    MobilePhone,
    HomePhone,
    BusinessPhone,
    Digits,
    Redact,
    RedactAll,
    FileKey,
    FileName,
    Street,
    Apartment,
    Blank,
    Null,
    EmptyJson,
    EmptyValues,
}

/// <summary>
/// Which masking rule applies to which JSON key. Rules are per JSON key,
/// because that is what the API returns.
///
/// Kept on purpose: dates of birth, external_id and student_code (schools need
/// them for reporting and to match their own systems), and suburb, state and
/// postcode.
///
/// Must stay the same as the Anon.* rules in connector/EnrolHQ.pq. The parity
/// test compares the two.
/// </summary>
internal static class Rules
{
    /// <summary>
    /// Raise this whenever a rule is added or changed. A local copy remembers
    /// the version it was masked under, and one masked under an older version
    /// is downloaded again in full, because it still holds whatever the older
    /// rules left unmasked.
    /// </summary>
    public const int Version = 5;

    /// <summary>Applied wherever the key appears.</summary>
    private static readonly Dictionary<string, Rule> ByKey = new(StringComparer.Ordinal)
    {
        // Names
        ["first_name"] = Rule.FirstName,
        ["last_name"] = Rule.LastName,
        ["snapshot_first_name"] = Rule.FirstName,
        ["snapshot_last_name"] = Rule.LastName,
        ["full_name"] = Rule.FullName,
        ["case_manager_name"] = Rule.FullName,
        ["parish_priest_name"] = Rule.FullName,
        ["student_profile_name"] = Rule.FullName,
        ["parent_name"] = Rule.FullName,
        // Staff named on a campus
        ["principal_name"] = Rule.FullName,
        ["registrar_name"] = Rule.FullName,
        // Built from the real surname and first initial
        ["sid"] = Rule.Sid,
        // Contact
        ["email"] = Rule.Email,
        // Email log: each recipient, cc and bcc is a record with the address and whether it was opened
        ["address"] = Rule.Email,
        ["mobile_phone"] = Rule.MobilePhone,
        ["contact_phone"] = Rule.MobilePhone,
        ["home_phone"] = Rule.HomePhone,
        ["business_phone"] = Rule.BusinessPhone,
        // Government, health and other identifiers
        ["usi"] = Rule.Digits,
        ["vsn"] = Rule.Digits,
        ["nesa_number"] = Rule.Digits,
        ["medicare_number"] = Rule.Digits,
        ["medicare_card_reference_number"] = Rule.Digits,
        ["health_fund_member_number"] = Rule.Digits,
        ["ambulance_fund_number"] = Rule.Digits,
        ["health_care_card_number"] = Rule.Digits,
        ["passport_number"] = Rule.Digits,
        ["working_with_children_number"] = Rule.Digits,
        // Free text
        ["custody_details"] = Rule.Redact,
        ["cultural_requirements"] = Rule.Redact,
        ["symptoms_treatment"] = Rule.Redact,
        ["medication"] = Rule.Redact,
        ["other_label"] = Rule.Redact,
        ["enrichments_details"] = Rule.Redact,
        ["music_details"] = Rule.Redact,
        ["sport_details"] = Rule.Redact,
        ["missed_periods"] = Rule.Redact,
        ["special_needs_other"] = Rule.Redact,
        ["current_school_provides_other"] = Rule.Redact,
        ["current_school_provides_required_other"] = Rule.Redact,
        ["professional_support_other"] = Rule.Redact,
        ["undiagnosed_learning_difficulties"] = Rule.Redact,
        ["dietary_requirements_other"] = Rule.Redact,
        ["custom_block_1_details"] = Rule.Redact,
        ["custom_block_2_details"] = Rule.Redact,
        ["custom_block_3_details"] = Rule.Redact,
        ["current_school_history"] = Rule.Redact,
        ["postal_address"] = Rule.Redact,
        ["form_note"] = Rule.Redact,
        ["comment"] = Rule.Redact,
        // Document groups sit under a different parent key for each kind of document.
        ["note"] = Rule.Redact,
        ["medical_conditions_other"] = Rule.Redact,
        // Free text and computed values that normally contain a person's name
        ["parents_salutation"] = Rule.Redact,
        ["correspondence_addressee_other"] = Rule.Redact,
        ["student_resides_with_other"] = Rule.Redact,
        ["how_hear_personal_referral"] = Rule.Redact,
        // Often a family business named after the parent
        ["employer"] = Rule.Redact,
        ["updated_by"] = Rule.Redact,
        ["deleted_by"] = Rule.Redact,
        // Written by staff for families joining an interview: links, meeting
        // ids and staff contact details
        ["meeting_instructions"] = Rule.Redact,
        ["custom_form_payer_names"] = Rule.RedactAll,
        // Payment references embed the sid, and for some gateways the payer's
        // name, email or date of birth
        ["reference_id"] = Rule.Redact,
        ["transaction_number"] = Rule.Redact,
        ["result_text"] = Rule.Redact,
        // File references
        ["avatar"] = Rule.FileKey,
        ["avatar_src"] = Rule.FileKey,
        ["image_src"] = Rule.FileKey,
        ["attachment_src"] = Rule.FileKey,
        ["principal_signature_src"] = Rule.FileKey,
        ["logo_image_src"] = Rule.FileKey,
        ["header_image_src"] = Rule.FileKey,
        ["enrolment_review_scan_pdf"] = Rule.FileKey,
        ["file"] = Rule.FileKey,
        ["image"] = Rule.FileKey,
        ["attachment"] = Rule.FileKey,
        ["principal_signature"] = Rule.FileKey,
        ["export_file"] = Rule.FileKey,
        ["header_image"] = Rule.FileKey,
        ["logo_image"] = Rule.FileKey,
        ["hint_image"] = Rule.FileKey,
        ["form_pdf"] = Rule.FileKey,
        ["event_booking_pdf"] = Rule.FileKey,
        ["filename"] = Rule.FileName,
        ["attachment_file_names"] = Rule.FileName,
        // Email log: what staff wrote about the email
        ["staff_description"] = Rule.Redact,
        // Addresses: suburb, state and postcode are kept for statistics
        ["street_address"] = Rule.Street,
        ["apartment"] = Rule.Apartment,
        // IP addresses
        ["submission_ip"] = Rule.Null,
        ["ip_addr"] = Rule.Null,
        ["user_parent_acceptance_ip"] = Rule.Null,
        ["non_user_parent_acceptance_ip"] = Rule.Null,
        // Signed links that let the holder accept an offer or sign a form
        ["enrolment_accept_link"] = Rule.Null,
        ["gpa_accept_link"] = Rule.Null,
        ["accept_link"] = Rule.Null,
        // A link that lets the holder join a meeting, and the signed token
        // that lets the holder book into an event from its kiosk page
        ["online_meeting_link"] = Rule.Null,
        ["public_token"] = Rule.Null,
        // Free-form JSON
        ["payload"] = Rule.EmptyJson,
        ["initial_payload"] = Rule.EmptyJson,
        ["token_request_data"] = Rule.EmptyJson,
        ["request_data"] = Rule.EmptyJson,
        // Questionnaire answers: keys kept, values cleared
        ["questionnaire"] = Rule.EmptyValues,
        ["preschool_questionnaire"] = Rule.EmptyValues,
        ["abilities_questionnaire"] = Rule.EmptyValues,
        ["event_booking_questionnaire"] = Rule.EmptyValues,
    };

    /// <summary>Applied only on records that have a first or last name.</summary>
    private static readonly Dictionary<string, Rule> ByKeyOnPeople = new(StringComparer.Ordinal)
    {
        ["middle_name"] = Rule.Blank,
        ["preferred_name"] = Rule.Blank,
        ["traditional_name"] = Rule.Blank,
    };

    /// <summary>
    /// Keys whose meaning depends on where they sit, as "parent.key". For
    /// top-level fields the parent is the table name.
    /// </summary>
    private static readonly Dictionary<string, Rule> ByContext = new(StringComparer.Ordinal)
    {
        ["applications.title"] = Rule.Blank,
        ["application_details.title"] = Rule.Blank,
        ["student_profile.title"] = Rule.Blank,
        ["student.title"] = Rule.Blank,
        ["doctor.name"] = Rule.FullName,
        ["doctors.name"] = Rule.FullName,
        ["doctor.telephone"] = Rule.BusinessPhone,
        ["doctors.telephone"] = Rule.BusinessPhone,
        ["dentist.name"] = Rule.FullName,
        ["dentist.telephone"] = Rule.BusinessPhone,
        ["signature.name"] = Rule.FullName,
        ["signatures.name"] = Rule.FullName,
        ["notes.text"] = Rule.Redact,
        ["activity_log.description"] = Rule.Redact,
        ["interview.notes"] = Rule.Redact,
        ["interviews.notes"] = Rule.Redact,
        ["document_group.note"] = Rule.Redact,
        ["document_groups.note"] = Rule.Redact,
        ["agreement_documents.link"] = Rule.FileKey,
        // Written by staff for families coming to an interview
        ["location.hint_text"] = Rule.Redact,
        ["email_log.subject"] = Rule.Redact,
    };

    /// <summary>Not personal: a campus's own contact address.</summary>
    private static readonly HashSet<string> Keep = new(StringComparer.Ordinal) { "campuses.email", "campus.email" };

    public static Rule? For(string parent, string key, bool isPerson)
    {
        var context = parent + "." + key;
        if (Keep.Contains(context))
        {
            return null;
        }

        if (ByContext.TryGetValue(context, out var byContext))
        {
            return byContext;
        }

        if (isPerson && ByKeyOnPeople.TryGetValue(key, out var onPeople))
        {
            return onPeople;
        }

        if (ByKey.TryGetValue(key, out var byKey))
        {
            return byKey;
        }

        // Every entry under cms_images is a file link.
        if (parent == "cms_images")
        {
            return Rule.FileKey;
        }

        // custom_field_1, custom_field_2, ... hold answers as text, a list or a nested object.
        if (key.StartsWith("custom_field_", StringComparison.Ordinal))
        {
            return Rule.RedactAll;
        }

        // "How did you hear about us" is reporting data, so the answer typed
        // under Other is kept. The safety net still redacts one that names the family.
        if (key == "how_hear_other")
        {
            return null;
        }

        // religion_other, student_resides_with_other, ... are typed in by parents, and on
        // live data some contain the family's own names.
        if (key.EndsWith("_other", StringComparison.Ordinal))
        {
            return Rule.Redact;
        }

        return null;
    }
}
