using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging;

namespace AetheriumForge.Core.Models;

/// <summary>
/// Registers model discovery and downloads.
/// </summary>
public static class ModelDownloadsServiceCollectionExtensions
{
    /// <summary>The attempt timeout of the manifest client.</summary>
    public static readonly TimeSpan ManifestAttemptTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// The attempt timeout of the download client. With <see cref="HttpCompletionOption.ResponseHeadersRead"/> it bounds the wait for the
    /// response headers; the body is streamed outside the resilience pipeline and only stops on cancellation or a network error.
    /// </summary>
    public static readonly TimeSpan DownloadAttemptTimeout = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Registers <see cref="ModelSourceOptions"/>, <see cref="ModelManifestClient"/> and <see cref="ModelDownloader"/> as typed HTTP clients
    /// with the standard resilience handler (retry with exponential backoff, attempt and total timeouts, circuit breaker), and
    /// <see cref="ModelLibrary"/>.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Adjusts the source options.</param>
    /// <returns><paramref name="services"/>.</returns>
    public static IServiceCollection AddModelDownloads(this IServiceCollection services, Action<ModelSourceOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var options = new ModelSourceOptions();
        configure?.Invoke(options);
        services.AddSingleton(options);

        services.AddHttpClient(nameof(ModelManifestClient), client => client.Timeout = Timeout.InfiniteTimeSpan)
            .AddTypedClient((http, provider) => new ModelManifestClient(http, options, provider.GetService<ILogger<ModelManifestClient>>()))
            .AddStandardResilienceHandler(resilience =>
            {
                resilience.AttemptTimeout.Timeout = ManifestAttemptTimeout;
                resilience.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(45);
                resilience.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(30);

                // A GitHub rate limit lasts up to an hour: fall back to the direct URL at once instead of retrying.
                resilience.Retry.ShouldHandle = args => ValueTask.FromResult(
                    args.Outcome.Result?.StatusCode is not (HttpStatusCode.TooManyRequests or HttpStatusCode.Forbidden)
                    && HttpClientResiliencePredicates.IsTransient(args.Outcome));
            });

        services.AddHttpClient(nameof(ModelDownloader), client => client.Timeout = Timeout.InfiniteTimeSpan)
            .AddTypedClient((http, provider) => new ModelDownloader(http, options.ModelsFolder, options, provider.GetService<ILogger<ModelDownloader>>()))
            .AddStandardResilienceHandler(resilience =>
            {
                resilience.AttemptTimeout.Timeout = DownloadAttemptTimeout;
                resilience.TotalRequestTimeout.Timeout = TimeSpan.FromMinutes(10);
                resilience.CircuitBreaker.SamplingDuration = TimeSpan.FromMinutes(5);
            });

        services.AddTransient(provider => new ModelLibrary(
            options.ModelsFolder,
            provider.GetRequiredService<ModelManifestClient>(),
            provider.GetRequiredService<ModelDownloader>(),
            provider.GetService<ILogger<ModelLibrary>>()));

        return services;
    }
}
