# SQLView Pro Google Sheets Template Report

The Google Sheets Template report type works like the [Excel Template](exceltemplate)
report, but the template lives in Google Drive instead of the DNN file system. At
render time SQLView Pro clones the template, writes the report's query results into
it live (so any formulas, charts or pivot tables in the template recalculate using
Google's own engine), exports the result as an `.xlsx` file, streams it to the
browser, and then deletes the temporary clone.

### Google Sheets Template Report Fields

-   Template Folder – a dropdown listing every folder (by name) within the
    configured shared Drive; pick the folder that contains the template
    spreadsheet. The dropdown's value is the folder's Drive id, but you no
    longer need to look up or paste that id yourself.
-   Template Name – the template spreadsheet's file name within that Drive folder
-   Data Sheet Name – the sheet (tab) within the template where report data is
    written
-   Contains Header Row – check this if the sheet already has a header row you
    want to keep; data is then written starting on row 2. When unchecked, the
    whole sheet is cleared and a header row is generated from the report's
    column names.
-   Output Filename Prefix – the prefix used to name the exported `.xlsx` file.
    `[TICKS]` is replaced with the current tick count to keep file names unique.
-   Disposition Type – `inline` or `attachment`, same as the Excel Template report

### Prerequisites

-   A Google Cloud service account with the Drive and Sheets APIs enabled.
-   The target Drive folder (and the template spreadsheet within it) must be
    **shared with the service account's email address** - without this the
    report will fail to find or clone the template.
-   The service account's JSON key must be stored in Delinea Secret Server as a
    secret with a field slug of `json-key`, and the following `appSettings` must
    be configured in the host site's `web.config` (see [Configuration](configuration)):
    -   `DNNStuff:SQLViewPro:DelineaBaseUrl`
    -   `DNNStuff:SQLViewPro:DelineaUsername`
    -   `DNNStuff:SQLViewPro:DelineaPassword`
    -   `DNNStuff:SQLViewPro:DelineaGoogleSecretName` - the secret name used to
        retrieve the Google service-account key
    -   `DNNStuff:SQLViewPro:GoogleSheetsSharedDriveId` - the shared Drive id
        whose folders are listed in the "Template Folder" dropdown

### Notes

-   The original template is never modified - each report run clones it, writes
    to the clone, and deletes the clone once the export completes (even if the
    export itself fails).
-   Because writing uses Google's `USER_ENTERED` input option, formulas typed
    into the data range are evaluated the same way they would be if typed by a
    user in Sheets.
