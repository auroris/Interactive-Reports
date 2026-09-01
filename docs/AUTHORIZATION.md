# Authorization

Interactive Reports uses the host application's ASP.NET Core identity. It does not
authenticate users, issue cookies or tokens, or maintain a second user directory.
Authentication middleware populates `HttpContext.User`; Interactive Reports passes
that `ClaimsPrincipal` to its authorization gates.

The mapped server API is the security boundary. The caller may be the packaged web
component, another application, a scheduled process, a hand-written HTTP client, or
the optional GraphQL adapter.

These controls make internet exposure possible; they do not make it the preferred
deployment. Keep the application on a trusted network when practical. If it must be
publicly reachable, use a dedicated reporting database or read replica and a
least-privileged read-only principal, never the primary production database.

Every published data and security-administration endpoint participates in application
operation authorization. The only exceptions are the opt-in `whoami` bootstrap
diagnostic and packaged HTML/CSS/JavaScript delivery. Those exceptions expose no report
data and grant no authority.

Authorization never depends on client UI state, navigation history, or how the request
was produced. For each requested operation the server asks one question:

```text
ClaimsPrincipal + Action + Resource -> allow or deny
```

Only an allowed operation proceeds to execution or persistence.

This guide covers the three supported application-operation integrations:

1. A direct Interactive Reports callback.
2. Native ASP.NET Core resource-based authorization handlers.
3. A callback that delegates each operation to a named ASP.NET Core policy.

All three receive the same action and resource vocabulary. Choose the integration
style that fits the host application. They can be combined, although every configured
adapter must then grant the operation.

## Authorization layers

Application-operation authorization is one layer in a larger security model. A
request may need to pass all of these gates:

1. Host endpoint conventions, such as
   `app.MapInteractiveReports(...).RequireAuthorization(...)`.
2. The report definition's `authorization` block.
3. Built-in saved-report ownership, publication, and read-only rules.
4. Every configured application-operation authorizer.
5. Server-resolved context parameters used for row-level constraints.

The operation authorizer does not replace report policies, ownership rules, feature
flags, configured-document immutability, or row-level security. It can further
restrict them. When no administrator identities are configured, it can also supply
the affirmative decision required for an administrator operation.

The usual ASP.NET Core setup still applies:

```csharp
builder.Services
    .AddAuthentication(/* host scheme */)
    .AddCookie(); // or JWT bearer, OIDC, an application-specific scheme, etc.

builder.Services.AddAuthorization();

var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();

app.MapInteractiveReports("/api/reports");
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
the application expresses and composes its decision.

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
| `Resource.Definition` | The mutable, typed saved-report definition for create, update, and document upload operations. Its metadata is effective rather than a sparse patch. |
| `RequestServices` | The current request service provider. Use it to resolve scoped ACL services or `IAuthorizationService`. It is available on the callback request, not on the native requirement. |

`Resource.Definition` is an
`InteractiveReport.AspNetCore.Definitions.InteractiveReportDefinition`. It exposes
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

## Typed definition inspection and mutation

The definition supplied to authorization is the same mutable object the endpoint later
validates and persists. An application can therefore inspect the full typed shape and
narrow a client request before granting it:

```csharp
using InteractiveReport.AspNetCore.Definitions;
using InteractiveReport.Core.Model;

