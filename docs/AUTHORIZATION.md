# Authorization

Interactive Reports uses the host application's ASP.NET Core identity. It does not
authenticate users, issue cookies or tokens, or maintain a second user directory.
Authentication middleware populates `HttpContext.User`; Interactive Reports passes
that `ClaimsPrincipal` to its authorization gates.

The central `IReportAuthorizationService` is the security boundary shared by mapped
clients. JSON, GraphQL, and file-download packages translate their transports into an
`InteractiveReportRequestContext`; the server makes definition, saved-resource,
administrator, application-authorizer, feature, and trusted-context decisions without
depending on `HttpContext` or an HTTP result type.

These controls make internet exposure possible; they do not make it the preferred
deployment. Keep the application on a trusted network when practical. If it must be
publicly reachable, use a dedicated reporting database or read replica and a
least-privileged read-only principal, never the primary production database.

Every published data and administration endpoint participates in application
operation authorization. The only exceptions are the opt-in `whoami` bootstrap
diagnostic and packaged HTML/CSS/JavaScript delivery. Those exceptions expose no report
data and grant no authority.

Authorization never depends on client UI state, navigation history, or how the request
was produced. For each requested operation the server asks one question:

```text
ClaimsPrincipal + Action + Resource -> allow or deny
```

Only an allowed operation proceeds to execution or persistence.

## The three rules

Interactive Reports deliberately keeps a small amount of access control of its own and
leaves the rest to the integrating application. The engine is built so that an
application can start with the minimal quickstart and take over each decision as it
grows.

1. **A report is public or it is not.** `authorization.allowAnonymous: true` admits
   anyone, including callers no authentication system has identified. Every other
   report admits any authenticated caller. Which authenticated users may see a report
   beyond that is the application's business: name an ASP.NET Core `policy` on the
   definition, or narrow with the operation authorizers described below. The engine
   holds no per-report user lists.
