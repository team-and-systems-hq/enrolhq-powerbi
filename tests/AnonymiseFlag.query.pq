// Anything other than Yes, No or blank must be refused, never treated as No.
let
    rejects = (value as text) as logical => (try EnrolHQ.Contents("{{instance}}", null, value))[HasError],
    accepts = (value as nullable text) as logical => not (try EnrolHQ.Contents("{{instance}}", null, value))[HasError],
    ok = rejects("Y") and rejects("true") and rejects("yes please") and accepts(" yes ") and accepts(null),
    result = if ok then #table({"Check"}, {{"Anonymise flag accepts only Yes, No or blank"}}) else error "Anonymise flag accepted an unclear value."
in
    result

