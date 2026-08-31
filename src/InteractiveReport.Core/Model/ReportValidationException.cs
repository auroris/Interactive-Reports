namespace InteractiveReport.Core.Model;

/// <summary>
/// Carries path-specific report-state validation failures to a transport boundary. These details may be
/// reference only what the client already sent. Everything else is sanitized at the
/// transport boundary.
/// </summary>
public sealed class ReportValidationException : Exception
{
    /// <summary>Gets the validation failures in discovery order.</summary>
    public IReadOnlyList<ValidationError> Errors { get; }

    /// <summary>
    /// Creates an exception whose message summarizes the supplied path-specific failures.
    /// </summary>
    /// <param name="errors">The validation failures to expose through <see cref="Errors"/>.</param>
    public ReportValidationException(IReadOnlyList<ValidationError> errors)
        : base($"Report state validation failed: {string.Join("; ", errors.Select(e => $"{e.Path}: {e.Message}"))}")
    {
        Errors = errors;
    }
}

/// <summary>Associates one validation message with its exact path in the report-state document.</summary>
/// <param name="Path">The report-state path containing the invalid value.</param>
/// <param name="Message">The client-safe explanation of the failed rule.</param>
public sealed record ValidationError(string Path, string Message);
