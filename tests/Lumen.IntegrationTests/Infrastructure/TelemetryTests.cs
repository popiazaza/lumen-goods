using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Lumen.Api.Infrastructure.Telemetry;
using Lumen.Domain.Payments;
using Lumen.IntegrationTests.Checkout;
using Microsoft.Extensions.DependencyInjection;

namespace Lumen.IntegrationTests.Infrastructure;

/// <summary>
/// Lumen's own spans and metrics, read in process the way an exporter reads them. Spans come from one static
/// ActivitySource shared by every test host, so they are picked by payment id; metrics come from this class's host
/// only (its <see cref="IMeterFactory"/>), so another test's payment never counts here.
/// </summary>
public sealed class TelemetryTests(CheckoutHost host) : IClassFixture<CheckoutHost>
{
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
    public async Task The_gauges_report_waiting_partner_orders_per_partner_and_unpaid_refunds_per_status()
    {
        var shopper = await host.NewShopperAsync();
        await shopper.AddToCartAsync(await host.HarborProductAsync());
        var order = await shopper.PlaceOrderAsync();
        (await shopper.PayByCardAndWaitAsync(order.Number, "4111 1111 1111 1111")).Status.Should().Be(PaymentStatus.Succeeded);
        var measured = new ConcurrentQueue<Measured>();
        using var metrics = ListenToLumenMetrics(measured);

        metrics.RecordObservableInstruments();

        var waiting = measured.Where(m => m.Instrument == "lumen.partner_orders.waiting")
            .ToDictionary(m => (string)m.Tags[LumenTelemetry.Partner]!, m => m.Value);
        waiting.Keys.Should().BeEquivalentTo("harbor", "kestrel");
        waiting["harbor"].Should().BeGreaterThanOrEqualTo(1, "this order's Harbor line waits for the partner order job, which is off in tests");
        measured.Where(m => m.Instrument == "lumen.refunds.pending").Select(m => m.Tags[LumenTelemetry.Status])
            .Should().BeEquivalentTo(["Pending", "Failed"]);
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
