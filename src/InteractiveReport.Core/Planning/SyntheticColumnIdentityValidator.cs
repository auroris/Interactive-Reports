using System.Text.RegularExpressions;
using InteractiveReport.Core.Model;

namespace InteractiveReport.Core.Planning;

/// <summary>
/// Enforces the document-wide identity namespace shared by authored computed columns
/// and Group/Pivot metrics. This pass is deliberately schema-independent: an identity
/// remains reserved even when its declaration is disabled, lives in a dormant sibling,
/// or is removed from an intermediate relation by a later shape.
/// </summary>
internal static partial class SyntheticColumnIdentityValidator
{
    /// <summary>
    /// Determines whether an identifier may be assigned to an authored synthetic column.
    /// </summary>
    /// <param name="id">The authored identifier to test.</param>
    /// <returns><see langword="true"/> when the identifier is valid for an authored node; otherwise, <see langword="false"/>.</returns>
    internal static bool IsValidAuthoredId(string id)
        => AuthoredIdPattern().IsMatch(id);

    /// <summary>
    /// Collects every computed-column and group/pivot metric id, then validates the shared namespace in deterministic order.
    /// </summary>
    /// <param name="document">The complete report-state document whose table graph is inspected.</param>
    /// <returns>Path-specific errors for malformed or duplicate authored synthetic ids.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="document"/> is <see langword="null"/>.</exception>
    public static List<ValidationError> Collect(ReportState document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var errors = new List<ValidationError>();
        var declarations = new List<IdentityDeclaration>();
        var firstById = new Dictionary<string, IdentityDeclaration>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var (tableId, table) in document.Tables ?? [])
        {
            if (table?.Composables is null) continue;

            for (var composableIndex = 0;
                 composableIndex < table.Composables.Count;
                 composableIndex++)
            {
                var composable = table.Composables[composableIndex];
                if (composable is null) continue;

                var composablePath = $"tables.{tableId}.composables[{composableIndex}]";
                var kind = composable.Kind?.Trim();
                if (string.Equals(kind, "group", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(kind, "pivot", StringComparison.OrdinalIgnoreCase))
                {
                    for (var ruleIndex = 0;
                         ruleIndex < (composable.Values?.Count ?? 0);
                        ruleIndex++)
                    {
                        var rule = composable.Values![ruleIndex];
                        Collect(
                            rule?.Id,
                            tableId,
                            0,
                            kind!,
                            composableIndex,
                            ruleIndex,
                            $"{composablePath}.values[{ruleIndex}].id");
                    }
                }
                else if (string.Equals(kind, "compute", StringComparison.OrdinalIgnoreCase))
                {
                    for (var ruleIndex = 0;
                         ruleIndex < (composable.Computed?.Count ?? 0);
                         ruleIndex++)
                    {
                        var rule = composable.Computed![ruleIndex];
                        Collect(
                            rule?.Id,
                            tableId,
                            1,
                            kind!,
                            composableIndex,
                            ruleIndex,
                            $"{composablePath}.computed[{ruleIndex}].id");
                    }
                }
            }
        }

        // Invariant: map order and composable storage order have no meaning. Shape metrics own
        // their identities before Computed within a table, matching the canonical phase
        // schedule; all remaining choices use stable syntax keys only.
        foreach (var declaration in declarations
                     .OrderBy(value => value.TableId, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(value => value.TableId, StringComparer.Ordinal)
                     .ThenBy(value => value.Phase)
                     .ThenBy(value => value.Kind, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(value => value.Kind, StringComparer.Ordinal)
                     .ThenBy(value => value.Id, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(value => value.Id, StringComparer.Ordinal)
                     .ThenBy(value => value.ComposableIndex)
                     .ThenBy(value => value.RuleIndex))
            Register(declaration);

        return errors;

        // Accepts one authored identifier and its declaration coordinates, then appends a
        // normalized declaration for the later validation pass. Null identifiers are ignored;
        // the method returns no value.
        void Collect(
            string? candidate,
            string tableId,
            int phase,
            string kind,
            int composableIndex,
            int ruleIndex,
            string path)
        {
            // Cache policy: null is already reported by the structural validator. Blank
            // authored values are declarations too: keep them in this schema-independent pass
            // so a dormant table with a populated schema cache cannot evade irN rules.
            if (candidate is null) return;
            var id = candidate;

            declarations.Add(new IdentityDeclaration(
                id,
                tableId,
                phase,
                kind,
                composableIndex,
                ruleIndex,
                path));
        }

        // Accepts one collected declaration and records it as the first use of its identifier.
        // Invalid or duplicate declarations append validation errors instead. It returns no
        // value and mutates only the enclosing validation accumulators.
        void Register(IdentityDeclaration declaration)
        {
            if (!IsValidAuthoredId(declaration.Id))
            {
                errors.Add(new ValidationError(
                    declaration.Path,
                    $"synthetic column id '{declaration.Id}' must use the canonical irN namespace, such as ir1"));
                return;
            }

            if (firstById.TryGetValue(declaration.Id, out var first))
            {
                errors.Add(new ValidationError(
                    declaration.Path,
                    $"synthetic column id '{declaration.Id}' is already declared at {first.Path}; "
                    + "computed columns and Group/Pivot metrics share one document-wide namespace"));
                return;
            }

            firstById.Add(declaration.Id, declaration);
        }
    }

    private sealed record IdentityDeclaration(
        string Id,
        string TableId,
        int Phase,
        string Kind,
        int ComposableIndex,
        int RuleIndex,
        string Path);

    /// <summary>
    /// Returns the compiled expression that accepts the canonical <c>irN</c> authored-id namespace.
    /// </summary>
    /// <returns>The compiled regular expression.</returns>
    [GeneratedRegex(@"^ir[1-9]\d*$")]
    private static partial Regex AuthoredIdPattern();
}
