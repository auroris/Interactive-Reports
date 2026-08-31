using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace InteractiveReport.AspNetCore;

/// <summary>
/// Holds one optional logging sink for the whole Interactive Reports integration. The host
/// supplies it at registration or endpoint mapping; the package never creates a
/// provider, console sink, file, or other logging side effect of its own.
/// </summary>
internal sealed class InteractiveReportLogging
{
    private ILogger? _logger;

    /// <summary>Gets the current host-supplied sink, or <see langword="null"/> when logging is disabled.</summary>
    internal ILogger? Logger => Volatile.Read(ref _logger);

    /// <summary>
    /// Replaces the current logging sink.
    /// </summary>
    /// <param name="logger">The host-provided logger that receives diagnostic events; <see langword="null"/> disables logging.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="logger"/> is <see langword="null"/>.</exception>
    internal void Use(ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        Volatile.Write(ref _logger, logger);
    }

    /// <summary>
    /// Creates a stable typed adapter so singleton engine services may be created before the route is
    /// mapped. It consults the current sink for every event, so mapping-time logger configuration still
    /// reaches those services.
    /// </summary>
    /// <typeparam name="T">The logging category type.</typeparam>
    /// <returns>A forwarding logger that consults the current sink for every call.</returns>
    internal ILogger<T> For<T>() => new ForwardingLogger<T>(this);

    /// <summary>
    /// Logs the start and outcome of one endpoint request around the next filter delegate.
    /// </summary>
    /// <param name="invocation">The current endpoint-filter invocation context.</param>
    /// <param name="next">The next request delegate in the middleware pipeline.</param>
    /// <returns>A task containing the downstream endpoint result.</returns>
    /// <remarks>Reads request and response metadata and emits structured events when a sink is configured.</remarks>
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
        /// <summary>
        /// Forwards scope creation to the current sink.
        /// </summary>
        /// <typeparam name="TState">The structured scope-state type.</typeparam>
        /// <param name="state">The structured scope state.</param>
        /// <returns>The sink's disposable scope, or <see langword="null"/> when no sink is configured.</returns>
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
            => owner.Logger?.BeginScope(state);

        /// <summary>
        /// Determines whether the requested log level is enabled for the ASP.NET Core transport.
        /// </summary>
        /// <param name="logLevel">The diagnostic severity whose enabled state is being queried.</param>
        /// <returns><see langword="true"/> when the requested log level is enabled; otherwise, <see langword="false"/>.</returns>
        public bool IsEnabled(LogLevel logLevel)
            => owner.Logger?.IsEnabled(logLevel) == true;

        /// <summary>
        /// Forwards one structured log event to the current sink.
        /// </summary>
        /// <typeparam name="TState">The structured event-state type.</typeparam>
        /// <param name="logLevel">The event severity.</param>
        /// <param name="eventId">The structured logging event identifier.</param>
        /// <param name="state">The structured event state.</param>
        /// <param name="exception">The exception associated with the event, if any.</param>
        /// <param name="formatter">The callback that formats state and exception as a message.</param>
        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => owner.Logger?.Log(logLevel, eventId, state, exception, formatter);
    }
}
