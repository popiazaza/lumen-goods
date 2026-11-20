using Lumen.Api.Orders;
using Lumen.Domain.Orders;
using Lumen.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static Lumen.IntegrationTests.Orders.OrderingTestData;

namespace Lumen.IntegrationTests.Orders;

/// <summary>
/// The partner order job as production runs it: no order filter, so it claims every due partner order in the database.
/// It runs alone, after the parallel tests, so it cannot take partner orders another test is still driving.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class UnscopedPartnerOrderJobCollection
{
    public const string Name = "Partner order job, unscoped (whole database)";
}

[Collection(UnscopedPartnerOrderJobCollection.Name)]
public sealed class PartnerOrderJobUnscopedTests(LumenApiFactory factory)
{
    [Fact]
    [Trait("BR", "BR-ORD-01")]
    public async Task BR_ORD_01_a_run_claims_due_partner_orders_of_paid_orders_including_ones_never_scheduled()
    {
        // Leftovers of earlier tests may be due as well and go to these fakes; a large batch keeps this order in the run.
        await using var host = OrderJobHost.Start(factory, builder => builder.UseSetting("PartnerOrders:BatchSize", "10000"));
        var kestrel = LineSpec.Kestrel(16.22m);
        var order = await factory.PaidOrderAsync(Now, kestrel);
        host.Kestrel.Decisions[kestrel.Item] = ("CONFIRMED", null);

        // A Pending partner order written without a due time is due at once (and claimed first).
        await using (var db = factory.NewContext())
        {
            var lineId = order.Line(1).Id;
            await db.PartnerOrders.Where(p => p.OrderLineId == lineId)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.NextAttemptUtc, (DateTimeOffset?)null), Ct);
        }

        await host.App.Services.GetRequiredService<PartnerOrderJob>().RunOnceAsync(Ct);

        host.Kestrel.CallsFor($"{order.Number}-1").Should().ContainSingle().Which.Method.Should().Be("POST");
        var partnerOrder = (await factory.ReloadAsync(order)).Line(1).PartnerOrder!;
        partnerOrder.Status.Should().Be(PartnerOrderStatus.Accepted);
        partnerOrder.SubmittedUtc.Should().Be(Now);
    }
}