reports.UseAuthorization((request, cancellationToken) =>
{
    cancellationToken.ThrowIfCancellationRequested();

    if (request.Action == InteractiveReportAction.CreateSavedReport
        && request.Resource.Definition is { } definition)
    {
        // This host never permits end users to publish directly. The private save may
        // still proceed if the rest of the rule grants CreateSavedReport.
        definition.Public = false;

        // The state is typed. No JsonElement traversal or second deserialization is
        // required.
        // This policy deliberately counts filters in every declared table,
        // including inactive alternatives, rather than only activeTable ancestry.
        var allDeclaredFilters = definition.State?.Tables?.Values
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
  evaluated first. All registered authorizers see the same definition instance.
- Publication and owner actions are derived from the effective definition after the
  base action passes. Setting `Public = false` during create therefore removes an
  unwanted publication request before the administrator boundary is evaluated.
- If an authorizer later adds a public, default-selection, or owner change, the server detects it
  and evaluates the corresponding administrator action before persistence.
- A denial at any point discards all mutations because nothing has yet been stored.
- After authorization, title/owner invariants and the submitted state's executable
  view are validated against the current dataset schema. The typed state is then
  serialized canonically. Unknown client JSON members are not copied into storage.

Assigning `Definition.State` marks `StateChanged` true. Mutating a nested state object
on create or on an update that already supplied state preserves its existing true
value. On an update with `StateChanged == false`, the stored JSON remains byte-for-byte
untouched. To replace it from authorization code, assign a new `ReportState` to
`Definition.State`.

This mutation surface is available through all three authorization integrations. A
direct callback is usually the clearest place for request normalization. Native
resource handlers and resource-aware named policies receive the same mutable resource.

Non-default user-document reads parse stored JSON only for response framing and send it
to the client without hydrating `InteractiveReportDefinition` or `ReportState`. Historical
user documents are therefore not rewritten or rejected merely because they are read.
Configured documents read their state from the configured file. If that file is missing,
the stale database identity is deleted and the original request returns 404. When the
missing document was the default, the server also creates a new synthetic default for
subsequent requests. Any database-backed default is repairable: invalid stored state is
regenerated from current appsettings without changing the database id. Configured file
bodies are never rewritten this way. If a present configured body fails processing, its
exception and identity are logged, its optimistic row is deleted, and the request returns
404. Its configuration remains authoritative, so the next synchronization recreates an
identity and retries it rather than substituting a synthetic default.

Query, LOV, and export are addressed by configured definition key. They authorize that
definition and execute the client-submitted document without reading the saved-report
store. The submitted document has no required ID or persisted provenance. A client copy
can therefore continue to execute after its source document's ownership or publication
changes, provided the caller still has access to the configured definition.

Authorization is expressed in facts rather than in how the caller reached an
endpoint. For saved reports, the relevant facts are available on the resource:

- `ReadSavedReport` is normally allowed when the report is default/global, the caller
  owns it, or the caller is an application administrator.
- `UpdateSavedReport` and `DeleteSavedReport` are normally allowed when the caller owns
  the report or is an application administrator. Publication does not remove owner
  rights over title/state or deletion.
- Changing global publication, default selection, or ownership emits a separate action, so those
  decisions do not have to be inferred from `UpdateSavedReport`.

The engine applies the same built-in facts, including configured/database report-user
grants and the union of configured/database administrators. The application authorizer
receives the operation facts so it can add restrictions or supply administrator
authority only when both built-in administrator sources are empty.

Authorization is centralized in `IReportAccessService`. Configuration
stores expose a lightweight name/authorization envelope, allowing authentication,
policy, administrator, and named-user gates to run before connection resolution and
saved-default hydration. Saved-report listing and normalized title-collision queries
occur only after that report-level gate succeeds. Definition-free security endpoints
use the same service before invoking their authorization store or user provider.

## Action reference

| Action | Request that emits it | Built-in notes |
|---|---|---|
| `ViewReport` | Schema for an ordinary report | Report-definition authentication and policy run first. |
| `Query` | Process a client document through HTTP, or execute a stored document through GraphQL | HTTP processing authorizes only the resolved report definition and never supplies original document metadata. GraphQL loads a stored document and supplies `SavedReport` metadata. |
| `Export` | CSV export | The report's `download` feature must also be enabled. Admin-list export emits `ListAllSavedReports` as well. |
| `ListSavedReports` | List visible saved reports for one report definition | Storage filters to the default, global, and caller-owned rows. |
| `ReadSavedReport` | Load one saved report, or execute it through GraphQL | Public, owner, and administrator access are distinguished from `SavedReport` metadata and the principal. |
| `CreateSavedReport` | Create a saved report | Requires an authenticated canonical owner and the `savedReports` feature. Receives the typed definition before publication actions are derived. |
| `UpdateSavedReport` | Update a saved report | Owner or administrator. Receives effective metadata and only client-authored replacement state. Global publication and default selection remain unchanged unless their separate actions also pass. Configured content remains read-only. |
| `DeleteSavedReport` | Delete a saved report | Owner or administrator. Configured rows remain undeletable even when authorized. |
| `PublishGlobalReport` | Effective definition changes public status | Emitted for both publishing and unpublishing after base-action mutation. Administrator action. |
| `SelectDefaultReport` | Effective definition selects a new family default | Administrator action. The new default becomes global and the previous default remains global. A configured default cannot be replaced through the API. |
| `ChangeSavedReportOwner` | Effective definition changes owner | Administrator action. |
| `ListAllSavedReports` | Schema/query/export of the built-in `__saved-reports` definition | Administrator action. Its export also emits `Export`. |
| `ListAuthorizationUsers` | Resolve the protected administration user directory | Administrator action. Directory entries are choices, not grants. |
| `ManageAuthorization` | List or change database administrators, report restrictions, and report-user grants | Administrator action. Configuration grants remain read-only. |
| `DownloadReportDocument` | Download the canonical admin JSON envelope | Administrator action. |
| `UploadReportDocument` | Validate and import an admin JSON envelope | Administrator action. Upload always creates a private user document; file publication metadata is ignored. |

One HTTP request can emit several actions. These examples assume authorization does
not first narrow the typed definition:

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
            or InteractiveReportAction.ManageAuthorization
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
that identity mapping instead of duplicating it in several callbacks.

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
            or InteractiveReportAction.ManageAuthorization
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
`InteractiveReportAuthorizationRequirement` handler must approve.

## Administrator resolution and fail-closed behavior

`InteractiveReport:Administrators` supplies source-controlled administrators. Database
administrators created through the administration center are additive. The canonical
identity is resolved through the configured `identityClaim`, then NameIdentifier,
`sub`, and finally `Identity.Name`. Matching is ordinal and case-sensitive; identity
provider subject values are treated as opaque identifiers.

Operations that require administrator authority use this decision table:

| Effective configured/database administrator list | Caller | Application authorizer | Result |
|---|---|---|---|
| Nonempty | Listed | None | Allowed by the built-in administrator boundary. |
| Nonempty | Listed | All grant | Allowed. |
| Nonempty | Listed | Any denies | Denied. |
| Nonempty | Not listed | Any | Denied before application operation authorization; an explicit list cannot be bypassed. |
| Empty | Authenticated | All grant | Allowed for that concrete action and resource. |
| Empty | Authenticated | Missing or any denies | Denied. |
| Any | Unauthenticated | Any | `401 Unauthorized`. |

This fallback is action-specific. An application can grant
`SelectDefaultReport` while denying `DeleteSavedReport`; it does not have to promote
the principal to a permanent administrator identity.

A definition with `authorization.administratorsOnly: true` uses the same model. With
a nonempty effective administrator list, only listed callers reach operation
authorization. With both sources empty, the concrete operation must be affirmatively
granted by an application authorizer. The built-in `__saved-reports` definition emits the explicit
`ListAllSavedReports` action. For an application-defined administrators-only report,
the authorizer should map its `Resource.ReportName` to the application's administrator
rule. The request carries action and resource facts, not the engine's intermediate
reason for asking.

The optional administration user-directory endpoint is part of the security surface.
It performs the same administrator check and emits `ListAuthorizationUsers` before it
resolves or invokes `IInteractiveReportUserProvider`. Directory entries are account
choices only; returning an account does not authorize it. The separate Authorization
editor emits `ManageAuthorization` when it turns a choice into a database grant.

Ordinary operations retain the built-in behavior when no application authorizer is
registered. Registering either `UseAuthorization` or
`UseAspNetCoreAuthorization` opts every operation into application authorization, not
only administrator operations. Therefore:

- A callback replacing the administrator list should evaluate the action against the
  resource facts: public/owner/administrator for reads, owner/administrator for update
  and delete, and administrator for publication, ownership, list-all, and document
  administration and authorization-management actions.
- A native handler must also succeed ordinary requirements that the application wants
  to permit.
- Registering the native adapter with no successful handler denies ordinary and
  administrator operations.

## Composition rules

The final decision is conjunctive across the Interactive Reports pipeline:

- Every action emitted by the server operation must pass.
- Every callback registered with `UseAuthorization` must return `true` for that action.
- If the native adapter is registered, its ASP.NET Core authorization result must
  succeed for that action.
- The built-in report and saved-report rules must also pass.

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

## Built-in named-user report restrictions

A report can opt into exact identity grants in configuration:

```json
"authorization": {
  "restricted": true,
  "users": [ "orders-user-id", "finance-user-id" ]
}
```

The administration center can independently store a restriction marker and report-user
grants in `IR_REPORT_AUTHORIZATION`. Effective restriction is configuration OR database;
effective users are the union. This permits source-controlled baseline grants plus
operator-managed additions. Administrators are not implicit report users. A denied
authenticated identity receives 404, and an unauthenticated identity receives 401.

The database layer exists only when `InteractiveReport:SavedReports:DataSource` or
`:Connection` is explicitly configured. It shares that target and the optional
`SavedReports:TablePrefix`; installing the package alone creates no file or table.

`allowAnonymous`, `administratorsOnly`, and named-user restriction are mutually
exclusive access modes. `allowAnonymous` cannot be combined with a policy: it is the
explicit report-specific opt-out from the default authenticated boundary. A policy can
stack on a named-user restriction and remains an additional requirement. Application
authorizers also remain restrictive; a built-in grant never bypasses them.

## Report-definition policies remain available

Operation authorization does not replace the existing per-report policy:

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
explicit anonymous opt-in; an absent authorization block requires authentication.

Use report-definition policies for a broad dataset boundary and operation
authorization for distinctions such as query versus export, private save versus
publication, or per-resource administration.

## Packaged UI hints

The packaged UI is one optional API consumer. It uses hints to decide whether
administrative controls are worth offering, but neither the API nor authorization
depends on that client and the UI never treats a hint as permission.

- Schema responses include `authorization.mayRequestAdministration`.
- When the optional `whoami` endpoint is enabled, it includes
  `isAdministrator`, `administratorListConfigured`, and
  `applicationAuthorizationConfigured`, plus source-specific
  `configuredAdministrator` and `databaseAdministrator` flags.

These fields only control presentation. The UI may display a button that a
resource-specific callback later denies. Every protected endpoint evaluates the
concrete action and resource again on the server.

## Testing recommendations

Test through the mapped HTTP and GraphQL endpoints so the report-definition gate,
built-in access matrix, action mapping, and status translation are exercised together.
At minimum, cover:

- An ordinary allowed and denied query.
- A private saved-report owner update.
- A non-owner read/update/delete attempt.
- Default selection and global publication.
- Empty administrator list with an affirmative decision.
- Empty administrator list with no authorizer.
- Nonempty administrator list with an unlisted caller.
- A configured administrator restricted by the application authorizer.
- A multi-action request where one action is denied.
- Expected denial exception, cancellation, and unexpected exception behavior.
- Native handler success and absence of a successful handler.

The repository's end-to-end authorization coverage is in
`tests/InteractiveReport.AspNetCore.Tests/InteractiveReportAuthorizationHttpTests.cs`
and `GraphQLHttpTests.cs`.
