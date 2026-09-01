using GraphQL;
using InteractiveReport.AspNetCore;
using InteractiveReport.Core.Model;
using InteractiveReport.Core.Validation;
using Microsoft.AspNetCore.Http;

namespace InteractiveReport.Client.GraphQL;

/// <summary>
/// GraphQL client workflow: discover the configurations and saved documents a caller may see,
/// then load an authorized report document, apply GraphQL view arguments to a detached copy,
/// and submit it through the ordinary server query boundary.
/// </summary>
internal sealed class InteractiveReportGraphQLExecutor(
    IHttpContextAccessor httpContextAccessor,
    IInteractiveReportServer server)
{
    /// <summary>
    /// Lists the appsettings report configurations the caller may view.
    /// </summary>
    /// <param name="ct">Cancels definition resolution and authorization.</param>
    /// <returns>The visible configurations, ordered by title.</returns>
    /// <exception cref="ExecutionError">Thrown when every configuration is denied or authorization infrastructure fails.</exception>
    public async Task<IReadOnlyList<ReportConfigurationSummary>> Configurations(CancellationToken ct)
    {
        var listed = await server.ListConfigurations(Context(), ct);
        if (listed.Failure is not null) throw Failure(listed.Failure);
        return listed.Value!;
    }

    /// <summary>
    /// Lists the saved documents the caller may load for one configured report.
    /// </summary>
    /// <param name="reportName">The appsettings report configuration name.</param>
    /// <param name="ct">Cancels authorization, document synchronization, and persistence reads.</param>
    /// <returns>The caller's visible documents; administrators receive the complete family.</returns>
    /// <exception cref="ExecutionError">Thrown when the report is absent, hidden, or its store is unreachable.</exception>
    public async Task<IReadOnlyList<SavedReportSummary>> SavedReports(
        string reportName,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(reportName))
            throw Error("report must be a non-empty configuration name.", "BAD_USER_INPUT");

        var listed = await server.ListSavedReports(reportName, Context(), ct);
        if (listed.Failure is not null) throw Failure(listed.Failure);
        return listed.Value!;
    }

    /// <summary>
    /// Executes one saved report after applying the requested view arguments.
    /// </summary>
    /// <param name="id">The saved-report document id.</param>
    /// <param name="page">An optional 1-based page override.</param>
    /// <param name="pageSize">An optional page-size override; zero selects the engine's unpaged mode.</param>
    /// <param name="search">Optional replacement toolbar search text; blank clears the stored search.</param>
    /// <param name="sorts">Optional replacement ordering for the active table; empty clears the stored ordering.</param>
    /// <param name="ct">Cancels document retrieval and execution.</param>
    /// <returns>The executed report result.</returns>
    /// <exception cref="ExecutionError">Thrown for out-of-range arguments and for every server failure.</exception>
    public async Task<ReportResult?> Query(
        long id,
        int? page,
        int? pageSize,
        string? search,
        IReadOnlyList<SortRule>? sorts,
        CancellationToken ct)
    {
        if (id < 1) throw Error("id must be a positive saved-report id.", "BAD_USER_INPUT");
        if (page is < 1) throw Error("page must be at least 1.", "BAD_USER_INPUT");
        if (pageSize is < 0) throw Error("pageSize cannot be negative.", "BAD_USER_INPUT");
        if (sorts is not null && sorts.Any(sort => string.IsNullOrWhiteSpace(sort.Col)))
            throw Error("every sort entry needs a non-empty col.", "BAD_USER_INPUT");

        var context = Context();
        var loaded = await server.LoadDocument(id, context, ct);
        if (loaded.Failure is not null) throw Failure(loaded.Failure);
        var document = loaded.Value!;
        var state = ReportStateResolver.Resolve(defaults: null, document.State);
        if (page.HasValue || pageSize.HasValue)
        {
            state.Page ??= new PageRequest();
            if (page.HasValue) state.Page.Index = page.Value;
            if (pageSize.HasValue) state.Page.Size = pageSize.Value;
        }
        if (search is not null) state.Search = search;
        if (sorts is not null) ApplySorts(state, sorts);

        var queried = await server.Query(document with { State = state }, context, ct);
        if (queried.Failure is not null) throw Failure(queried.Failure);
        return queried.Value;
    }

    /// <summary>
    /// Replaces the active table's terminal ordering, matching how the packaged client's sort
    /// editor writes one sort composable per table. A table that never ordered its rows receives
    /// a new composable at the end of its own list; array position carries no execution
    /// semantics, so the engine still orders it by kind.
    /// </summary>
    /// <param name="state">The detached state to mutate.</param>
    /// <param name="sorts">The replacement sort rules; an empty list clears the ordering.</param>
    /// <exception cref="ExecutionError">Thrown when the document declares no table to order.</exception>
    private static void ApplySorts(ReportState state, IReadOnlyList<SortRule> sorts)
    {
        // Ordering is a document declaration, so it needs a document table to live in. Every
        // default, configured, and packaged-client document has one; a hand-authored state with
        // no tables is refused rather than restructured, because a synthesized table would
        // replace — not extend — the definition's own default tables during execution.
        var table = ActiveTable(state)
            ?? throw Error(
                "sort requires a saved report whose document declares the table to order.",
                "BAD_USER_INPUT");

        table.Composables ??= [];
        var terminal = table.Composables.LastOrDefault(composable => IsKind(composable, "sort"));
        if (terminal is null)
        {
            terminal = new TableComposable { Kind = "sort" };
            table.Composables.Add(terminal);
        }
        terminal.Sorts = [.. sorts];
    }

    /// <summary>
    /// Resolves the table the document orders. This mirrors the packaged client's document
    /// normalization: the selected table when it resolves, otherwise the document's sole table.
    /// </summary>
    /// <param name="state">The detached state whose active table is required.</param>
    /// <returns>The active table, or <see langword="null"/> when the document declares none.</returns>
    private static ReportTable? ActiveTable(ReportState state)
    {
        if (state.Tables is not { Count: > 0 } tables) return null;
        if (!string.IsNullOrWhiteSpace(state.ActiveTable)
            && tables.TryGetValue(state.ActiveTable.Trim(), out var active))
            return active;
        return tables.Count == 1 ? tables.Values.First() : null;
    }

    /// <summary>
    /// Determines whether a composable declares the supplied operation token.
    /// </summary>
    /// <param name="composable">The composable to classify.</param>
    /// <param name="kind">The case-insensitive operation token.</param>
    /// <returns><see langword="true"/> when the composable declares that kind; otherwise, <see langword="false"/>.</returns>
    private static bool IsKind(TableComposable composable, string kind)
        => string.Equals(composable.Kind?.Trim(), kind, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Projects the ambient HTTP exchange onto the transport-neutral request context.
    /// </summary>
    /// <returns>The request context consumed by the server boundary.</returns>
    /// <exception cref="InvalidOperationException">Thrown when no HTTP request is in flight.</exception>
    private InteractiveReportRequestContext Context()
    {
        var http = httpContextAccessor.HttpContext
            ?? throw new InvalidOperationException("GraphQL report execution requires an active HTTP request.");
        return new InteractiveReportRequestContext
        {
            User = http.User,
            RequestServices = http.RequestServices,
            TraceIdentifier = http.TraceIdentifier,
        };
    }

    /// <summary>
    /// Translates a transport-neutral server failure into a GraphQL execution error. The
    /// classification becomes the GraphQL code; the message is the same English fallback text the
    /// REST API returns for that stable code, so both transports describe one failure identically.
    /// </summary>
    /// <param name="failure">The classified server failure.</param>
    /// <returns>The execution error to throw from a resolver.</returns>
    private static ExecutionError Failure(InteractiveReportFailure failure)
    {
        var code = failure.Kind switch
        {
            InteractiveReportFailureKind.Unauthenticated => "UNAUTHENTICATED",
            InteractiveReportFailureKind.Forbidden => "FORBIDDEN",
            InteractiveReportFailureKind.NotFound => "NOT_FOUND",
            InteractiveReportFailureKind.Invalid => "REPORT_VALIDATION_FAILED",
            _ => "INTERNAL_SERVER_ERROR",
        };
        var error = Error(InteractiveReportErrorCatalog.Find(failure.Code).Description, code);
        error.AddExtension("reportErrorCode", failure.Code);
        if (failure.Details is not null) error.AddExtension("details", failure.Details);
        if (failure.Validation is not null) error.AddExtension("validation", failure.Validation);
        if (failure.TraceIdentifier is not null) error.AddExtension("traceId", failure.TraceIdentifier);
        return error;
    }

    /// <summary>
    /// Creates a coded GraphQL execution error.
    /// </summary>
    /// <param name="message">The public error message.</param>
    /// <param name="code">The stable GraphQL error classification.</param>
    /// <returns>The execution error to throw from a resolver.</returns>
    private static ExecutionError Error(string message, string code)
        => new(message) { Code = code };
}
