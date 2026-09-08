# Windows Authentication sample

An ASP.NET Core host with a Windows-authenticated orders report, saved reports,
administration, and CSV export. `Program.cs` registers the authentication and
authorization hooks; `ReportAccess.cs` contains the application access rules.

## Run

Use Windows with the .NET 10 SDK and Node.js 20 or later. From the repository root:

```sh
npm ci
npm run build:client
dotnet run --project samples/WindowsAuthentication --urls http://localhost:5044
```

Open [the report](http://localhost:5044) in a browser that supports Windows
Authentication. The host uses ASP.NET Core Negotiate authentication on Kestrel.
Browser policy may require allowing integrated authentication for this address.
Hosting requirements for IIS and Kerberos are covered in
[Microsoft's Windows Authentication guide](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/windowsauth?view=aspnetcore-10.0).

The host creates three sample orders in `App_Data/reports.db`. Saved documents and
administrator grants use the same explicitly configured database, with their tables
created on first use. The database survives restarts and is ignored by Git. The
process account needs write access to `App_Data`; Windows Authentication identifies
the caller but does not impersonate that caller for SQLite access.

## Bootstrap administration

1. Open [whoami](http://localhost:5044/api/reports/whoami) and copy the exact `identity`
   value. It normally has the form `DOMAIN\username` or `MACHINE\username`.
2. Put that value in `InteractiveReport:Administrators` in `appsettings.json`. Escape
   the backslash in JSON:

   ```json
   "Administrators": [ "DOMAIN\\username" ]
   ```

3. Restart and open [administration](http://localhost:5044/api/reports/admin).
   Save a document from the report viewer first to have a document to administer.

Administrators can publish documents, choose defaults, reassign ownership, and
manage further administrator grants. Configured bootstrap entries are read-only in
the administration page. Identity matching is case-sensitive; use the value returned
by `whoami`. Set `InteractiveReport:WhoamiEnabled` to `false` when bootstrap is complete.

Windows Authentication supplies the current principal, not an account-search service.
The administration pickers use identities already known to Interactive Reports.
To search an organization's directory, register `UseUserDirectory(...)` with an
application directory service as described in the [authorization guide](../../docs/AUTHORIZATION.md).

## Authorization hooks

| Hook | Sample behavior |
|---|---|
| `AddAuthentication(...).AddNegotiate()` and `UseAuthentication()` | Populate the caller's Windows principal. |
| `Reports.Read` policy, named by the report definition | Require authentication; also require `ReportAccess:ReaderGroup` when configured. |
| `UseAuthorization(...)` | Require `ReportAccess:ExporterGroup` for CSV export. Other known actions proceed to the library's ownership and administrator rules. Unknown actions are denied. |
| Optional `UseAdministrators(...)` | Use `ReportAccess:AdministratorGroup` as the authority for administrator membership when configured. |

By default, all authenticated Windows users can read the report and save private
documents. Export is denied until an exporter group is configured. To restrict readers
and enable exports for selected Windows groups, set:

```json
"ReportAccess": {
  "ReaderGroup": "DOMAIN\\Report Readers",
  "ExporterGroup": "DOMAIN\\Report Exporters",
  "AdministratorGroup": ""
}
```

Group checks call `ClaimsPrincipal.IsInRole` on the authenticated principal. Set
existing domain or machine group names appropriate to your environment. An
administrator still needs the reader group to access a restricted report and the
exporter group to export it.

Leave `AdministratorGroup` empty to use the configured bootstrap accounts and the
administration page's administrator editor. Setting it registers `UseAdministrators`:
Windows group membership then replaces both sources, and the page hides that editor.
Document administration remains available to members of the configured group.

The operation callback can inspect `request.Resource.ReportName`, `SavedReport`, and
`Candidate`, or resolve a scoped ACL service from `request.RequestServices`. Returning
`true` permits the operation to continue; it cannot override a report policy,
saved-document ownership, or an administrator requirement. In this sample, an
administrator without the exporter group receives the same export denial as anyone else.

The endpoint conventions require authenticated API calls. The library marks its
packaged HTML and browser assets as anonymous; those responses contain no report data.

## Verify

In PowerShell 7, while the sample is running:

```powershell
Invoke-WebRequest http://localhost:5044/api/reports/orders/schema -SkipHttpErrorCheck
Invoke-RestMethod http://localhost:5044/api/reports/whoami -UseDefaultCredentials -AllowUnencryptedAuthentication
Invoke-RestMethod http://localhost:5044/api/reports/orders/default -UseDefaultCredentials -AllowUnencryptedAuthentication
```

The first request should receive `401` with a `WWW-Authenticate: Negotiate` header.
The authenticated requests should return the caller's identity and three report rows.
The PowerShell flag permits credential negotiation over the sample's local HTTP URL;
use HTTPS for a deployed host.

Check these access boundaries with accounts in the corresponding groups:

- A reader can query and save a private document; another reader cannot edit it.
- A caller outside a configured reader group cannot query the report.
- A caller outside the exporter group cannot export, even when an administrator.
- A bootstrap administrator can manage saved documents and administrator grants.
- After restarting, saved documents and administrator grants remain available.

The sample references the repository projects directly. In a separate application,
replace those project references with the corresponding NuGet packages, which include
the browser assets. Keep the explicit Negotiate and SQLite package references.
