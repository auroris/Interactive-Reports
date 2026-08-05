namespace InteractiveReport.Core.Model;

/// <summary>
/// Validation failures are the one error class that is precise and verbose to the client:
/// they reference only what the client already sent. Everything else is sanitized.
/// </summary>
public sealed class ReportValidationException : Exception
{
    public IReadOnlyList<ValidationError> Errors { get; }

    public ReportValidationException(IReadOnlyList<ValidationError> errors)
        : base($"Report state validation failed: {string.Join("; ", errors.Select(e => $"{e.Path}: {e.Message}"))}")
    {
        Errors = errors;
    }
}

public sealed record ValidationError(string Path, string Message);
