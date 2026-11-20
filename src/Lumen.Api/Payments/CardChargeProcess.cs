using System.Collections.Concurrent;
using Lumen.Api.Infrastructure.Data;
using Lumen.Api.Infrastructure.Telemetry;
using Lumen.Domain.Payments;
using Microsoft.EntityFrameworkCore;

namespace Lumen.Api.Payments;

/// <summary>
/// BR-PAY-04: runs card charges detached from the HTTP request that started them, so the Pay call answers at once
/// ("processing") and a slow gateway (card 0042, 20 s) is never cut short by the browser leaving or refreshing: the
/// page reads the outcome from the payment status endpoint. One charge per payment id at a time in this process. On
/// shutdown the host waits for the charges in flight; a charge whose worker died anyway (a crash) is reconciled with
/// the gateway by the payment window job.
/// </summary>
internal sealed class DetachedPaymentRunner(IServiceScopeFactory scopes, ILogger<DetachedPaymentRunner> logger) : IHostedService
{
    private readonly ConcurrentDictionary<Guid, Task> _running = new();

    /// <summary>A charge for this payment is running in this process right now.</summary>
    public bool IsRunning(Guid paymentId) => _running.ContainsKey(paymentId);

    /// <summary>Starts charging the card; returns at once. The card stays in memory for the charge only.</summary>
    public void StartCardCharge(Guid paymentId, CardNumber card) =>
        Start(paymentId, services => services.GetRequiredService<CardChargeProcess>().RunAsync(paymentId, card));

    /// <summary>Completes when every charge started so far has finished (tests, shutdown).</summary>
    public Task WhenIdleAsync() => Task.WhenAll(_running.Values);

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await WhenIdleAsync().WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("Shutting down with {Count} card charges still running; the payment window job reconciles them", _running.Count);
        }
    }

    private void Start(Guid paymentId, Func<IServiceProvider, Task> work)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_running.TryAdd(paymentId, done.Task))
        {
            logger.LogWarning("Payment {Payment} is already being charged in this process; not starting it twice", paymentId);
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await work(scope.ServiceProvider);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Charging payment {Payment} failed; the payment window job will reconcile it with the gateway", paymentId);
            }
            finally
            {
                _running.TryRemove(paymentId, out _);
                done.SetResult();
            }
        });
    }
}

/// <summary>
/// One card charge, start to finish, in its own scope: the gateway call with the payment id as idempotency key
/// (BR-PAY-01 (c)), the outcome recorded afterwards, and after a gateway error (not a decline) exactly one automatic
/// retry with the same payment id (BR-PAY-05). The amount is the payment's, which is the order total (BR-CHK-01).
/// </summary>
internal sealed class CardChargeProcess(
    LumenDbContext db,
    IPaymentGateway gateway,
    PaymentRecorder recorder,
    ILogger<CardChargeProcess> logger)
{
    public async Task RunAsync(Guid paymentId, CardNumber card)
    {
        // Detached on purpose: nothing cancels a charge once it has been sent (CancellationToken.None throughout).
        var charge = await db.Payments.AsNoTracking().Where(p => p.Id == paymentId)
            .Join(db.Orders, p => p.OrderId, o => o.Id, (p, o) => new ChargeTarget(p.Id, o.Number, p.Amount))
            .SingleAsync(CancellationToken.None);
        var status = await recorder.RecordAsync(paymentId, await ChargeAsync(charge, card, LumenTelemetry.FirstTry), CancellationToken.None);
        if (status == PaymentStatus.Error && await recorder.StartAutomaticRetryAsync(paymentId, CancellationToken.None))
        {
            logger.LogInformation("Payment {Payment}: gateway error, retrying once with the same payment id", paymentId);
            await recorder.RecordAsync(paymentId, await ChargeAsync(charge, card, LumenTelemetry.AutomaticRetry), CancellationToken.None);
        }
    }

    /// <summary>One gateway call, traced as its own span (card 0059: an Error first try, then the automatic retry).</summary>
    private async Task<GatewayChargeResult> ChargeAsync(ChargeTarget charge, CardNumber card, string attempt)
    {
        using var activity = LumenTelemetry.Source.StartActivity("card charge");
        activity?.SetTag(LumenTelemetry.OrderNumber, charge.OrderNumber)
            .SetTag(LumenTelemetry.PaymentId, charge.PaymentId.ToString())
            .SetTag(LumenTelemetry.Attempt, attempt);
        GatewayChargeResult result;
        try
        {
            result = await gateway.ChargeAsync(new GatewayChargeRequest(charge.PaymentId, card, charge.Amount), CancellationToken.None);
        }
        catch (Exception ex)
        {
            // Treated as a gateway error: the same payment id is safe to send again, the gateway answers it once.
            logger.LogWarning(ex, "Gateway call for payment {Payment} failed", charge.PaymentId);
            activity?.AddException(ex);
            result = GatewayChargeResult.Failure($"Gateway call failed: {ex.GetType().Name}");
        }

        LumenTelemetry.SetOutcome(activity, result.Status.ToString(), result.Status == GatewayChargeStatus.Error ? result.Error : null);
        return result;
    }

    private sealed record ChargeTarget(Guid PaymentId, string OrderNumber, decimal Amount);
}
