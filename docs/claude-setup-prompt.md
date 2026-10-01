# Asking Claude about the data

The Power BI project comes with its tables, relationships and measures already set up, so there is nothing to build first.

## Before you start

1. Open `data\<school>\anonymised\powerbi\EnrolHQ.pbip` in Power BI Desktop.
2. Click **Refresh** on the Home ribbon if the tables are empty. It takes about ten seconds.
3. Open Claude Desktop. If it was already open when Power BI was set up, quit it from the system tray and open it again.

## 1. Connect

Paste this into Claude Desktop:

```
Connect to 'EnrolHQ' in Power BI Desktop. This is a school's admissions data from EnrolHQ.

Before answering anything, read the model: list the tables with their descriptions and row counts, the relationships, and the measures. Read the sync_info table and tell me when the data was last synced and whether it is anonymised.

Notes on the model:
- applications has one row per student application. application_details has the same applications with every field, and child tables such as application_details_guardians and application_details_siblings.
- The school's own name for each status is application_statuses[status_label]. Use it whenever a question is about status, not the status number.
- event_bookings_student_profiles links event bookings to applications.
- Two relationships are switched off to avoid clashes: events to campuses, and event_bookings to events (bookings reach events through events_sessions). Use USERELATIONSHIP if you need either.
- Statuses such as Trashed, Declined and Not Proceeding are included in every count unless you filter them out. Ask me if it is unclear whether a question means all applications or only live ones.
- Use the existing measures where one fits. Show me the DAX you ran with each answer.
```

## 2. Ask

Example questions:

- How many applications do we have for 2027, by entry grade and status?
- What is our offer acceptance rate by entry year?
- Which suburbs do our 2027 applicants come from? Show the top 15.
- How many 2027 applicants have booked an event, and how does their offer acceptance compare with those who have not?
- What is the split of applicants by current school for Year 7 entry?
- How long, on average, between an offer being made and being accepted?
- Which events had the most bookings this year, and how many of those students went on to apply?

## 3. Adding to the model

Claude is connected in read-write mode and asks before each change. To add a measure:

```
Add a measure called 'Live applications' to the applications table that counts applications whose status label is not Trashed, Declined or Not Proceeding. Show me the DAX first.
```

Save the project in Power BI Desktop (Ctrl+S) afterwards to keep it.

## Keeping the data current

Run `enrolhq-sync` again, then click **Refresh** in Power BI Desktop. Later runs only fetch the applications that changed, so they are much quicker than the first.
