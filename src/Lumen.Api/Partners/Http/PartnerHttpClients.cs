using System.Net;
using Lumen.Api.Partners.Ports;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using Polly;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace Lumen.Api.Partners;

internal static class PartnerHttpClients
{
    /// <summary>
    /// A named client for one partner: base address from configuration, no automatic redirects, and a resilience
    /// pipeline built from the partner's <see cref="PartnerResilienceOptions"/>, outermost first:
    /// total timeout → retry (GET only; honours Retry-After; 5xx, 408, 429, timeouts, connection errors) →
    /// circuit breaker → <paramref name="beforeAttempt"/> (Harbor's shared rate limiter) → attempt timeout.
    /// The rate limiter sits inside the retry, so every retry also spends a token, and outside the attempt timeout,
    /// so waiting for a token does not count against the partner.
    /// </summary>
    public static IHttpClientBuilder AddPartnerHttpClient<TOptions>(
        this IServiceCollection services,
        string name,
        Action<ResiliencePipelineBuilder<HttpResponseMessage>, IServiceProvider>? beforeAttempt = null)
        where TOptions : PartnerOptions
    {
        var builder = services
            .AddHttpClient(name, (sp, client) =>
            {
                var options = sp.GetRequiredService<IOptions<TOptions>>().Value;
                client.BaseAddress = options.BaseUri;

                // The pipeline owns timeouts; this only backstops reading a buffered body after the headers arrived.
                client.Timeout = options.Resilience.TotalTimeout + TimeSpan.FromSeconds(30);
                client.MaxResponseContentBufferSize = 16 * 1024 * 1024;
            })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                // A redirect would carry the partner key to wherever it points; treat 3xx as an answer instead.
                AllowAutoRedirect = false,
                AutomaticDecompression = DecompressionMethods.All,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                ConnectTimeout = TimeSpan.FromSeconds(10),
            });

        builder.AddResilienceHandler($"{name}-pipeline", (pipeline, context) =>
        {
            var options = context.ServiceProvider.GetRequiredService<IOptions<TOptions>>().Value.Resilience;

            pipeline.AddTimeout(options.TotalTimeout);

            var retry = new HttpRetryStrategyOptions
            {
                MaxRetryAttempts = options.MaxRetries,
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                Delay = options.RetryBaseDelay,
                ShouldRetryAfterHeader = true,
            };

            // Partner order POSTs must never be repeated by a transport retry; their idempotency is handled by the saga.
            retry.DisableForUnsafeHttpMethods();
            pipeline.AddRetry(retry);

            pipeline.AddCircuitBreaker(new HttpCircuitBreakerStrategyOptions
            {
                FailureRatio = options.BreakerFailureRatio,
                MinimumThroughput = options.BreakerMinimumThroughput,
                SamplingDuration = options.BreakerSamplingDuration,
                BreakDuration = options.BreakerBreakDuration,
            });

            beforeAttempt?.Invoke(pipeline, context.ServiceProvider);
            pipeline.AddTimeout(options.AttemptTimeout);
        });

        return builder;
    }

    /// <summary>
    /// Sends a request and turns transport failures (timeouts, open circuit, connection errors) into a
    /// <see cref="PartnerException"/> with a message fit for the admin page. Statuses are left to the caller.
    /// </summary>
    public static async Task<HttpResponseMessage> SendToPartnerAsync(
        this HttpClient client,
        HttpRequestMessage request,
        HttpCompletionOption completion,
        string partnerName,
        CancellationToken cancellationToken)
    {
        var what = $"{partnerName} {request.Method} {Describe(request.RequestUri)}";
        try
        {
            return await client.SendAsync(request, completion, cancellationToken);
        }
        catch (TimeoutRejectedException ex)
        {
            throw new PartnerException($"{what} timed out.", null, ex);
        }
        catch (BrokenCircuitException ex)
        {
            throw new PartnerException($"{what} not attempted: too many recent failures (circuit open).", null, ex);
        }
        catch (Polly.RateLimiting.RateLimiterRejectedException ex)
        {
            throw new PartnerException($"{what} not attempted: the client-side rate limit queue is full.", null, ex);
        }
        catch (HttpRequestException ex)
        {
            throw new PartnerException($"{what} failed: {ex.Message}", (int?)ex.StatusCode, ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new PartnerException($"{what} timed out.", null, ex);
        }
    }

    /// <summary>Path and query only: base URLs can carry user info, and logs need no host.</summary>
    public static string Describe(Uri? uri) => uri is null ? "(no url)" : uri.IsAbsoluteUri ? uri.PathAndQuery : uri.OriginalString;

    /// <summary>Reads at most <paramref name="maxChars"/> characters of an error body, for messages.</summary>
    public static async Task<string> ReadSnippetAsync(HttpResponseMessage response, int maxChars, CancellationToken cancellationToken)
    {
        try
        {
            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            text = text.Trim();
            return text.Length <= maxChars ? text : text[..maxChars] + "...";
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException)
        {
            return "";
        }
    }
}