2. **The application decides who administers, when it wants to.** Administrators list
   every saved report, publish and unpublish, select defaults, reassign owners, delete,
   download and upload documents, and manage the administrator list. The first
   implemented source answers the administrator question: a callback registered with
   `UseAdministrators`, then an `InteractiveReport:AdministratorPolicy`, then the
   built-in fallback of `InteractiveReport:Administrators` plus the list kept by the
   administration center. See [Administrators](#administrators).
3. **Saved reports have owners.** A private document is visible to its owner and to
   administrators; a global or default document is visible to everyone who may see the
   report. Only the owner or an administrator changes or deletes a document; only an
   administrator publishes, selects a default, or reassigns ownership.

Operation authorizers registered by the application always get the final say on the
built-in decisions: they can deny any action, including an administrator's, and never
widen what the rules above allow.

## Authorization layers

Application-operation authorization is one layer in a larger security model. A
request may need to pass all of these gates:

1. Host endpoint conventions, such as
   `app.MapInteractiveReportJson(...).RequireAuthorization(...)`.
2. The report definition's `authorization` block: public, authenticated, or a policy.
3. Built-in saved-report ownership, publication, and read-only rules, and the
   administrator decision for administrator actions.
4. Every configured application-operation authorizer.
5. Server-resolved context parameters used for row-level constraints.

The operation authorizer does not replace report policies, ownership rules, feature
flags, configured-document immutability, or row-level security. It can further
restrict them.

The usual ASP.NET Core setup still applies:

```csharp
builder.Services
    .AddAuthentication(/* host scheme */)
    .AddCookie(); // or JWT bearer, OIDC, an application-specific scheme, etc.

builder.Services.AddAuthorization();

var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();

app.MapInteractiveReportJson("/api/reports");
```

Interactive Reports does not require a particular authentication scheme. The host
must arrange for the correct principal to be present before the mapped endpoints run.

## Choosing an integration

| Integration | Best fit | Registration | Decision code |
|---|---|---|---|
| Direct callback | Small or self-contained access rules | `UseAuthorization(...)` | A delegate returning `ValueTask<bool>` |
| Native resource handler | Applications already organized around authorization requirements and handlers | `UseAspNetCoreAuthorization()` plus `IAuthorizationHandler` registration | `AuthorizationHandler<InteractiveReportAuthorizationRequirement, InteractiveReportAuthorizationResource>` |
| Callback to named policies | Applications with an established policy catalog but no need for a new handler type | `UseAuthorization(...)` plus `AddAuthorization(...)` | Resolve `IAuthorizationService` and call `AuthorizeAsync` with a policy name |

There is no security-strength difference between the three. The difference is how
the application expresses and composes its decision. The administrator question has
the same two flavours, a callback and a policy name, described under
[Administrators](#administrators).

## Common request contract

Every callback receives an `InteractiveReportAuthorizationRequest`. The native
adapter exposes the action on `InteractiveReportAuthorizationRequirement` and passes
the same resource separately.

| Member | Meaning |
|---|---|
| `User` | The current ASP.NET Core `ClaimsPrincipal`. Use its claims, roles, authentication type, or name as the host normally would. |
| `Action` | One `InteractiveReportAction` describing the operation currently being evaluated. |
| `Resource.ReportName` | The exact report-definition name. Authorization is therefore scoped to the dataset/report definition, not merely to a database connection. |
| `Resource.SavedReport` | Current immutable saved-report metadata, when an existing row is involved. It contains id, title, owner, global/default flags, and origin. |
| `Resource.Candidate` | The mutable, typed saved report proposed by a create, update, or document-upload operation. Its metadata is effective rather than a sparse patch. |
| `RequestServices` | The current request service provider. Use it to resolve scoped ACL services or `IAuthorizationService`. It is available on the callback request, not on the native requirement. |

`Resource.Candidate` is a
`InteractiveReport.AspNetCore.Definitions.SavedReportCandidate`. It exposes
`Id`, `ReportName`, `Title`, `Public`, `Default`, `Owner`, `State`, and
`StateChanged`. `Public` corresponds to the HTTP field `isGlobal`; `Default`
corresponds to `isDefault` on updates. `State` is the typed `ReportState` object graph, including
the unordered table map, explicit recursive `from` dependencies, nullable per-table
schema caches, direct composables, filters, computed columns, formats, and other nested
structures. Each child consumes its completed parent's relation; cached schemas remain
advisory response data, so authorization code must not treat them as proof of either
the configured SQL or a composed relation's shape.

For updates, title, global/default status, and owner are the effective values after the
client patch has been applied to current metadata. `StateChanged` distinguishes a
submitted replacement from an update that retains the existing state. When it is
false, `State` is null. The server deliberately does not deserialize current stored
state merely to authorize an update.

Query and export authorization receives the report name and action, not the submitted
query state. Data partitioning belongs in trusted server-side context parameters rather
than in client-authored filters.

## Typed candidate inspection and mutation

The candidate supplied to authorization is the same mutable object the endpoint later
validates and persists. An application can therefore inspect the full typed shape and
narrow a client request before granting it:

```csharp
using InteractiveReport.AspNetCore.Definitions;
using InteractiveReport.Core.Model;

reports.UseAuthorization((request, cancellationToken) =>
{
    cancellationToken.ThrowIfCancellationRequested();

    if (request.Action == InteractiveReportAction.CreateSavedReport
        && request.Resource.Candidate is { } candidate)
    {
        // This host never permits end users to publish directly. The private save may
        // still proceed if the rest of the rule grants CreateSavedReport.
        candidate.Public = false;

        // The state is typed. No JsonElement traversal or second deserialization is
        // required.
        // This policy deliberately counts filters in every declared table,
        // including inactive alternatives, rather than only activeTable ancestry.
        var allDeclaredFilters = candidate.State?.Tables?.Values
            .SelectMany(table => table.Composables ?? [])
            .Where(composable => string.Equals(
                composable.Kind,
                "filter",
                StringComparison.OrdinalIgnoreCase))
            .SelectMany(composable => composable.Filters ?? [])
            .ToList();
        if (allDeclaredFilters?.Count > 20)
            return ValueTask.FromResult(false);
    }

    return ValueTask.FromResult(ApplicationReportAcl.Allows(
        request.User,
        request.Action,
        request.Resource));
});
```

Mutation follows these rules:

- The base `CreateSavedReport`, `UpdateSavedReport`, or `UploadReportDocument` action is
  evaluated first. All registered authorizers see the same candidate instance.
- Publication and owner actions are derived from the effective candidate after the
  base action passes. Setting `Public = false` during create therefore removes an
  unwanted publication request before the administrator boundary is evaluated.
- If an authorizer later adds a public, default-selection, or owner change, the server detects it
  and evaluates the corresponding administrator action before persistence.
- A denial at any point discards all mutations because nothing has yet been stored.
- After authorization, title/owner invariants and the submitted state's executable
  view are validated against the current dataset schema. The typed state is then
  serialized canonically. Unknown client JSON members are not copied into storage.

Assigning `Candidate.State` marks `StateChanged` true. Mutating a nested state object
on create or on an update that already supplied state preserves its existing true
value. On an update with `StateChanged == false`, the stored JSON remains byte-for-byte
untouched. To replace it from authorization code, assign a new `ReportState` to
`Candidate.State`.

This mutation surface is available through all three authorization integrations. A
direct callback is usually the clearest place for request normalization. Native
resource handlers and resource-aware named policies receive the same mutable resource.

Saved documents are bound to the current `ReportState` model before they are returned or
executed. Configured files remain read-only and are reconciled through their database
catalogue identities. The complete persistence and recovery behavior is documented in
[Saved reports](SAVED-REPORTS.md#reconciliation).

Query, LOV, and export are addressed by configured definition key. They authorize that
definition and execute the client-submitted document without reading the saved-report
store. The submitted document has no required ID or persisted provenance. A client copy
can therefore continue to execute after its source document's ownership or publication
changes, provided the caller still has access to the configured definition.

Authorization is expressed in facts rather than in how the caller reached an
endpoint. For saved reports, the relevant facts are available on the resource:

- `ReadSavedReport` is normally allowed when the report is default/global, the caller
  owns it, or the caller is an administrator.
- `UpdateSavedReport` and `DeleteSavedReport` are normally allowed when the caller owns
  the report or is an administrator. Publication does not remove owner
  rights over title/state or deletion.
- Changing global publication, default selection, or ownership emits a separate action, so those
  decisions do not have to be inferred from `UpdateSavedReport`.

The engine applies the same built-in facts, with the administrator decision made as
described under [Administrators](#administrators). The application authorizer receives
the operation facts so it can add restrictions; it cannot supply administrator
authority.

Authorization decisions are centralized in the server's transport-neutral
`IReportAuthorizationService` and `IInteractiveReportServer`. Transport adapters call
those boundaries and translate their results. Configuration stores expose a lightweight
name/authorization envelope, allowing the authentication and policy gates to run before
connection resolution and saved-default hydration. Saved-report listing and normalized
title-collision queries occur only after that report-level gate succeeds.
Definition-free administration endpoints use the same service before invoking the
administrator store or user directory.

## Action reference

| Action | Request that emits it | Built-in notes |
|---|---|---|
| `ViewReport` | Root configuration catalogue or schema for an ordinary report | Report-definition authentication and policy run first. The root catalogue returns names and titles only; it does not reconcile documents. |
| `Query` | Process a client document through JSON, or execute a stored document through GraphQL | JSON processing authorizes only the resolved report definition and never supplies original document metadata. GraphQL loads a stored document and supplies `SavedReport` metadata. |
| `Export` | File-client download | The report's `download` feature must also be enabled. Admin-list download emits `ListAllSavedReports` as well. |
| `ListSavedReports` | List visible saved reports for one report definition | The server reconciles the complete family in one store query, then filters in memory. Administrators see all rows; other callers see public and exactly owned rows. |
| `ReadSavedReport` | Load one saved report, or execute it through GraphQL | Public, owner, and administrator access are distinguished from `SavedReport` metadata and the principal. |
| `CreateSavedReport` | Create a saved report | Requires an authenticated canonical owner and the `savedReports` feature. Receives the typed candidate before publication actions are derived. |
| `UpdateSavedReport` | Update a saved report | Owner or administrator. Receives effective metadata and only client-authored replacement state. Global publication and default selection remain unchanged unless their separate actions also pass. Configured content remains read-only. |
| `DeleteSavedReport` | Delete a saved report | Owner or administrator. Configured rows remain undeletable even when authorized. |
| `PublishGlobalReport` | Effective definition changes public status | Emitted for both publishing and unpublishing after base-action mutation. Administrator action. |
| `SelectDefaultReport` | Effective definition selects a new family default | Administrator action. The new default becomes global and the previous default remains global. A configured default cannot be replaced through the API. |
| `ChangeSavedReportOwner` | Effective definition changes owner | Administrator action. |
| `ListAllSavedReports` | Administrator view of a family list, or schema/query/download of the built-in `__saved-reports` definition | Administrator action. Its download also emits `Export`. |
| `ListAuthorizationUsers` | Look up application accounts for the administration pickers | Administrator action. Lookup entries are choices, not grants. |
| `ManageAdministrators` | List or replace the database-authored administrator list | Administrator action. Configured administrators remain read-only, and both lists are inert while the application decides administrators. |
| `DownloadReportDocument` | Download the canonical admin JSON envelope | Administrator action. |
| `UploadReportDocument` | Validate and import an admin JSON envelope | Administrator action. Upload always creates a private user document; file publication metadata is ignored. |

One HTTP request can emit several actions. These examples assume authorization does
not first narrow the typed candidate:

- Creating a private report emits `CreateSavedReport`.
- Creating a global report emits `CreateSavedReport` and `PublishGlobalReport`; both must pass.
- Updating title, state, global status, default selection, and owner emits
  `UpdateSavedReport`, `PublishGlobalReport`, `SelectDefaultReport`, and
  `ChangeSavedReportOwner`.
- Exporting the administrator listing emits `ListAllSavedReports` and `Export`.
- Executing a saved report through GraphQL emits `ReadSavedReport` and `Query`.

Every distinct action must be granted. Evaluation may stop on the first denial, so an
authorization callback is not a complete audit-event stream. Audit accepted business
events at the application boundary where appropriate.

## Option 1: direct callback

Register a callback on the builder returned by `AddInteractiveReports`. (The
`AddConnection` factory here is illustrative — a connection-string-backed source is
normally just the definition's `dataSource`, with no code at all; code registration
remains for custom factories and wrapper connection types.)

```csharp
var reports = builder.Services
    .AddInteractiveReports(builder.Configuration)
    .AddConnection("MainDb", services =>
        new SqlConnection(
            services.GetRequiredService<IConfiguration>()
                .GetConnectionString("MainDb")));

reports.UseAuthorization((request, cancellationToken) =>
{
    cancellationToken.ThrowIfCancellationRequested();

    var saved = request.Resource.SavedReport;
    var caller = request.User.FindFirstValue(ClaimTypes.NameIdentifier);
    var isOwner = saved is not null
        && caller is not null
        && string.Equals(saved.Owner, caller, StringComparison.Ordinal);
    var isPublic = saved is { IsGlobal: true } or { IsDefault: true };
    var isAdministrator = request.User.IsInRole("ReportAdministrators");

    var allowed = request.Action switch
    {
        InteractiveReportAction.ReadSavedReport =>
            isPublic || isOwner || isAdministrator,

        InteractiveReportAction.UpdateSavedReport
            or InteractiveReportAction.DeleteSavedReport =>
            isOwner || isAdministrator,

        InteractiveReportAction.PublishGlobalReport
            or InteractiveReportAction.SelectDefaultReport
            or InteractiveReportAction.ChangeSavedReportOwner
            or InteractiveReportAction.ListAllSavedReports
            or InteractiveReportAction.ListAuthorizationUsers
            or InteractiveReportAction.ManageAdministrators
            or InteractiveReportAction.DownloadReportDocument
            or InteractiveReportAction.UploadReportDocument =>
            isAdministrator,

        InteractiveReportAction.Export =>
            request.User.HasClaim("reports", "export"),

        InteractiveReportAction.CreateSavedReport =>
            request.User.HasClaim("reports", "save"),

        _ => true,
    };

    return ValueTask.FromResult(allowed);
});
```

This pattern makes the owner/public/administrator facts explicit. The identity claim
used for `caller` must match `InteractiveReport:IdentityClaim`; the example uses the
default first choice, `ClaimTypes.NameIdentifier`. In a real application, centralize
that identity mapping instead of duplicating it in several callbacks. Note that the
callback's `isAdministrator` only narrows: to make the same role the source of
administrator authority, register it with `UseAdministrators` as well.

Callbacks can be asynchronous and can resolve scoped services from the request:

```csharp
reports.UseAuthorization(async (request, cancellationToken) =>
{
    var acl = request.RequestServices.GetRequiredService<IReportAcl>();

    return await acl.AllowsAsync(
        request.User,
        request.Action,
        request.Resource,
        cancellationToken);
});
```

Do not resolve a scoped ACL service while configuring the application and capture it
in the callback. Resolve it from `request.RequestServices`, as above.

The callback is registered as an operation authorizer for every protected operation. Additional
callbacks can branch on `request.Resource.ReportName` for per-report restrictions and
compose with the saved-report rule above:

```csharp
reports.UseAuthorization((request, _) =>
{
    if (request.Resource.ReportName.Equals(
            "payroll",
            StringComparison.OrdinalIgnoreCase))
    {
        return ValueTask.FromResult(request.User.IsInRole("PayrollReaders"));
    }

    return ValueTask.FromResult(true);
});
```

Return `true` to grant the current action and `false` to deny it. A callback may throw
`InteractiveReportAuthorizationDeniedException` when exception-based control flow is
more natural. Do not throw a general exception for an expected denial; general
exceptions are treated as authorization infrastructure failures.

## Option 2: native ASP.NET Core resource handlers

Enable the adapter and register a normal ASP.NET Core authorization handler:

```csharp
var reports = builder.Services
    .AddInteractiveReports(builder.Configuration)
    .AddConnection("MainDb", _ => new SqlConnection(connectionString));

reports.UseAspNetCoreAuthorization();

builder.Services.AddScoped<
    IAuthorizationHandler,
    InteractiveReportsAuthorizationHandler>();
```

`UseAspNetCoreAuthorization()` ensures ASP.NET Core authorization services are
registered. It does not grant any operation. If no handler succeeds the emitted
requirement, the operation is denied.

A typed handler can use constructor-injected application services:

```csharp
public sealed class InteractiveReportsAuthorizationHandler(
    IReportAcl acl)
    : AuthorizationHandler<
        InteractiveReportAuthorizationRequirement,
        InteractiveReportAuthorizationResource>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        InteractiveReportAuthorizationRequirement requirement,
        InteractiveReportAuthorizationResource resource)
    {
        var allowed = await acl.AllowsAsync(
            context.User,
            requirement.Action,
            resource);

        if (allowed)
            context.Succeed(requirement);
    }
}
```

The handler must call `context.Succeed(requirement)` to grant the action. Returning
without succeeding is a denial. `context.Fail()` can record an explicit failure, but
ordinary ASP.NET Core handler composition rules apply: applications should use it only
when no other handler may grant the same requirement.

Multiple ASP.NET handlers can observe the requirement and resource. Their behavior is
the standard ASP.NET Core behavior for one requirement. This is inside the native
adapter. If the application also registers a direct Interactive Reports callback, the
native adapter's final result and the callback result are combined with AND semantics.

The standard `AuthorizationHandler` API does not carry a `CancellationToken`. A handler
that performs cancellable I/O can obtain request cancellation through an
application-specific request-scoped service or `IHttpContextAccessor`. Avoid blocking
authorization calls.

## Option 3: callback delegating to named policies

This option is useful when the application already names its access rules as policies
and wants a simple action-to-policy map.

First configure the policies:

```csharp
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("Reports.Read", policy =>
        policy.RequireClaim("reports", "read"));

    options.AddPolicy("Reports.Export", policy =>
        policy.RequireClaim("reports", "export"));

    options.AddPolicy("Reports.Save", policy =>
        policy.RequireClaim("reports", "save"));

    options.AddPolicy("Reports.Administer", policy =>
        policy.RequireRole("ReportAdministrators"));
});
```

Then delegate from the callback:

```csharp
reports.UseAuthorization(async (request, cancellationToken) =>
{
    cancellationToken.ThrowIfCancellationRequested();

    var saved = request.Resource.SavedReport;
    var caller = request.User.FindFirstValue(ClaimTypes.NameIdentifier);
    var isOwner = saved is not null
        && caller is not null
        && string.Equals(saved.Owner, caller, StringComparison.Ordinal);
    var isPublic = saved is { IsGlobal: true } or { IsDefault: true };

    var policyName = request.Action switch
    {
        InteractiveReportAction.ReadSavedReport when isPublic || isOwner =>
            "Reports.Read",
        InteractiveReportAction.ReadSavedReport =>
            "Reports.Administer",

        InteractiveReportAction.UpdateSavedReport
            or InteractiveReportAction.DeleteSavedReport when isOwner =>
            "Reports.Save",
        InteractiveReportAction.UpdateSavedReport
            or InteractiveReportAction.DeleteSavedReport =>
            "Reports.Administer",

        InteractiveReportAction.PublishGlobalReport
            or InteractiveReportAction.SelectDefaultReport
            or InteractiveReportAction.ChangeSavedReportOwner
            or InteractiveReportAction.ListAllSavedReports
            or InteractiveReportAction.ListAuthorizationUsers
            or InteractiveReportAction.ManageAdministrators
            or InteractiveReportAction.DownloadReportDocument
            or InteractiveReportAction.UploadReportDocument =>
            "Reports.Administer",

        InteractiveReportAction.Export => "Reports.Export",
        InteractiveReportAction.CreateSavedReport => "Reports.Save",
        _ => "Reports.Read",
    };

    var authorization = request.RequestServices
        .GetRequiredService<IAuthorizationService>();

    var result = await authorization.AuthorizeAsync(
        request.User,
        request.Resource,
        policyName);

    return result.Succeeded;
});
```

Passing `request.Resource` is deliberate. Policies containing resource-aware handlers
can inspect `InteractiveReportAuthorizationResource`; claim-only and role-only policies
simply ignore it.

`IAuthorizationService.AuthorizeAsync` does not accept a cancellation token. Check the
callback token before calling it, and ensure policy handlers follow the application's
normal cancellation strategy.

Do not also call `UseAspNetCoreAuthorization()` merely because this callback resolves
`IAuthorizationService`. The callback already delegates to ASP.NET Core. Register the
native adapter as well only when both the named policy result and a separate native
`InteractiveReportAuthorizationRequirement` handler must approve. With this option the
`Reports.Administer` policy is also the natural value for
`InteractiveReport:AdministratorPolicy`, so one rule answers both questions.

## Administrators

Administrator authority is a single fact about a caller, decided once per request by
the first source that is implemented:

| Order | Source | Registration | Notes |
|---|---|---|---|
| 1 | Application callback | `reports.UseAdministrators((request, ct) => …)` | Receives the authenticated principal and the request services. Its answer is final. |
| 2 | Application policy | `InteractiveReport:AdministratorPolicy` | An ASP.NET Core policy name evaluated through `IAuthorizationService`. Used only when no callback is registered. |
| 3 | Built-in fallback | `InteractiveReport:Administrators` plus the administration center's list | Consulted only when neither application source exists. Configured entries are read-only; the database list is set as a whole from the administration page or `PUT {prefix}/admin/administrators`. |

```csharp
reports.UseAdministrators((request, cancellationToken) =>
    ValueTask.FromResult(request.User.IsInRole("ReportAdministrators")));
```

```json
{
  "InteractiveReport": {
    "AdministratorPolicy": "Reports.Administer"
  }
}
```

The rules that follow from this ordering:

- Declining is not implementing. An application that registers neither source leaves
  the fallback in charge; there is no abstain answer.
- While the application answers, the configured list and the database list are inert.
  The administration page hides its Administrators editor and `whoami` reports
  `administratorsManagedByApplication: true`. A callback that answers false for
  everyone therefore locks administration until the application changes its answer;
  the database list cannot rescue it.
- An unauthenticated caller is never an administrator and is never put to the
  application. Administration needs an identity for ownership and reassignment.
- Operation authorizers still see every administrator action and keep their veto. They
  narrow; they never grant.
- A callback or policy that throws is an infrastructure error, reported as a sanitized
  `500` with a trace id, not as a denial.

The canonical identity used by the fallback is resolved through the configured
`identityClaim`, then NameIdentifier, `sub`, and finally `Identity.Name`. Matching is
ordinal and case-sensitive; identity-provider subject values are treated as opaque
identifiers. Enable `InteractiveReport:WhoamiEnabled` to see the exact value while
bootstrapping.

The built-in `__saved-reports` definition, the listing behind the administration page,
is the one report that belongs to administrators: it is hidden from everyone else and
emits `ListAllSavedReports`.

The administration account lookup is part of the security surface. It performs the
same administrator check and emits `ListAuthorizationUsers` before it reads known
identities from configuration and storage or invokes `IInteractiveReportUserProvider`
or the `UseUserDirectory` callback. Lookup entries are account choices only; returning
an account does not authorize it. The Administrators editor emits `ManageAdministrators`
when it replaces the database list.

## Composition rules

The final decision is conjunctive across the Interactive Reports pipeline:

- Every action emitted by the server operation must pass.
- Every callback registered with `UseAuthorization` must return `true` for that action.
- If the native adapter is registered, its ASP.NET Core authorization result must
  succeed for that action.
- The built-in report, administrator, and saved-report rules must also pass.

Within the native adapter, ASP.NET Core retains its normal handler and requirement
semantics. Outside it, multiple Interactive Reports adapters use AND semantics.

This makes layered rules straightforward:

```csharp
reports
    .UseAuthorization(TenantBoundary)
    .UseAuthorization(LicenceBoundary)
    .UseAspNetCoreAuthorization();
```

In this example the tenant callback, licence callback, and native handler result must
all grant every emitted action.

## Denials, errors, and disclosure

Application-operation results are translated as follows:

| Condition | Result |
|---|---|
| Required authentication is absent | `401 Unauthorized` |
| Authenticated caller is denied a capability it already knows exists | `403 Forbidden` coded-error response |
| Denial concerns a report or saved report whose existence should be concealed | `404 Not Found` |
| Callback returns `false` | Expected denial using the applicable status above |
| Callback throws `InteractiveReportAuthorizationDeniedException` | Expected denial using the applicable status above |
| Request cancellation is observed | Cancellation propagates |
| Any other callback/native-adapter exception | Logged under `InteractiveReport.Authorization`; sanitized `500` with a trace id |

The message from `InteractiveReportAuthorizationDeniedException` is not sent to the
client. Authorization internals and resource existence remain protected. Use
application logs or an audit store for detailed reasons.

## Report-definition policies

A per-report policy is the declarative way to narrow an authenticated report to some
of its authenticated callers:

```json
{
  "InteractiveReport": {
    "Reports": {
      "orders": {
        "connection": "MainDb",
        "sql": "SELECT * FROM ORDERS",
        "authorization": {
          "policy": "MayAccessOrders"
        }
      }
    }
  }
}
```

The report policy runs before action authorization and receives the user through
ordinary `IAuthorizationService` policy evaluation. A failed authenticated policy is
returned as 404 to avoid disclosing the definition. `allowAnonymous: true` is the
explicit public opt-in and cannot be combined with a policy; an absent authorization
block requires authentication.

Naming a policy anywhere requires `builder.Services.AddAuthorization()`; the host fails
at startup otherwise. A policy the provider does not know at startup is only logged as
a warning, because a dynamic policy provider may resolve it later; if it is still
unknown when a request arrives, that request fails with a sanitized `500`.

Use report-definition policies for a broad dataset boundary and operation
authorization for distinctions such as query versus export, private save versus
publication, or per-resource administration.

## Packaged UI hints

The packaged UI is one optional API consumer. It uses hints to decide whether
administrative controls are worth offering, but neither the API nor authorization
depends on that client and the UI never treats a hint as permission.

- Schema responses include `authorization.mayRequestAdministration`.
- When the optional `whoami` endpoint is enabled, it includes `isAdministrator`,
  `administratorSource` (`application`, `policy`, `configuration`, `database`, or
  `none`), and `administratorsManagedByApplication`.
- `GET {prefix}/admin/administrators` includes `managedByApplication`, which the
  administration page uses to hide the Administrators editor.

These fields only control presentation. The UI may display a button that a
resource-specific callback later denies. Every protected endpoint evaluates the
concrete action and resource again on the server.

## Testing recommendations

Test through the mapped HTTP and GraphQL endpoints so the report-definition gate,
built-in access matrix, action mapping, and status translation are exercised together.
At minimum, cover:

- A public report read anonymously, and an authenticated report refused anonymously.
- A report policy admitting one caller and hiding the report from another.
- A private saved-report owner update.
- A non-owner read/update/delete attempt.
- Default selection and global publication.
- The administrator callback granting, and the fallback lists ignored while it is registered.
- The administrator policy granting when no callback is registered.
- The fallback list granting, with an operation authorizer still able to restrict.
- A multi-action request where one action is denied.
- Expected denial exception, cancellation, and unexpected exception behavior.
- Native handler success and absence of a successful handler.

The repository's end-to-end authorization coverage is in
`tests/InteractiveReport.AspNetCore.Tests/AuthorizationHttpTests.cs`,
`InteractiveReportAuthorizationHttpTests.cs`, and `GraphQLHttpTests.cs`.
