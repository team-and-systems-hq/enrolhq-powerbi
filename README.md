# EnrolHQ for Power BI

Keeps a local copy of a school's EnrolHQ data and opens it in Power BI.

```
EnrolHQ API  ->  enrolhq-sync  ->  local copy (SQLite + Parquet)  ->  Power BI project
```

Power BI never calls the EnrolHQ API. `enrolhq-sync` does the downloading, one request at a time and within the API's limits, and Power BI reads the files it writes.

## Quick start

Needs 64-bit Windows and Power BI Desktop.

1. Download `enrolhq-sync.exe` from the [latest release](https://github.com/team-and-systems-hq/enrolhq-powerbi/releases/latest) into a folder of its own. Each release is built by GitHub from the tagged source, and comes with a `.sha256` file to check the download against.
2. Run it from that folder:
   ```
   enrolhq-sync.exe
   ```
   The first time, it asks for the school's EnrolHQ address, the API token (an EnrolHQ administrator makes one under their profile) and whether to anonymise, and saves the answers to a `.env` file beside it that only your Windows account can read. After that it just runs. To set the file up by hand instead, see [`.env.example`](.env.example).
3. Wait for the download to finish. It shows its progress.
4. Open `data\<school>\anonymised\powerbi\EnrolHQ.pbip` in Power BI Desktop and click **Refresh**.

The first download takes 30 to 60 minutes for a school with about 10,000 applications; nearly all of that is the full application records. Later runs fetch only the applications that changed.

Windows SmartScreen may warn the first time the program runs, because it is not yet signed with a code-signing certificate. Choose **More info** and **Run anyway**. To check the download first, compare its hash with the one in the `.sha256` file, in PowerShell:
```
(Get-FileHash enrolhq-sync.exe).Hash
```

To build it yourself instead, see [Development](#development). For a second school, see [Adding another school](#adding-another-school).

## Settings

| Setting | Value |
| --- | --- |
| `ENROLHQ_INSTANCE` | The school's EnrolHQ address, for example `enrol.yourschool.edu.au` |
| `ENROLHQ_API_TOKEN` | The school's API token |
| `ENROLHQ_ANONYMISE` | `yes` (default) or `no`. Anything else is refused |
| `ENROLHQ_DATA_DIR` | Where to keep the local copy. Default: `data` beside the `.env` file |

Each setting can also be given as an environment variable of the same name. An environment variable wins over the `.env` file. When the environment sets both `ENROLHQ_INSTANCE` and `ENROLHQ_API_TOKEN`, no `.env` is read unless one is named with `--env`, and the local copy goes in a `data` folder in the current folder. Each run says where its settings came from.

## Adding another school

Each school has its own settings file and its own local copy, so adding one never touches another.

1. Get an API token from that school. A token only works for the school whose EnrolHQ issued it.
2. Create a settings file for the school beside `.env`, named after it, for example `.env.secondschool`:
   ```
   ENROLHQ_INSTANCE=enrol.secondschool.edu.au
   ENROLHQ_API_TOKEN=that-school's-token
   ENROLHQ_ANONYMISE=yes
   ```
   Limit who can read it, as for `.env` (see [Security](#security)). Git ignores every `.env.*` file except `.env.example`.
3. From the folder that holds the settings files, download that school's data by naming its file:
   ```
   enrolhq-sync --env .env.secondschool
   ```
   Give the path to `enrolhq-sync.exe` if it is not in that folder. The run starts by saying which school and which settings file it is using.
4. Open that school's project, `data\enrol.secondschool.edu.au\anonymised\powerbi\EnrolHQ.pbip`, in Power BI Desktop and click **Refresh**.

Every other command takes `--env` the same way, for example `enrolhq-sync status --env .env.secondschool`. Without `--env` the tool uses `.env`.

```
data\
    enrol.firstschool.edu.au\anonymised\
    enrol.secondschool.edu.au\anonymised\
        enrolhq.db
        parquet\
        powerbi\EnrolHQ.pbip
        logs\
```

What differs from school to school:

- **The tables and columns.** Schools switch fields on and off and name their statuses differently. Each school's Power BI project is written from that school's own columns.
- **Fields the masking rules have not seen.** Their text is redacted, not let through, and the run lists the fields it happened in. See [How the masking works](#how-the-masking-works).
- **How long the first download takes.** It grows with the number of applications.

Every school's project is called `EnrolHQ`. Open one at a time in Power BI Desktop, so that anything connecting to it by name gets the school you mean.

## Security

- **Where it connects.** Only to `https://<ENROLHQ_INSTANCE>/api/v2/`. It sends nothing anywhere else and has no telemetry.
- **What it does there.** It reads. Every request is a `GET`, apart from the one `POST` that signs in.
- **The API token.** In `.env` the token is plain text, so limit who can read the file to the account that runs the tool. In Command Prompt, signed in as that account:
  ```
  icacls .env /inheritance:r /grant:r "%USERNAME%:F"
  ```
  In PowerShell, write `"${env:USERNAME}:F"` in place of `"%USERNAME%:F"`.
  To keep the token out of files altogether, leave it out of `.env` and have whatever starts the tool set the `ENROLHQ_API_TOKEN` environment variable, for example from a secrets manager. The token is not accepted as a command-line option, because other programs on the machine can see a command line and it is saved in shell history and scheduled task definitions. The token is never written to the console, the logs or the local copy.
- **The local copy.** With `ENROLHQ_ANONYMISE=no` the `data\<school>\real` folder holds real personal data; protect it as you would the school's other student records. An anonymised copy still needs care, see [Kept on purpose](#kept-on-purpose).
- **Reporting a problem.** To report a security problem privately, use **Report a vulnerability** on this repository's Security tab on GitHub.

## The sync tool

| Command | Purpose |
| --- | --- |
| `enrolhq-sync` | Download what is new, write the Parquet files, and write the Power BI project if there is none |
| `enrolhq-sync --full` | Download everything again |
| `enrolhq-sync --only events,event_bookings` | Download only these tables |
| `enrolhq-sync export` | Write the Parquet files again from the local copy, without downloading |
| `enrolhq-sync export --new-project` | Also replace the Power BI project with a fresh one. Anything added to it is lost |
| `enrolhq-sync status` | Show what the local copy holds |

What it does to stay within the API's rules:

- **One request at a time**, at least 300 ms apart. The API allows 5 a second.
- **Waits when told to.** On "too many requests" it waits for as long as the API asks, or backs off further each time if the API does not say.
- **Signs in once.** The API allows 5 sign-ins a minute, and each one cancels the access token before it.
- **Saves its place after every page.** Stop it with Ctrl+C, or lose the connection, and the next run, with or without `--full`, carries on from the same page.
- **Fetches only changes** for full application records, the one endpoint that supports it. Changes are asked for by EnrolHQ's clock, so a computer clock that runs fast does not cause changes to be missed.
- **Notices when a list changes during a download.** If records are added or removed while a table downloads, the pages move, so the table is downloaded again before anything is removed from the local copy.
- **Checks application records against the applications list** after each run. An application missing from the full records is fetched on its own, and one deleted in EnrolHQ is removed, so no periodic `--full` is needed.
- **Retries** when the connection drops, including while signing in, and stops with a plain message when the address does not exist or EnrolHQ refuses.
- **Shows progress**: `applications: 4,200 of 9,396 · page 21 of 47, about 53s left`. A log of each run is kept in `data\<school>\<mode>\logs`. Logs hold table names, counts and field names, never record data or tokens.

Two runs cannot use one local copy at once: the second stops and says so. Do not run it while anything else is signing in with the same API token, because each sign-in cancels the other's access token. If EnrolHQ refuses one table (the token may not be allowed to read it), the other tables are still downloaded and the run ends with an error code.

### Where the data goes

```
data\<school>\anonymised\        (or \real\ when ENROLHQ_ANONYMISE=no)
    enrolhq.db                   every record as JSON, and how far each download has got
    parquet\*.parquet            one file per table, for Power BI
    powerbi\EnrolHQ.pbip         the Power BI project
    logs\
```

Real and anonymised data never share a folder, and a local copy refuses to be used in the other mode.

### Tables

One table per endpoint, plus a table for each list inside a record, linked to its parent by id. For example `event_bookings` has `event_bookings_attendees` and `event_bookings_student_profiles`.

| Table | Source |
| --- | --- |
| `applications` | `applications-list/`: summary of each application |
| `application_details` and its child tables | `applications/`: the full record, with guardians, siblings, emergency contacts, payments and so on |
| `events`, `events_sessions` | `staff-events/` |
| `event_bookings` and its child tables | `staff-event-bookings/` |
| `forms`, `form_submissions` | `forms/staff/`, `forms/staff-submits/` |
| `leads`, `lead_references` | `leads/`, `lead-references/` |
| `notes` | `notes/`: notes staff wrote on an application, linked to it by `student_profile`. The text is redacted when anonymised |
| `activity_log` | `activity-log/`: communications staff logged against an application, by `activity_kind` (phone call, meeting, letter, SMS, email and so on). The description is redacted when anonymised |
| `email_log` and `email_log_recipient_list` | `email-log/`: every email EnrolHQ sent, with its kind, sender and whether each recipient opened it. Subjects are redacted when anonymised |
| `staff`, `campuses`, `attendance_types`, `application_statuses` | reference data |
| `sync_info` | When each table was last downloaded, and whether the data is anonymised |

The API does not say which application an activity log entry or an email belongs to, so those two tables cannot be joined to applications; they report what was sent and when, by kind and by staff member. Notes do carry the application.

Not downloaded: audit log, payment order lines and form answers. The API serves those one student or one form at a time.

Column types are worked out from the values, because which fields exist varies from school to school. Times are the school's local clock time; a time EnrolHQ sends in UTC is turned into the computer's local time.

## The Power BI project

`EnrolHQ.pbip` has every table, the relationships between them, and a starter set of measures. The folder it reads from is the `DataFolder` parameter, so the project still works if the local copy is moved.

It is a starting point. Once measures or report pages have been added it belongs to whoever added them, so `enrolhq-sync` never overwrites it unless run with `--new-project`. That holds even if `EnrolHQ.pbip` has been renamed or deleted: while the `EnrolHQ.SemanticModel` or `EnrolHQ.Report` folder exists, the project is left alone.

An existing project keeps loading as the data changes. Every table it has keeps a Parquet file, empty if the table has no rows, and every column it loads is still written, empty if the data no longer has it. New tables and columns are not added to an existing project; each sync lists any it is missing, and any column whose values no longer fit the type the project gives it.

Close the project in Power BI Desktop before replacing it; if it is open, the old project is left as it was. Replacing it removes the data cached inside it, so click **Refresh** after opening the new one.

The project is written in the format and compatibility level (1606) that Power BI Desktop's August 2026 release saves. An older Power BI Desktop may refuse to open it; update Power BI Desktop if so.

Power BI refuses a model in which filters can reach a table by two routes. Where two relationships would clash, the less important one is kept but switched off. A measure can still use it with `USERELATIONSHIP`.

`check-powerbi-project.ps1` checks the project with Microsoft's own Power BI tool, without opening Power BI Desktop.

## Anonymisation

With `ENROLHQ_ANONYMISE=yes`, records are masked in memory as they are downloaded. Real data is never written to disk: not to the database, the Parquet files or the logs.

The rules are in `sync\EnrolHQ.Sync\Anonymise\Rules.cs`, keyed on the field names the API returns.

| Data | Treatment |
| --- | --- |
| First, last and full names | Fake name |
| Middle, preferred and traditional names; student title | Blank |
| Emails | Random address at `example.com`, `example.net` or `example.org` |
| Phones | A number the ACMA sets aside for fiction: one of its 0491 57x xxx mobiles, or a (0x) 5550 xxxx or 7010 xxxx landline |
| USI, VSN, NESA, Medicare, passport and similar numbers | Random digits of the same length |
| Free text, and any field nobody has reviewed | `Redacted` |
| A record or number where text was expected | Every text and number in it `Redacted` |
| File links and file names | `anonymized/removed-file.<ext>` and `removed-file.<ext>`, keeping the extension only when it is a known file type |
| Street address, apartment | `Fake Street, 42`, `F101` |
| IP addresses, signed acceptance links, online meeting links, event kiosk tokens | Removed |
| Custom form payloads | Emptied |
| Questionnaire answers | Keys kept, values cleared |

Empty values stay empty. After masking, every field is checked: every masked field must look masked, and every other field must be on the reviewed list or hold no free text. If any check fails, the download stops and nothing from that page is stored.

A local copy remembers which version of the rules it was masked under, and notices if an older version of the tool has downloaded into it since. When a newer version of the tool has more rules, the next sync downloads every table again in full and then compacts the database, so nothing the older rules left unmasked stays in the local copy. Replaced and removed records are erased from the database file as they go, not left in its unused space. For a school with about 10,000 applications that sync takes 30 to 60 minutes. Until a table has been downloaded again, every command says it is out of date.

Power BI keeps its own copy of the data inside the project. After such a sync, click **Refresh** in Power BI Desktop and save the project, or the old values stay in it.

### Kept on purpose

Dates of birth, `external_id`, `student_code` and every record id are always kept: schools need dates of birth for reporting and the ids to match their own systems. How a family heard about the school is kept in full, including the answer typed under Other, because schools report on it; a typed answer that names the family is still redacted. Suburb, state, postcode, gender, religion, languages, countries, occupation, interests, current school and medical condition names are kept too, as are the school's own campus details, event names and descriptions, and form titles. Staff names on a campus (principal and registrar) are masked. `sync\EnrolHQ.Sync\Anonymise\Reviewed.cs` lists every kept field.

Date of birth with suburb, entry year and gender is often enough to identify a child within one school. `external_id` and `student_code` identify a child to anyone with access to the school's other systems. Anonymised data is safer to handle, not safe to publish.

### How the masking works

- **Fake values are stable, not random.** They are worked out from a hash of the record id and field name, so the same person has the same fake values in every table and on every sync. A record's `full_name` matches its fake first and last names, and a parent's alumnus record gets the parent's fake name. Other name fields, such as `case_manager_name` or a booking's `parent_name`, get a fake name of their own, which is not linked to the same person elsewhere.
- **A fake value never depends on the real value**, so it says nothing about it, even to someone with this code. By chance a fake name can be the same as the real one.
- **Fields nobody has reviewed are redacted.** Text in a field with no masking rule is kept only when the field is on the reviewed list in `Reviewed.cs`, or the text is a date, a time or an id. Anything else is redacted, so a field EnrolHQ adds later is never stored unmasked unnoticed. Each sync lists the fields this happened in; to keep one, check what it holds and add it to the list with a test.
- **`sid` is rebuilt.** The real `sid` contains the surname and first initial, so it is rebuilt from the fake name.
- **Fields that can carry a name are masked too**, even though they are not name fields: payment `reference_id`, `transaction_number` and `result_text`, `parents_salutation`, `custom_form_payer_names`, `employer`, `how_hear_personal_referral`, document group `note`, every free-text `*_other` answer, event booking `custom_field_*`, `initial_payload`, PDF references and signed acceptance links.
- **A safety net catches names in kept fields.** After masking, any kept text that contains the surname or email address of someone named in the same record is redacted. Surnames come from every name field, and each part of a hyphenated surname counts on its own. Surnames of five letters or more match anywhere; shorter ones (Lee, Ng, Chen) only as a whole word, so Lee does not match Leeds. First names are not matched, because too many are ordinary words. Ids, `external_id` and `student_code` are never touched. Each sync reports which fields this happened in.

## Development

Requires the .NET 8 SDK. Building, syncing and `dotnet test` need nothing else.

Two scripts use Microsoft tools that are not in this repository, because Microsoft's licences do not allow them to be redistributed. Download them from Microsoft and unpack them into `tools\`:

| Tool | Needed by | Where to get it | Unpack into |
| --- | --- | --- | --- |
| Power BI modelling MCP server | `check-powerbi-project.ps1` | VS Code extension `analysis-services.powerbi-modeling-mcp`. A `.vsix` file is a zip | `tools\powerbi-mcp\package` |
| Power Query SDK tools | The connector scripts, which also need Windows PowerShell 5.1 | NuGet package `Microsoft.PowerQuery.SdkTools`. A `.nupkg` file is a zip | `tools\sdktools` |

| Command | Purpose |
| --- | --- |
| `dotnet test sync\EnrolHQ.Sync.Tests` | Run the sync tool's tests. No token or network needed. GitHub runs them on every push |
| `dotnet build sync\EnrolHQ.Sync -c Release` | Build the sync tool |
| `.\check-powerbi-project.ps1` | Check the generated Power BI project |
| `.\update-parity.ps1` | Regenerate the file the parity test compares against |

### Building the single-file program

Every release on GitHub is built this way by the `Release` workflow when a tag such as `v0.2.0` is pushed: the tests run, the file is built with the tag as its version, its SHA-256 is written beside it, and both are attached to the release. To build the same file by hand:

1. Run the tests:
   ```
   dotnet test sync\EnrolHQ.Sync.Tests
   ```
2. Make sure no `enrolhq-sync.exe` is running, then build:
   ```
   dotnet publish sync\EnrolHQ.Sync -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false -o bin\publish
   ```
   This writes `bin\publish\enrolhq-sync.exe`, about 70 MB, for 64-bit Windows. It replaces the file already there.
3. Check it before handing it on. Neither command calls EnrolHQ:
   ```
   bin\publish\enrolhq-sync.exe --help
   bin\publish\enrolhq-sync.exe status
   ```
   `status` reads the local copy named by `.env`. From a folder with no settings in reach it should stop with "No settings found".

The version inside the file ends with the commit it was built from, for example `0.1.0+57ef307`. It is shown under **Details > Product version** in the file's properties. The commit is whatever was checked out at the time, so commit your changes before building, or the version will name a commit that does not hold them.

To use the file on another computer, copy it into a folder with a `.env` file and run it there. The local copy goes in a `data` folder beside the `.env` file. `bin\` is not committed; the file is not in this repository.

### Adding a masking rule

Rules are keyed on JSON key names, in `sync\EnrolHQ.Sync\Anonymise\Rules.cs`:

- `ByKey` applies wherever the key appears.
- `ByContext` applies to a key under a given parent, written `parent.key`.
- `ByKeyOnPeople` applies only on records that have a first, last or full name.
- `Keep` exempts a `parent.key` from the key rules.

Add a test with each rule, and raise `Rules.Version` so that existing local copies are downloaded again under the new rule. A field that needs no rule because it never holds anything personal goes in `Reviewed.cs` instead. The connector has the same rules in `connector\EnrolHQ.pq`; if it is still in use, make the change there too and run `.\update-parity.ps1`. The parity test fails when the two disagree.

## The Power BI connector (superseded)

`connector\EnrolHQ.pq` is a Power Query connector that loads from the API directly. It works for one table at a time, but Power BI evaluates every table separately and several times over, which runs into the API's sign-in limit and gives no progress or resume. Use `enrolhq-sync` instead.

It is kept because the parity test uses its masking rules as a second implementation to check the sync tool against. It does not have the reviewed list, so it keeps text in fields that have no rule. `build.ps1`, `test.ps1`, `test-masking.ps1` and `get-access-token.ps1` belong to it.

## Licence

MIT. See `LICENSE`.
