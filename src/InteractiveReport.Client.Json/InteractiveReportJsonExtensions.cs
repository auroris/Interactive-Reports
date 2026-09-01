using InteractiveReport.AspNetCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace InteractiveReport.Client.Json;

/// <summary>Registers the JSON/HTTP adapter over the Interactive Reports server.</summary>
public static class InteractiveReportJsonExtensions
{
    public static IServiceCollection AddInteractiveReportJson(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return services;
    }
}
