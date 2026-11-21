using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Lumen.Api.Infrastructure.Data;
using Lumen.Api.Partners.Sync;
using Lumen.Api.Payments;
using Lumen.Domain.Orders;
using Lumen.Domain.Payments;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Lumen.Api.Infrastructure.Telemetry;

/// <summary>
/// OpenTelemetry for the API. Traces: requests, HTTP calls (partners, images), SQL (Npgsql) and Lumen's own spans.
/// Metrics: requests, HTTP calls, the runtime and Lumen's counters and gauges. All of it, logs included, goes out over
/// OTLP only when <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> is set (docker compose's <c>observability</c> profile: the Aspire
/// dashboard; in Azure, a collector or Azure Monitor). Without it no exporter is added and nothing leaves the process;
/// tracing and the gauges' database reads are then not set up at all.
/// </summary>
internal static class TelemetryExtensions
{
    public static IServiceCollection AddLumenTelemetry(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<TelemetryOptions>()
            .Bind(configuration.GetSection(TelemetryOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddSingleton<LumenMetrics>();
        var telemetry = services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService("lumen-api"))
            .WithMetrics(metrics => metrics
                .AddMeter(LumenTelemetry.Name)
                .AddInstrumentation(sp => sp.GetRequiredService<LumenMetrics>())
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation());

        if (!string.IsNullOrWhiteSpace(configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
        {
            // Only with an exporter: a tracer provider records every request, HTTP call and SQL command it listens to,
            // which costs on each of them even when no span ever leaves the process.
            telemetry.WithTracing(tracing => tracing
                .SetSampler(new ParentBasedSampler(new SkipUnparentedClientSpans()))
                .AddSource(LumenTelemetry.Name)
                // The compose health check calls /health every 3 s; its spans would be noise.
                .AddAspNetCoreInstrumentation(options => options.Filter = context => !context.Request.Path.StartsWithSegments("/health"))
                .AddHttpClientInstrumentation()
                // Npgsql 10 traces every command through its own "Npgsql" ActivitySource; no extra package needed.
                .AddSource("Npgsql"));

            // Traces, metrics and logs. Protocol, headers and intervals follow the standard OTEL_EXPORTER_OTLP_* variables.
            telemetry.UseOtlpExporter();
            services.AddHostedService<GaugeRefresher>();
        }

        return services;
    }

    /// <summary>
    /// For spans that start a trace, drops client spans: the background jobs poll the database every few seconds and
    /// the catalogue sync fetches partner pages, and thousands of one-span traces would bury the ones worth reading.
    /// Spans inside a request or a Lumen span are all kept (the parent decides), and metrics are not affected.
    /// </summary>
    private sealed class SkipUnparentedClientSpans : Sampler
    {
        public override SamplingResult ShouldSample(in SamplingParameters samplingParameters) =>
            new(samplingParameters.Kind == ActivityKind.Client ? SamplingDecision.Drop : SamplingDecision.RecordAndSample);
    }
}

/// <summary>Lumen's ActivitySource and meter name, and the attribute names its spans and metrics share.</summary>
internal static class LumenTelemetry
{
    public const string Name = "Lumen";

    public const string OrderNumber = "lumen.order_number";
    public const string PaymentId = "lumen.payment_id";

    /// <summary>Which gateway call of a card charge it is: <see cref="FirstTry"/> or <see cref="AutomaticRetry"/> (BR-PAY-05).</summary>
    public const string Attempt = "lumen.attempt";

    public const string Partner = "lumen.partner";
    public const string ExternalRef = "lumen.external_ref";
    public const string Method = "lumen.method";
    public const string Status = "lumen.status";
    public const string Outcome = "lumen.outcome";

    public const string FirstTry = "first_try";
    public const string AutomaticRetry = "automatic_retry";

    public static readonly ActivitySource Source = new(Name);

    /// <summary>Records what came of a span's work; an <paramref name="error"/> marks the span failed.</summary>
    public static void SetOutcome(Activity? activity, string outcome, string? error = null)
    {
        activity?.SetTag(Outcome, outcome);
        if (error is not null)
        {
            activity?.SetStatus(ActivityStatusCode.Error, error);
        }
    }
}

/// <summary>
/// Lumen's own metrics, on the "Lumen" meter from <see cref="IMeterFactory"/> (so each host, and each test host, has its
/// own). Counters are added once the outcome is committed. The gauges report the values <see cref="RefreshGaugesAsync"/>
/// last read (a few small indexed queries, every <see cref="TelemetryOptions.GaugeRefreshInterval"/>), never on a request
/// and never while metrics are collected: a slow or unreachable database would otherwise hold up every metric.
/// </summary>
internal sealed class LumenMetrics
{
    private static readonly PartnerOrderStatus[] Undecided = [PartnerOrderStatus.Pending, PartnerOrderStatus.Submitted, PartnerOrderStatus.Accepted];
    private static readonly RefundStatus[] Unpaid = [RefundStatus.Pending, RefundStatus.Failed];

    private readonly IServiceScopeFactory _scopes;
    private readonly PartnerRegistry _partners;
    private readonly TimeProvider _time;
    private readonly IOptions<TelemetryOptions> _options;
    private readonly ILogger<LumenMetrics> _logger;
    private readonly Counter<long> _payments;
    private readonly Counter<long> _partnerOrders;
    private readonly Counter<long> _refunds;

    /// <summary>What the gauges report; null (nothing reported) until the first refresh, and after a failed one.</summary>
    private volatile GaugeValues? _gauges;

    public LumenMetrics(
        IMeterFactory meters, IServiceScopeFactory scopes, PartnerRegistry partners, TimeProvider time, IOptions<TelemetryOptions> options,
        ILogger<LumenMetrics> logger)
    {
        (_scopes, _partners, _time, _options, _logger) = (scopes, partners, time, options, logger);
        var meter = meters.Create(LumenTelemetry.Name);
        _payments = meter.CreateCounter<long>("lumen.payments", "{payment}",
            "Payments that reached an outcome (Succeeded, Declined, Failed or Expired), by outcome.");
        _partnerOrders = meter.CreateCounter<long>("lumen.partner_orders", "{call}",
            "Partner order sends and polls, by partner and what Lumen did with the answer.");
        _refunds = meter.CreateCounter<long>("lumen.refunds", "{refund}",
            "Refund payouts, by payment method and outcome (Succeeded, or Failed and retried later).");
        meter.CreateObservableGauge("lumen.partner_orders.waiting", ObserveWaitingPartnerOrders, "{partner_order}",
            "Partner orders the partner has not decided yet (Pending, Submitted or Accepted), by partner.");
        meter.CreateObservableGauge("lumen.refunds.pending", ObservePendingRefunds, "{refund}",
            "Refunds recorded but not paid out yet (Pending, or Failed and waiting for a retry), by status.");
        meter.CreateObservableGauge("lumen.partner_sync.age", ObserveSyncAge, "s",
            "Time since the partner's catalogue last synced successfully, by partner.");
    }

    /// <summary>Counts a payment whose status, once committed, moved from <paramref name="before"/> to an outcome.</summary>
    public void PaymentChanged(PaymentStatus before, PaymentStatus after)
    {
        if (before != after && after is PaymentStatus.Succeeded or PaymentStatus.Declined or PaymentStatus.Failed or PaymentStatus.Expired)
        {
            _payments.Add(1, new KeyValuePair<string, object?>(LumenTelemetry.Outcome, after.ToString()));
        }
    }

    public void PartnerOrderCalled(string partner, string outcome) =>
        _partnerOrders.Add(1, new KeyValuePair<string, object?>(LumenTelemetry.Partner, partner), new KeyValuePair<string, object?>(LumenTelemetry.Outcome, outcome));

    public void RefundFinished(PaymentMethod method, RefundStatus outcome) =>
        _refunds.Add(1, new KeyValuePair<string, object?>(LumenTelemetry.Method, method.ToString()), new KeyValuePair<string, object?>(LumenTelemetry.Outcome, outcome.ToString()));

    /// <summary>
    /// Reads the gauges' values from the database, within <see cref="TelemetryOptions.GaugeQueryTimeout"/>. A failed or
    /// timed-out read is logged and leaves the gauges reporting nothing until the next one succeeds, rather than values
    /// nobody can tell are stale.
    /// </summary>
    public async Task RefreshGaugesAsync(CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(_options.Value.GaugeQueryTimeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            await using var scope = _scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<LumenDbContext>();
            var waiting = await db.PartnerOrders.Where(p => Undecided.Contains(p.Status))
                .GroupBy(p => p.Source)
                .Select(g => new { Partner = g.Key, Count = g.LongCount() })
                .ToDictionaryAsync(c => c.Partner, c => c.Count, linked.Token);
            var refunds = await db.Refunds.Where(r => Unpaid.Contains(r.Status))
                .GroupBy(r => r.Status)
                .Select(g => new { Status = g.Key, Count = g.LongCount() })
                .ToDictionaryAsync(c => c.Status, c => c.Count, linked.Token);
            var synced = await db.PartnerSyncStates.Where(s => s.LastSuccessUtc != null)
                .Select(s => new { s.Partner, LastSuccessUtc = s.LastSuccessUtc!.Value })
                .ToDictionaryAsync(s => s.Partner, s => s.LastSuccessUtc, linked.Token);
            _gauges = new GaugeValues(waiting, refunds, synced);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _gauges = null;
            _logger.LogWarning(ex, "Could not read the Lumen gauges within {Timeout}; they report nothing until the next read succeeds",
                _options.Value.GaugeQueryTimeout);
        }
    }

    // Every partner and status reports, 0 included, so an emptied queue shows 0 rather than its last value.
    private IEnumerable<Measurement<long>> ObserveWaitingPartnerOrders() => _gauges is { } gauges
        ? _partners.All.Select(p => new Measurement<long>(
            gauges.WaitingPartnerOrders.GetValueOrDefault(p.Code), new KeyValuePair<string, object?>(LumenTelemetry.Partner, p.Code)))
        : [];

    private IEnumerable<Measurement<long>> ObservePendingRefunds() => _gauges is { } gauges
        ? Unpaid.Select(s => new Measurement<long>(
            gauges.PendingRefunds.GetValueOrDefault(s), new KeyValuePair<string, object?>(LumenTelemetry.Status, s.ToString())))
        : [];

    // Taken at collection time, so the age keeps growing between refreshes as it does between syncs.
    private IEnumerable<Measurement<double>> ObserveSyncAge()
    {
        if (_gauges is not { } gauges)
        {
            return [];
        }

        var now = _time.GetUtcNow();
        return gauges.LastSyncedUtc.Select(s => new Measurement<double>(
            (now - s.Value).TotalSeconds, new KeyValuePair<string, object?>(LumenTelemetry.Partner, s.Key)));
    }

    private sealed record GaugeValues(
        Dictionary<string, long> WaitingPartnerOrders, Dictionary<RefundStatus, long> PendingRefunds, Dictionary<string, DateTimeOffset> LastSyncedUtc);
}

/// <summary>
/// Refreshes <see cref="LumenMetrics"/>' gauges every <see cref="TelemetryOptions.GaugeRefreshInterval"/>. Registered only
/// when metrics are exported (<c>OTEL_EXPORTER_OTLP_ENDPOINT</c>): otherwise nobody reads them.
/// </summary>
internal sealed class GaugeRefresher(LumenMetrics metrics, IOptions<TelemetryOptions> options, TimeProvider time) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        using var timer = new PeriodicTimer(options.Value.GaugeRefreshInterval, time);
        try
        {
            do
            {
                await metrics.RefreshGaugesAsync(stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
    }
}

/// <summary>Telemetry settings (section <c>Telemetry</c>).</summary>
public sealed class TelemetryOptions
{
    public const string SectionName = "Telemetry";

    /// <summary>How often the gauges' values are read from the database (only while metrics are exported).</summary>
    [Range(typeof(TimeSpan), "00:00:01", "01:00:00")]
    public TimeSpan GaugeRefreshInterval { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>How long one read of the gauges may take before it is abandoned until the next.</summary>
    [Range(typeof(TimeSpan), "00:00:01", "00:01:00")]
    public TimeSpan GaugeQueryTimeout { get; init; } = TimeSpan.FromSeconds(5);
}
