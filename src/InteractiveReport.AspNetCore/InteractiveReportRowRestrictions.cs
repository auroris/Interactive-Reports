using System.Security.Claims;
using InteractiveReport.Core.Execution;
using InteractiveReport.Core.Identity;
using InteractiveReport.Core.Model;
using Microsoft.Extensions.DependencyInjection;

namespace InteractiveReport.AspNetCore;

/// <summary>One row-access decision for a report whose SQL contains {{RowRestriction}}.</summary>
public sealed record InteractiveReportRowRestrictionRequest
{
    /// <summary>The current principal. Public reports may receive an anonymous principal.</summary>
    public required ClaimsPrincipal User { get; init; }
    /// <summary>The canonical report identity, or null for anonymous callers or a missing identity claim.</summary>
    public required string? UserId { get; init; }
    /// <summary>The canonical configured report key, independent of saved-report titles.</summary>
    public required string ReportName { get; init; }
    /// <summary>The current scope for resolving application permission services.</summary>
    public required IServiceProvider RequestServices { get; init; }
}

/// <summary>
/// Supplies a row-access decision before data reads. Only SQL containing an executable
/// {{RowRestriction}} slot invokes this callback. The decision is shared by all database
/// statements in that operation; it is never cached across requests.
/// </summary>
public delegate ValueTask<RowRestriction> InteractiveReportRowRestrictionCallback(
    InteractiveReportRowRestrictionRequest request,
    CancellationToken cancellationToken);

/// <summary>An immutable application decision, separate from ordinary operation authorization.</summary>
public sealed class RowRestriction
{
    internal enum DecisionKind { NotApplicable, Unrestricted, Predicate, Deny }
    internal DecisionKind Kind { get; }
    internal string? Expression { get; }
    internal IReadOnlyList<object?> Values { get; }

    private RowRestriction(DecisionKind kind, string? expression = null, object?[]? values = null)
    {
        Kind = kind;
        Expression = expression;
        Values = Array.AsReadOnly(values is null ? [] : (object?[])values.Clone());
    }

    /// <summary>Contributes no decision. If every callback abstains, a marked report fails before execution.</summary>
    public static RowRestriction NotApplicable { get; } = new(DecisionKind.NotApplicable);
    /// <summary>Explicitly allows the configured dataset; it cannot undo another callback's predicate.</summary>
    public static RowRestriction Unrestricted { get; } = new(DecisionKind.Unrestricted);
    /// <summary>Refuses the operation, including for an anonymous caller or an administrator.</summary>
    public static RowRestriction Deny { get; } = new(DecisionKind.Deny);

    /// <summary>
    /// Supplies a trusted SQL predicate without WHERE. Each unquoted ? binds one value;
    /// ?? represents a literal question mark outside quotes. Values are never interpolated.
    /// Use native SQL identifiers/quoting and the aliases visible at the configured slot.
    /// </summary>
    /// <example><code>RowRestriction.Where("p.CG = ?", 0)</code></example>
    public static RowRestriction Where(string expression, params object?[] values)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expression);
        ArgumentNullException.ThrowIfNull(values);
        return new(DecisionKind.Predicate, expression, values);
    }
}

internal sealed record RegisteredRowRestriction(InteractiveReportRowRestrictionCallback Callback);

/// <summary>Resolves the opt-in application decision without changing any shared definition.</summary>
internal static class ReportRowRestrictions
{
    internal static async Task<RowRestriction> Resolve(
        ReportDefinition definition,
        InteractiveReportRequestContext context,
        string? identityClaim,
        CancellationToken ct)
    {
        var request = new InteractiveReportRowRestrictionRequest
        {
            User = context.User,
            UserId = ReportIdentity.Resolve(context.User, identityClaim),
            ReportName = definition.Name,
            RequestServices = context.RequestServices,
        };
        var decided = false;
        var predicates = new List<string>();
        var values = new List<object?>();
        foreach (var registration in context.RequestServices.GetServices<RegisteredRowRestriction>())
        {
            ct.ThrowIfCancellationRequested();
            var decision = await registration.Callback(request, ct)
                ?? throw new InvalidOperationException($"Report '{definition.Name}': a row-restriction callback returned null.");
            ct.ThrowIfCancellationRequested();
            switch (decision.Kind)
            {
                case RowRestriction.DecisionKind.Deny:
                    throw new InteractiveReportAuthorizationDeniedException();
                case RowRestriction.DecisionKind.Unrestricted:
                    decided = true;
                    break;
                case RowRestriction.DecisionKind.Predicate:
                    // Validate each callback's bindings independently, so one callback's surplus
                    // values cannot accidentally fill another callback's missing placeholders.
                    _ = ReportSqlTemplate.Bind(definition, decision.Expression!, decision.Values,
                        new Dictionary<string, object?>());
                    decided = true;
                    predicates.Add("(\n" + decision.Expression + "\n)");
                    values.AddRange(decision.Values);
                    break;
            }
        }
        if (!decided)
            throw new InvalidOperationException(
                $"Report '{definition.Name}': SQL contains {ReportSqlTemplate.Marker}, but no callback supplied a row-access decision. "
                + "Register UseRowRestrictions and return Where, Unrestricted, or Deny for this report.");
        return predicates.Count == 0
            ? RowRestriction.Unrestricted
            : RowRestriction.Where(string.Join(" AND ", predicates), values.ToArray());
    }
}
