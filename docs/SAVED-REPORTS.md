# Saved reports

Saved reports preserve a user's report-state document without changing the trusted SQL
definition. This guide covers persistence, default selection, source-controlled documents,
and the packaged administration page. For end-user Save and Save As steps, see the
[User Guide](USER-GUIDE.md#saved-reports).

## Concepts

A configured report name identifies a report family. The family combines one trusted
appsettings definition with zero or more stored report documents:

- The default document is public and is the document selected for a first-time viewer.
- A private document is visible to its exact owner and administrators.
- A global document is visible to every caller who may view the report family.
- A configured document stores its state in a source-controlled JSON file and is read-only
  at runtime.
- A synthetic default is generated from the report definition when no usable stored
  default exists. It is returned to the client without being persisted.

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
`MYAPP_IR_ADMINISTRATORS`. With `autoCreate` left at its default, the current tables
are created when first needed. Set `autoCreate` to `false` when operators provision the
schema. The current release does not migrate an older saved-report schema in place.

An absent or unreachable storage target does not prevent ordinary report queries. With no
storage configured, a report's saved-report list is simply empty, so the viewer loads
without persistence; every other saved-report and administration operation, and every
operation against a configured but unreachable store, returns a sanitized error until its
target is available.

## Document ownership and visibility

Every family can have one stored default document, and that default is public. Selecting a
database-backed document as the new default publishes it and retains the former default as
an ordinary global document. An update cannot unset the default without selecting another
one; deleting the default document is allowed for its owner and for administrators, after
which the family falls back to its synthetic default until another document is selected.

Private ownership uses the canonical identity resolved from
`InteractiveReport:IdentityClaim`, then the standard identity fallbacks described in
[Authorization](AUTHORIZATION.md#administrators).
Identity comparison is ordinal and case-sensitive.

The family-list endpoint reads the catalogue and returns:

- all documents to an administrator;
- global and default documents to an authorized non-administrator; and
- that caller's private documents.

Loading through `GET /api/reports/{name}/{id}` also verifies that the document belongs to
the named family. A missing document, a document from another family, and a hidden document
all return the same not-found response.

## Loading and hydration

`GET /api/reports/{name}/{id}` loads the requested document and hydrates its `activeTable`
through the same validation and execution routine as `POST /api/reports/{name}/query`.
If the stored document cannot be parsed or hydrated, the load tries the family's stored
default, then a synthetic default. Each candidate is tried at most once. If the synthetic
document also fails, the server returns an error. Authorization denials and cancellation
stop the operation immediately.

`GET /api/reports/{name}/default` starts with the stored default when one exists and
otherwise hydrates the synthetic default. Both load routes return `{ summary, result }`.
`result.document` and the returned data describe the same successful candidate. `summary`
contains that candidate's saved metadata, or `null` for a synthetic document.

A client-supplied document is hydrated once. Failure returns an error without fallback.
The client owns its working document and may submit one it created itself; hydration
requires no saved-report identity. Save As uses `POST /api/reports/{name}/saved`, and
`PUT /api/reports/{id}` explicitly chooses the document to update.

Listing, loading, hydration, and document downloads do not insert, update, or delete saved
reports. Returning a fallback never repairs the stored original. Persistence changes occur
only through explicit save, update, or administration operations.

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

Reconciliation is an explicit host or administration operation. For example, after building
the application and before serving requests, a host with configured document files can run:

```csharp
await app.Services.GetRequiredService<ConfiguredReportDocumentSynchronizer>()
    .EnsureSynced();
```

The synchronizer compares the database catalogue with `documentFiles` and creates or removes
configured identities as needed. A file that already has an identity keeps its stored title
and default selection: later edits to the file change the state it serves, not its catalogue
metadata. The Workbench performs this call at startup. Neither the root configuration
catalogue nor family listing performs synchronization.

If a configured file disappears or cannot be processed, loading its existing ID follows the
same default fallback as any other failed stored document. The load leaves its catalogue row
unchanged. Explicit synchronization reconciles changes to configured file declarations.

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
Configured documents remain read-only. The Administrators editor sets the database-backed
administrator list as a whole; configuration entries remain source-controlled and cannot be
removed there, and the editor is withdrawn when the application decides administrators
itself (see [Authorization](AUTHORIZATION.md#administrators)). Every account picker
searches the application's user directory together with the identities already known from
configuration and storage, and still accepts an identity value typed exactly.

Report names beginning with `__` are reserved for built-in administration definitions.

## Supply account choices

Account pickers always offer the identities Interactive Reports already knows: configured
and database administrators and saved-report owners. Applications
add a searchable directory of display names and canonical values with a callback that
answers a search with .NET identities:

```csharp
using System.Security.Claims;

builder.Services
    .AddInteractiveReports(builder.Configuration)
    .UseUserDirectory((search, ct) => ValueTask.FromResult<IEnumerable<ClaimsIdentity>?>(
        AccountDirectory.Find(search.Search, search.Limit)
            .Select(account => new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, account.SubjectId),
                new Claim(ClaimTypes.Name, account.DisplayName),
            ]))));
```

The callback runs only after the caller passes the administration gate. Each identity
resolves through the same claim chain as sign-in, so the value offered is the value that
is stored as an owner or grant. Directory order is preserved ahead of known identities.
Returning `null` or nothing leaves the picker with known identities and free-form entry.
Directory membership supplies choices only; it grants no authority. The class-based
`IInteractiveReportUserProvider`, lookup limits, and memoization are described in the
[Integration API](API.md#supply-administration-user-choices).

## Import and export

The administration page downloads the canonical `{ title, default, state }` envelope used
by `documentFiles`. This supports a deliberate workflow:

1. Build and test a private saved report against the live definition.
2. Download its envelope from administration.
3. Commit the JSON file and add its path to `documentFiles`.
4. Deploy the application, explicitly synchronize configured documents, and verify the catalogue.

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
- storage backups cover both the saved-report and administrator tables; and
- application authorization grants each required saved-report action.

The complete operation and denial matrix is in [Authorization](AUTHORIZATION.md). The
browser workflow is in the [User Guide](USER-GUIDE.md#saved-reports).

