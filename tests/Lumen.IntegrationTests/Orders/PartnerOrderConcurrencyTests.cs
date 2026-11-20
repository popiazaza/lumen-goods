using Lumen.Api.Orders;
using Lumen.Domain.Catalog;
using Lumen.Domain.Orders;
using Lumen.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using static Lumen.IntegrationTests.Orders.OrderingTestData;

namespace Lumen.IntegrationTests.Orders;

/// <summary>
/// The partner order job under load and concurrency (BR-ORD-01): two job instances never send the same partner order
/// twice, a slow partner delays only its own orders, and one run stays within its batch and time limits.
/// </summary>
public sealed class PartnerOrderConcurrencyTests(LumenApiFactory factory)
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    [Fact]
    [Trait("BR", "BR-ORD-01")]
    public async Task BR_ORD_01_two_job_instances_running_at_once_send_and_process_each_partner_order_once()
    {
        var harbor = new FakeHarborOrders();
        var kestrel = new FakeKestrelOrders();

        // A partner that answers after a moment, so both instances are busy at the same time.
        harbor.OnRequest = _ => Task.Delay(20, Ct);
        kestrel.OnRequest = _ => Task.Delay(20, Ct);
        await using var first = OrderJobHost.Start(factory, harbor: harbor, kestrel: kestrel);
        await using var second = OrderJobHost.Start(factory, harbor: harbor, kestrel: kestrel);
        var orders = new List<Order>();
        for (var i = 0; i < 6; i++)
        {
            orders.Add(await factory.PaidOrderAsync(Now, LineSpec.Harbor(10m + i), LineSpec.Kestrel(5m + i)));
        }

        var reports = await Task.WhenAll(first.RunAsync([.. orders]), second.RunAsync([.. orders]));

        reports.Sum(r => r.Steps.Count).Should().Be(12, "each partner order was processed by exactly one instance");
        reports.Sum(r => r.Failed).Should().Be(0);
        foreach (var order in orders)
        {
            harbor.CallsFor($"{order.Number}-1").Should().ContainSingle();
            kestrel.CallsFor($"{order.Number}-2").Should().ContainSingle();
            var stored = await factory.ReloadAsync(order);
            stored.Line(1).PartnerOrder!.Should().BeEquivalentTo(new { Status = PartnerOrderStatus.Confirmed, Attempts = 1 });
            stored.Line(2).PartnerOrder!.Should().BeEquivalentTo(new { Status = PartnerOrderStatus.Accepted, Attempts = 1 });
        }
    }

    [Fact]
    [Trait("BR", "BR-ORD-01")]
    public async Task BR_ORD_01_a_slow_partner_delays_only_its_own_orders()
    {
        await using var host = OrderJobHost.Start(factory);
        var harborOrder = await factory.PaidOrderAsync(Now, LineSpec.Harbor(49.90m));
        var kestrelOrder = await factory.PaidOrderAsync(Now, LineSpec.Kestrel(16.22m));
        var kestrelCalled = new TaskCompletionSource();
        var kestrelAnswers = new TaskCompletionSource();
        host.Kestrel.OnRequest = async _ =>
        {
            kestrelCalled.TrySetResult();
            await kestrelAnswers.Task;
        };

        // Kestrel hangs; a full run (every partner) and Harbor's own next run both get Harbor's work done meanwhile.
        var fullRun = host.RunAsync(harborOrder, kestrelOrder);
        await kestrelCalled.Task.WaitAsync(Patience, Ct);
        await WaitUntilAsync(async () => (await factory.ReloadAsync(harborOrder)).Line(1).Status == OrderLineStatus.Confirmed);
        var harborAgain = await host.RunPartnerAsync(ProductSources.Harbor, harborOrder, kestrelOrder).WaitAsync(Patience, Ct);

        harborAgain.Steps.Should().BeEmpty("Harbor's work was already done while Kestrel was still hanging");
        fullRun.IsCompleted.Should().BeFalse("Kestrel's call is still hanging");
        (await factory.ReloadAsync(kestrelOrder)).Line(1).Status.Should().Be(OrderLineStatus.AwaitingPartner);

        kestrelAnswers.SetResult();
        var report = await fullRun.WaitAsync(Patience, Ct);
        report.Steps.Should().BeEquivalentTo([PartnerOrderStep.Confirmed, PartnerOrderStep.Accepted]);
    }

    [Fact]
    [Trait("BR", "BR-ORD-01")]
    public async Task BR_ORD_01_one_run_for_a_partner_stops_at_its_time_budget_and_leaves_the_rest_unclaimed()
    {
        await using var host = OrderJobHost.Start(factory, builder => builder.UseSetting("PartnerOrders:RunBudget", "00:00:25"));

        // Every Kestrel call takes 10 seconds (on the fake clock).
        host.Kestrel.OnRequest = _ =>
        {
            host.Clock.Advance(TimeSpan.FromSeconds(10));
            return Task.CompletedTask;
        };
        var orders = new List<Order>();
        for (var i = 0; i < 4; i++)
        {
            orders.Add(await factory.PaidOrderAsync(Now, LineSpec.Kestrel(5m + i)));
        }

        var report = await host.RunPartnerAsync(ProductSources.Kestrel, [.. orders]);

        report.Steps.Should().HaveCount(3, "calls start at 0, 10 and 20 seconds; at 30 the 25-second budget is spent");
        var untouched = new List<PartnerOrder>();
        foreach (var order in orders)
        {
            untouched.Add((await factory.ReloadAsync(order)).Line(1).PartnerOrder!);
        }

        untouched.Should().ContainSingle(p => p.Status == PartnerOrderStatus.Pending && p.Attempts == 0,
            "nothing is claimed that the run will not get to, so nothing waits for a claim to time out");

        // The next run sends it straight away (it is due first) and then polls the accepted ones while its budget lasts.
        (await host.RunPartnerAsync(ProductSources.Kestrel, [.. orders])).Steps[0].Should().Be(PartnerOrderStep.Accepted);
        foreach (var order in orders)
        {
            (await factory.ReloadAsync(order)).Line(1).PartnerOrder!.Status.Should().Be(PartnerOrderStatus.Accepted);
        }
    }

    [Fact]
    [Trait("BR", "BR-ORD-01")]
    public async Task BR_ORD_01_one_run_for_a_partner_handles_at_most_its_batch()
    {
        await using var host = OrderJobHost.Start(factory, builder => builder.UseSetting("PartnerOrders:BatchSize", "2"));
        var orders = new List<Order>();
        for (var i = 0; i < 3; i++)
        {
            orders.Add(await factory.PaidOrderAsync(Now, LineSpec.Harbor(10m + i)));
        }

        (await host.RunPartnerAsync(ProductSources.Harbor, [.. orders])).Steps.Should().HaveCount(2);
        (await host.RunPartnerAsync(ProductSources.Harbor, [.. orders])).Steps.Should().HaveCount(1);
        host.Harbor.Calls.Count(c => orders.Any(o => c.Reference == $"{o.Number}-1")).Should().Be(3);
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow + Patience;
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("The condition did not become true in time.");
            }

            await Task.Delay(50, Ct);
        }
    }
}
