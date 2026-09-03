# Saved reports

Saved reports preserve a user's report-state document without changing the trusted SQL
definition. This guide covers persistence, default selection, source-controlled documents,
and the packaged administration page. For end-user Save and Save As steps, see the
[User Guide](USER-GUIDE.md#saved-reports).

## Concepts

A configured report name identifies a report family. The family combines one trusted
appsettings definition with one or more report documents:

- The default document is public and is the document selected for a first-time viewer.
- A private document is visible to its exact owner and administrators.
- A global document is visible to every caller who may view the report family.
- A configured document stores its state in a source-controlled JSON file and is read-only
  at runtime.
- A synthetic default is generated from the report definition when no configured document
  owns the default role.

Database-generated integer IDs identify documents. They do not identify report definitions,
and a report-state document itself carries no trusted database or SQL provenance.

## Enable persistence

The package creates no persistence target unless the host configures one:

```json
{
  "InteractiveReport": {
    "SavedReports": {
      "dataSource": "MainDb",
      "tablePrefix": "MYAPP_"
    }
  }
}
```

`dataSource` accepts a `ConnectionStrings` name or a literal connection string. It uses
the same provider resolution as a report definition. Alternatively, set `connection` to
a factory registered with `AddConnection`.

`tablePrefix` is optional. The example produces `MYAPP_IR_SAVED_REPORTS` and
`MYAPP_IR_REPORT_AUTHORIZATION`. With `autoCreate` left at its default, the current tables
are created when first needed. Set `autoCreate` to `false` when operators provision the
schema. The current release does not migrate an older saved-report schema in place.

An absent or unreachable storage target does not prevent ordinary report queries. Saved-report
and administration operations return a sanitized error until their target is available.

## Document ownership and visibility

Every family has exactly one default document, and the default is public. Selecting a
database-backed document as the new default publishes it and retains the former default as
an ordinary global document. The default cannot be unset without selecting another one.

Private ownership uses the canonical identity resolved from
`InteractiveReport:IdentityClaim`, then the standard identity fallbacks described in
[Authorization](AUTHORIZATION.md#administrator-resolution-and-fail-closed-behavior).
Identity comparison is ordinal and case-sensitive.

The family-list endpoint reconciles configured files, then returns:

- all documents to an administrator;
- global and default documents to an authorized non-administrator; and
- that caller's private documents.

Loading through `GET /api/reports/{name}/{id}` also verifies that the document belongs to
the named family. A missing document, a document from another family, and a hidden document
all return the same not-found response.

## Source-controlled documents

Add JSON document paths to a report definition. Relative paths use the host's content root:

```json
{
  "InteractiveReport": {
    "Reports": {
      "orders": {
        "dataSource": "MainDb",
        "sql": "SELECT ORDER_ID, CUSTOMER, AMOUNT FROM ORDERS",
        "documentFiles": [
          "ReportDocuments/orders.default.json",
          "ReportDocuments/orders.finance.json"
        ]
      }
    }
  }
}
```

Each file is an envelope containing a required title, an optional default flag, and a
normal report-state document:

```json
{
  "title": "Finance",
  "default": false,
  "state": {
    "activeTable": "base",
    "tables": {
      "base": {
        "from": "definition",
        "schema": null,
        "composables": [
          {
            "kind": "filter",
            "filters": [ { "expr": "AMOUNT > 0" } ]
          },
          {
            "kind": "sort",
            "sorts": [ { "col": "AMOUNT", "dir": "desc" } ]
          }
        ]
      }
    }
  }
}
```

At most one configured file in a family may set `default` to `true`. That declaration
owns default selection until configuration changes; an API attempt to replace it returns
conflict. Configured titles are deployment declarations and need not be unique.

Copy every referenced file to build and publish output. The Workbench project demonstrates
an MSBuild content rule for its `ReportDocuments` directory.

### Reconciliation

Configured files retain their state on disk. The database contains their generated ID,
family, filename, title, and default metadata so they can participate in the ordinary
saved-report catalogue.

Reconciliation occurs when a family is listed. The server reads the complete family once,
compares it with the current `documentFiles` declarations, and repairs configured identities
before applying caller visibility. The root configuration catalogue does not reconcile files.

If a referenced file disappears, loading its former ID removes the stale catalogue row and
returns not found. A synthetic default is restored when the missing file owned the default
role. If a present file contains a state that cannot be processed, the failed identity is
removed and the load returns not found; the next family listing creates a fresh identity and
retries the declaration. Failures are logged with the family, ID, filename, and exception.

The state returned by the server may contain refreshed schema caches. Those caches are
advisory and are rebuilt from the live trusted definition when required. See
[Architecture](ARCHITECTURE.md#saved-reports-and-configured-documents) for the persistence
boundary and [Integration API](API.md#rest-surface) for wire contracts.

## Administration page

Map the JSON adapter and browse to the matching admin route:

```text
/api/reports/admin
```

Bootstrap at least one administrator in configuration:

```json
{
  "InteractiveReport": {
    "Administrators": [ "bootstrap-admin-id" ]
  }
}
```

Set `InteractiveReport:WhoamiEnabled` when administrators need to verify the exact
canonical identity seen by the application. Disable the diagnostic when it is no longer
needed.

The page can publish or unpublish a database document, select a default, reassign an owner,
inspect state, download an envelope, upload an envelope, and delete editable documents.
Configured documents remain read-only. The authorization editor manages database-backed
administrator, report-restriction, and report-user grants; configuration entries remain
source-controlled and cannot be removed there.

Report names beginning with `__` are reserved for built-in administration definitions.

## Supply account choices

Applications can replace free-form identity entry with a directory of display labels and
canonical values:

```csharp
public sealed class ReportUsers : IInteractiveReportUserProvider
{
    public ValueTask<IReadOnlyCollection<InteractiveReportUser>?> GetUsers(
        ClaimsPrincipal administrator,
        CancellationToken cancellationToken = default)
        => ValueTask.FromResult<IReadOnlyCollection<InteractiveReportUser>?>(
        [
            new("Ada Lovelace", "ada-id"),
            new("Grace Hopper", "grace-id"),
        ]);
}

builder.Services
    .AddInteractiveReports(builder.Configuration)
    .UseUserProvider<ReportUsers>();
```

The provider is scoped and runs only after the caller passes the administration gate.
Its order is preserved. Returning `null` or an empty collection retains free-form entry.
Directory membership supplies choices only; it grants no authority.

## Import and export

The administration page downloads the canonical `{ title, default, state }` envelope used
by `documentFiles`. This supports a deliberate workflow:

1. Build and test a private saved report against the live definition.
2. Download its envelope from administration.
3. Commit the JSON file and add its path to `documentFiles`.
4. Deploy the application and verify that the configured identity reconciles.

Uploading an envelope validates its state against the selected report family's current
schema and creates a private document owned by the importing administrator. File publication
metadata does not make the uploaded document global or default.

## Operational checks

Before deploying saved reports, verify that:

- the persistence principal can create the two tables, or operators provisioned the current
  schemas with `autoCreate: false`;
- configured document files are present under the published content root;
- the bootstrap administrator identity matches the host's authenticated principal;
- private ownership values use the same canonical identity format;
- storage backups cover both saved-report and authorization tables; and
- application authorization grants each required saved-report action.

The complete operation and denial matrix is in [Authorization](AUTHORIZATION.md). The
browser workflow is in the [User Guide](USER-GUIDE.md#saved-reports).

