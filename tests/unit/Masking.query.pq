// Checks the anonymisation rules against synthetic data. Returns one row per
// check; test-masking.ps1 fails if any row has Passed = false.
let
    studentId = "3f2b8c1e-1111-4222-8333-444455556666",
    parentId = "aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee",
    parent = [
        id = parentId,
        title = "Dr",
        first_name = "Parentfirst",
        last_name = "Parentlast",
        middle_name = "Parentmiddle",
        email = "parent@real-family.com",
        mobile_phone = "+61411222333",
        business_phone = "0398765432",
        health_care_card_number = "123456789A",
        occupation = "Engineer"
    ],
    application = [
        id = studentId,
        title = "Miss",
        first_name = "Realfirst",
        last_name = "Reallast",
        middle_name = "Realmiddle",
        preferred_name = "Realpreferred",
        traditional_name = "",
        dob = "2015-03-04",
        gender = 2,
        entry_year = 2027,
        application_status = 3,
        email = "student@real-family.com",
        mobile_phone = "+61400111222",
        home_phone = "0298765432",
        sid = "20150304-7-2027-REALLAST-R",
        entry_grade = 7,
        usi = "ABC1234567",
        vsn = "",
        nesa_number = null,
        custody_details = "Sensitive custody text",
        cultural_requirements = null,
        avatar = "https://bucket.s3.amazonaws.com/school/avatars/photo.JPG?X-Amz-Signature=abc",
        user_parent = parent,
        non_user_parent = null,
        residential_address = [
            street_address = "1 Real Street",
            apartment = "",
            suburb = "Kew",
            state = "VIC",
            postcode = "3101"
        ],
        guardians = {
            [
                id = 12,
                first_name = "Guardianfirst",
                last_name = "Guardianlast",
                middle_name = "Guardianmiddle",
                email = "",
                postal_address = "PO Box 1, Kew",
                working_with_children_number = "1234567A"
            ]
        },
        emergency_contacts = {
            [id = 12, first_name = "Contactfirst", last_name = "Contactlast", mobile_phone = "0400000000"]
        },
        medical_data = [
            medicare_number = "2123456701",
            medication = "Ventolin",
            symptoms_treatment = "",
            doctor = [name = "Jane Realdoctor", telephone = "0398765432", email = "doctor@clinic.com"]
        ],
        campus = [id = 1, name = "Kew Campus", email = "kew@school.edu.au"],
        custom_form_submits = {[id = "s1", form = "f1", payload = [answer = "Real answer"], submission_ip = "1.2.3.4"]},
        documents = {[id = 5, file = "school/docs/report.pdf", filename = "Jane Report.pdf", kind = "SCHOOL_REPORT"]},
        siblings = {[first_name = "Siblingfirst", last_name = "Reallast"], [first_name = "Othersibling", last_name = "Reallast"]}
    ],
    sibling = application & [id = "99999999-1111-4222-8333-444455556666", first_name = "Secondchild"],

    list = EnrolHQ.Anonymise({application, sibling}, "applications"),
    masked = list{0},
    maskedSibling = list{1},
    detail = EnrolHQ.Anonymise({application}, "application_details"){0},
    campuses = EnrolHQ.Anonymise({[id = 1, name = "Kew Campus", email = "kew@school.edu.au", principal_signature = "sigs/principal.png"]}, "campuses"){0},

    // Every real value in the fixture that must not survive masking.
    secrets = {
        "Realfirst", "Reallast", "Realmiddle", "Realpreferred", "Parentfirst", "Parentlast", "Parentmiddle",
        "real-family.com", "61411222333", "0398765432", "61400111222", "0298765432", "ABC1234567", "123456789A",
        "Sensitive custody text", "photo.JPG", "X-Amz-Signature", "1 Real Street", "Guardianfirst", "Guardianlast",
        "Guardianmiddle", "PO Box 1", "1234567A", "Contactfirst", "Contactlast", "0400000000", "2123456701",
        "Ventolin", "Realdoctor", "clinic.com", "Real answer", "1.2.3.4", "report.pdf", "Jane Report",
        "Siblingfirst", "Othersibling", "Secondchild", "REALLAST"
    },
    maskedJson = Text.FromBinary(Json.FromValue(list & {detail})),
    leaked = List.Select(secrets, (secret) => Text.Contains(maskedJson, secret)),

    endsWithExample = (value as text) as logical =>
        Text.EndsWith(value, "@example.com") or Text.EndsWith(value, "@example.net") or Text.EndsWith(value, "@example.org"),
    isDigits = (value as text) as logical => Text.Select(value, {"0".."9"}) = value,
    wrongType = try EnrolHQ.Anonymise({[id = 1, payload = 5]}, "form_submissions"),

    checks = {
        {"No real value survives anywhere in the output", List.IsEmpty(leaked)},

        {"Record id is kept", masked[id] = studentId},
        {"Date of birth is kept", masked[dob] = "2015-03-04"},
        {"Parent, sibling and alumnus dates of birth are kept", EnrolHQ.Anonymise({[id = parentId, first_name = "Parentfirst", dob = "1980-01-02", alumnus = [full_name = "Parentfirst Parentlast", dob = "1980-01-02"], siblings = {[first_name = "Siblingfirst", dob = "2012-05-06"]}]}, "application_details"){0}[[dob], [alumnus], [siblings]] = [dob = "1980-01-02", alumnus = [full_name = EnrolHQ.Anonymise({[id = parentId, first_name = "Parentfirst", alumnus = [full_name = "Parentfirst Parentlast"]]}, "application_details"){0}[alumnus][full_name], dob = "1980-01-02"], siblings = {[first_name = EnrolHQ.Anonymise({[id = parentId, first_name = "Parentfirst", siblings = {[first_name = "Siblingfirst"]}]}, "application_details"){0}[siblings]{0}[first_name], dob = "2012-05-06"]}]},
        {"external_id and student_code are kept", EnrolHQ.Anonymise({[id = studentId, external_id = "S1234", student_code = "SCH-0042", user_parent = [id = parentId, external_id = "P5678"]]}, "applications"){0} = [id = studentId, external_id = "S1234", student_code = "SCH-0042", user_parent = [id = parentId, external_id = "P5678"]]},
        {"Gender, entry year and status are kept", masked[gender] = 2 and masked[entry_year] = 2027 and masked[application_status] = 3},
        {"Suburb, state and postcode are kept", masked[residential_address][suburb] = "Kew" and masked[residential_address][state] = "VIC" and masked[residential_address][postcode] = "3101"},
        {"Parent occupation is kept", masked[user_parent][occupation] = "Engineer"},
        {"Parent employer is redacted", EnrolHQ.Anonymise({[id = parentId, first_name = "Parentfirst", last_name = "Parentlast", employer = "Parentlast Plumbing"]}, "leads"){0}[employer] = "Redacted"},
        {"Document kind is kept", masked[documents]{0}[kind] = "SCHOOL_REPORT"},

        {"Student title is blanked", masked[title] = ""},
        {"Parent title is kept", masked[user_parent][title] = "Dr"},
        {"Middle, preferred and traditional names are blanked", masked[middle_name] = "" and masked[preferred_name] = "" and masked[traditional_name] = ""},
        {"Parent and guardian middle names are blanked", masked[user_parent][middle_name] = "" and masked[guardians]{0}[middle_name] = ""},

        {"Email is a random example address", endsWithExample(masked[email]) and Text.Length(Text.BeforeDelimiter(masked[email], "@")) = 20},
        {"Mobile is a random Australian mobile", Text.StartsWith(masked[mobile_phone], "+6143") and Text.Length(masked[mobile_phone]) = 12},
        {"Home phone is a random Australian landline", List.Contains({"+612", "+613", "+617", "+618"}, Text.Start(masked[home_phone], 4)) and Text.Length(masked[home_phone]) = 12},
        {"Identifier becomes digits of the same length", isDigits(masked[usi]) and Text.Length(masked[usi]) = 10},
        {"Guardian identifier becomes digits of the same length", isDigits(masked[guardians]{0}[working_with_children_number]) and Text.Length(masked[guardians]{0}[working_with_children_number]) = 8},
        {"Medicare number becomes digits of the same length", isDigits(masked[medical_data][medicare_number]) and Text.Length(masked[medical_data][medicare_number]) = 10},

        {"Free text is redacted", masked[custody_details] = "Redacted" and masked[medical_data][medication] = "Redacted" and masked[guardians]{0}[postal_address] = "Redacted"},
        {"Empty stays empty", masked[vsn] = "" and masked[guardians]{0}[email] = "" and masked[medical_data][symptoms_treatment] = "" and masked[residential_address][apartment] = ""},
        {"Null stays null", masked[nesa_number] = null and masked[cultural_requirements] = null and masked[non_user_parent] = null},

        {"Street address is replaced", masked[residential_address][street_address] = "Fake Street, 42"},
        {"Apartment is replaced when present", EnrolHQ.Anonymise({[apartment = "Unit 9"]}, "leads"){0}[apartment] = "F101"},
        {"File link keeps only its extension", masked[avatar] = "anonymized/removed-file.JPG"},
        {"Document file and name are removed", masked[documents]{0}[file] = "anonymized/removed-file.pdf" and masked[documents]{0}[filename] = "removed-file.pdf"},
        {"File with no extension", EnrolHQ.Anonymise({[file = "https://host/path/download?id=1.2"]}, "leads"){0}[file] = "anonymized/removed-file"},
        {"Form payload is emptied", Record.FieldCount(masked[custom_form_submits]{0}[payload]) = 0},
        {"Text payload is emptied", EnrolHQ.Anonymise({[payload = "{""a"":1}"]}, "form_submissions"){0}[payload] = "{}"},
        {"IP address is removed", masked[custom_form_submits]{0}[submission_ip] = null},
        {"Form submit ids are kept", masked[custom_form_submits]{0}[id] = "s1" and masked[custom_form_submits]{0}[form] = "f1"},

        {"Doctor name, phone and email are masked", List.Count(Text.Split(masked[medical_data][doctor][name], " ")) = 2 and Text.StartsWith(masked[medical_data][doctor][telephone], "+61") and endsWithExample(masked[medical_data][doctor][email])},
        {"Campus name and email are kept", masked[campus][name] = "Kew Campus" and masked[campus][email] = "kew@school.edu.au" and campuses[email] = "kew@school.edu.au"},
        {"Campus signature file is removed", campuses[principal_signature] = "anonymized/removed-file.png"},

        {"Event booking free-text answers are redacted", EnrolHQ.Anonymise({[id = 1, custom_field_3 = "Allergic to nuts", booking_status = "confirmed"]}, "event_bookings"){0} = [id = 1, custom_field_3 = "Redacted", booking_status = "confirmed"]},
        {"PDF references are removed", EnrolHQ.Anonymise({[form_pdf = "0f0e0d0c-1111-4222-8333-444455556666", student_profile_event_bookings = {[event_booking_pdf = "0f0e0d0c-9999-4222-8333-444455556666"]}]}, "form_submissions"){0} = [form_pdf = "anonymized/removed-file", student_profile_event_bookings = {[event_booking_pdf = "anonymized/removed-file"]}]},
        {"Branding image links are removed", EnrolHQ.Anonymise({[cms_images = [favicon = "https://cdn/school/favicon.png", logo_dark = null]]}, "campuses"){0}[cms_images] = [favicon = "anonymized/removed-file.png", logo_dark = null]},
        {"Agreement document links are removed, labels kept", EnrolHQ.Anonymise({[agreement_documents = {[label = "Fee schedule", link = "https://cdn/school/fees.pdf"]}]}, "applications"){0}[agreement_documents]{0} = [label = "Fee schedule", link = "anonymized/removed-file.pdf"]},

        {"sid is rebuilt from fake names and never contains the real surname",
            let
                row = EnrolHQ.Anonymise({[id = studentId, first_name = "Realfirst", last_name = "Reallast", dob = "2015-03-04", entry_grade = 7, entry_year = 2027, sid = "20150304-7-2027-REALLAST-R"]}, "applications"){0}
            in
                Text.StartsWith(row[sid], "20150304-7-2027-" & Text.Upper(row[last_name]) & "-" & Text.Start(row[first_name], 1) & "-")
                and not Text.Contains(row[sid], "REALLAST")},
        {"sid in an unexpected format still loses the real surname",
            not Text.Contains(EnrolHQ.Anonymise({[id = studentId, first_name = "Realfirst", last_name = "Reallast", sid = "REALLAST-R"]}, "applications"){0}[sid], "REALLAST")},
        {"Payment reference is redacted", EnrolHQ.Anonymise({[payments = {[id = 3, reference_id = "enr-f:100:20150304-7-2027-REALLAST-R", amount = 250]}]}, "application_details"){0}[payments]{0} = [id = 3, reference_id = "Redacted", amount = 250]},
        {"Payment transaction number is redacted, receipt number and amount kept", EnrolHQ.Anonymise({[enrolment_acceptance_payment = [id = 4, transaction_number = "Parentfirst Parentlast 0042", receipt_number = "R-1001", amount = 500]]}, "application_details"){0}[enrolment_acceptance_payment] = [id = 4, transaction_number = "Redacted", receipt_number = "R-1001", amount = 500]},
        {"Free-text 'other' answers are redacted, the chosen option is kept", EnrolHQ.Anonymise({[religion = "Other", religion_other = "Same as Grandma Reallast", user_parent = [id = parentId, how_hear = {"Friend"}, how_hear_other = "Parentfirst's colleague"]]}, "application_details"){0} = [religion = "Other", religion_other = "Redacted", user_parent = [id = parentId, how_hear = {"Friend"}, how_hear_other = "Redacted"]]},
        {"Parents salutation and payer names are redacted", EnrolHQ.Anonymise({[parents_salutation = "Mr and Mrs Reallast", custom_form_payer_names = [#"enr-f" = "Parentfirst Parentlast"]]}, "application_details"){0} = [parents_salutation = "Redacted", custom_form_payer_names = [#"enr-f" = "Redacted"]]},
        {"Questionnaire keeps its keys and clears its values", EnrolHQ.Anonymise({[questionnaire = [hobbies = "Chess with Grandpa Reallast", siblings_here = {"yes"}]]}, "application_details"){0}[questionnaire] = [hobbies = null, siblings_here = null]},
        {"Nested event booking answers are redacted", EnrolHQ.Anonymise({[custom_field_1 = [confirm_flag = true, text = "Ask for Parentfirst"], custom_field_2 = {"Option A"}]}, "event_bookings"){0} = [custom_field_1 = [confirm_flag = true, text = "Redacted"], custom_field_2 = {"Redacted"}]},
        {"Signed acceptance links are removed", EnrolHQ.Anonymise({[enrolment_accept_link = "https://enrol/accept?token=abc", progress = [enrolment_offer = [accept_link = "https://enrol/offer?token=def", kind = "enrolment"]]]}, "application_details"){0} = [enrolment_accept_link = null, progress = [enrolment_offer = [accept_link = null, kind = "enrolment"]]]},
        {"Dentist is masked like the doctor", Text.StartsWith(EnrolHQ.Anonymise({[medical_data = [dentist = [name = "John Realdentist", telephone = "0398765432"]]]}, "application_details"){0}[medical_data][dentist][telephone], "+61")},

        {"Same record gives the same fake values in every table", masked[first_name] = detail[first_name] and masked[last_name] = detail[last_name] and masked[email] = detail[email] and masked[mobile_phone] = detail[mobile_phone]},
        {"Same parent gives the same fake values on each child", masked[user_parent] = maskedSibling[user_parent]},
        {"Different records give different fake values", masked[email] <> maskedSibling[email] and masked[email] <> masked[user_parent][email]},
        {"Records without an id still differ from each other", masked[siblings]{0}[first_name] <> masked[siblings]{1}[first_name] or masked[siblings]{0}[last_name] <> masked[siblings]{1}[last_name]},
        {"Same numeric id on different record kinds gives different values", masked[guardians]{0}[first_name] <> masked[emergency_contacts]{0}[first_name] or masked[guardians]{0}[last_name] <> masked[emergency_contacts]{0}[last_name]},

        {"A fake name is never the original", List.MatchesAll(
            {"Adam", "Felix", "Zoe", "Oliver", "Mia"},
            (name) => EnrolHQ.Anonymise({[id = studentId, first_name = name]}, "applications"){0}[first_name] <> name
        )},
        {"Masked digits are never the original", List.MatchesAll(
            {"0", "5", "12", "987", "2123456701"},
            (digits) => EnrolHQ.Anonymise({[id = studentId, usi = digits]}, "applications"){0}[usi] <> digits
        )},

        {"Masking a non-text value still produces a masked value", not wrongType[HasError] and wrongType[Value]{0}[payload] = "{}"}
    },
    result = #table(type table [Check = text, Passed = logical], checks)
in
    result
