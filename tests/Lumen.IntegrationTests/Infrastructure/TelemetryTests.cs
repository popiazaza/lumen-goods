using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Lumen.Api.Infrastructure.Telemetry;
using Lumen.Domain.Payments;
using Lumen.IntegrationTests.Checkout;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Lumen.IntegrationTests.Infrastructure;

/// <summary>
/// Lumen's own spans and metrics, read in process the way an exporter reads them. Spans come from one static
/// ActivitySource shared by every test host, so they are picked by payment id; metrics come from this class's host
/// only (its <see cref="IMeterFactory"/>), so another test's payment never counts here. The test hosts set no OTLP
/// endpoint, so they have no gauge refresher: the tests refresh the gauges themselves.
/// </summary>
public sealed class TelemetryTests(CheckoutHost host) : IClassFixture<CheckoutHost>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Card_0059_traces_the_failed_first_try_and_the_automatic_retry_in_one_trace_and_counts_one_paid_payment()
    {
        var shopper = await host.NewShopperAsync();
        await shopper.AddToCartAsync(await host.OwnProductAsync());
        var order = await shopper.PlaceOrderAsync();
        var spans = new ConcurrentQueue<Activity>();
        using var tracing = ListenToSpans(spans);
        var measured = new ConcurrentQueue<Measured>();
        using var metrics = ListenToLumenMetrics(measured);

        var payment = await shopper.PayByCardAndWaitAsync(order.Number, "4242 0000 0000 0059");

        payment.Status.Should().Be(PaymentStatus.Succeeded);
        var calls = spans
            .Where(s => s.Source.Name == LumenTelemetry.Name && Equals(s.GetTagItem(LumenTelemetry.PaymentId), payment.PaymentId.ToString()))
            .OrderBy(s => s.StartTimeUtc)
            .ToList();
        calls.Select(s => (s.OperationName, s.GetTagItem(LumenTelemetry.Attempt), s.GetTagItem(LumenTelemetry.Outcome), s.Status)).Should().Equal(
            ("card charge", LumenTelemetry.FirstTry, "Error", ActivityStatusCode.Error),
            ("card charge", LumenTelemetry.AutomaticRetry, "Approved", ActivityStatusCode.Unset));
        calls.Should().AllSatisfy(s => s.GetTagItem(LumenTelemetry.OrderNumber).Should().Be(order.Number));
        calls.Select(s => s.TraceId).Distinct().Should().ContainSingle("both gateway calls belong to the Pay request's trace");
        spans.Where(s => s.Source.Name == "Npgsql").Select(s => s.ParentSpanId)
            .Should().Contain(calls.Select(s => s.SpanId), "the mock gateway's SQL nests in each call");

        measured.Where(m => m.Instrument == "lumen.payments").Select(m => (m.Value, m.Tags[LumenTelemetry.Outcome]))
            .Should().Equal((1d, "Succeeded"));
    }

    [Fact]
    public async Task The_gauges_report_waiting_partner_orders_per_partner_and_unpaid_refunds_per_status_as_last_refreshed()
    {
        var shopper = await host.NewShopperAsync();
        await shopper.AddToCartAsync(await host.HarborProductAsync());
        var order = await shopper.PlaceOrderAsync();
        (await shopper.PayByCardAndWaitAsync(order.Number, "4111 1111 1111 1111")).Status.Should().Be(PaymentStatus.Succeeded);
        var measured = new ConcurrentQueue<Measured>();
        using var metrics = ListenToLumenMetrics(measured);

        metrics.RecordObservableInstruments();
        measured.Should().BeEmpty("collecting never queries the database, and nothing has refreshed the gauges yet");

        await host.App.Services.GetRequiredService<LumenMetrics>().RefreshGaugesAsync(Ct);
        metrics.RecordObservableInstruments();

        var waiting = measured.Where(m => m.Instrument == "lumen.partner_orders.waiting")
            .ToDictionary(m => (string)m.Tags[LumenTelemetry.Partner]!, m => m.Value);
        waiting.Keys.Should().BeEquivalentTo("harbor", "kestrel");
        waiting["harbor"].Should().BeGreaterThanOrEqualTo(1, "this order's Harbor line waits for the partner order job, which is off in tests");
        measured.Where(m => m.Instrument == "lumen.refunds.pending").Select(m => m.Tags[LumenTelemetry.Status])
            .Should().BeEquivalentTo(["Pending", "Failed"]);
    }

    [Fact]
    public async Task A_gauge_refresh_the_database_does_not_answer_gives_up_after_its_timeout_and_the_gauges_report_nothing()
    {
        var gauges = host.App.Services.GetRequiredService<LumenMetrics>();
        var timeout = host.App.Services.GetRequiredService<IOptions<TelemetryOptions>>().Value.GaugeQueryTimeout;
        var measured = new ConcurrentQueue<Measured>();
        using var metrics = ListenToLumenMetrics(measured);
        await gauges.RefreshGaugesAsync(Ct);
        metrics.RecordObservableInstruments();
        measured.Should().NotBeEmpty();

        var hanging = new TaskCompletionSource();
        host.Queries.BeforeNext(sql => sql.Contains("FROM \"PartnerOrders\"", StringComparison.Ordinal), async cancellationToken =>
        {
            hanging.SetResult();
            await new TaskCompletionSource().Task.WaitAsync(cancellationToken); // a database that never answers
        });
        var refresh = gauges.RefreshGaugesAsync(Ct);
        await hanging.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        refresh.IsCompleted.Should().BeFalse();
        host.Clock.Advance(timeout);
        await refresh.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        measured.Clear();
        metrics.RecordObservableInstruments();
        measured.Should().BeEmpty("values the last refresh could not read are not reported as current");
    }

    [Fact]
    public void The_gauge_refresher_is_set_up_only_when_an_OTLP_endpoint_is_configured()
    {
        host.App.Services.GetServices<IHostedService>().OfType<GaugeRefresher>().Should().BeEmpty();

        // Registrations only: a started tracer provider listens process-wide, to every other test host's requests too.
        var exporting = new ServiceCollection().AddLumenTelemetry(new ConfigurationBuilder()
            .AddInMemoryCollection([new("OTEL_EXPORTER_OTLP_ENDPOINT", "http://otel-collector.test:4317")])
            .Build());

        exporting.Should().ContainSingle(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(GaugeRefresher));
    }

    private static ActivityListener ListenToSpans(ConcurrentQueue<Activity> into)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name is LumenTelemetry.Name or "Npgsql",
            Sample = (ref _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = into.Enqueue,
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    private MeterListener ListenToLumenMetrics(ConcurrentQueue<Measured> into)
    {
        // Resolving it creates its instruments; the listener then sees them published.
        _ = host.App.Services.GetRequiredService<LumenMetrics>();
        var factory = host.App.Services.GetRequiredService<IMeterFactory>();
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == LumenTelemetry.Name && ReferenceEquals(instrument.Meter.Scope, factory))
                {
                    l.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => into.Enqueue(new Measured(instrument.Name, value, tags.ToArray())));
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => into.Enqueue(new Measured(instrument.Name, value, tags.ToArray())));
        listener.Start();
        return listener;
    }

    private sealed record Measured(string Instrument, double Value, KeyValuePair<string, object?>[] TagList)
    {
        public Dictionary<string, object?> Tags { get; } = TagList.ToDictionary(t => t.Key, t => t.Value);
    }
}
