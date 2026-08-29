using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace InteractiveReport.AspNetCore;

/// <summary>
/// One optional logging sink for the whole Interactive Reports integration. The host
/// supplies it at registration or endpoint mapping; the package never creates a
/// provider, console sink, file, or other logging side effect of its own.
/// </summary>
internal sealed class InteractiveReportLogging
{
    private ILogger? _logger;

    internal ILogger? Logger => Volatile.Read(ref _logger);

    internal void Use(ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        Volatile.Write(ref _logger, logger);
    }

    /// <summary>
    /// A stable typed adapter lets singleton engine services be created before the
    /// route is mapped. It consults the current sink for every event, so mapping-time
    /// logger configuration still reaches those services.
    /// </summary>
    internal ILogger<T> For<T>() => new ForwardingLogger<T>(this);

    internal static async ValueTask<object?> LogRequest(
        EndpointFilterInvocationContext invocation,
        EndpointFilterDelegate next)
    {
        var context = invocation.HttpContext;
        var logger = context.RequestServices
            .GetRequiredService<InteractiveReportLogging>()
            .Logger;
        if (logger is null) return await next(invocation);

        var started = Stopwatch.GetTimestamp();
        logger.LogInformation(
            "Interactive Reports request {Method} {Path} started (traceId {TraceId})",
            context.Request.Method,
            context.Request.Path.Value,
            context.TraceIdentifier);

        try
        {
            var result = await next(invocation);
            var statusCode = result is IStatusCodeHttpResult { StatusCode: { } resultStatus }
                ? resultStatus
                : context.Response.StatusCode;
            logger.LogInformation(
                "Interactive Reports request {Method} {Path} completed with {StatusCode} in {ElapsedMs} ms (traceId {TraceId})",
                context.Request.Method,
                context.Request.Path.Value,
                statusCode,
                Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                context.TraceIdentifier);
            return result;
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            logger.LogInformation(
                "Interactive Reports request {Method} {Path} was cancelled after {ElapsedMs} ms (traceId {TraceId})",
                context.Request.Method,
                context.Request.Path.Value,
                Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                context.TraceIdentifier);
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Interactive Reports request {Method} {Path} failed after {ElapsedMs} ms (traceId {TraceId})",
                context.Request.Method,
                context.Request.Path.Value,
                Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                context.TraceIdentifier);
            throw;
        }
    }

    private sealed class ForwardingLogger<T>(InteractiveReportLogging owner) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
            => owner.Logger?.BeginScope(state);

        public bool IsEnabled(LogLevel logLevel)
            => owner.Logger?.IsEnabled(logLevel) == true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => owner.Logger?.Log(logLevel, eventId, state, exception, formatter);
    }
}
