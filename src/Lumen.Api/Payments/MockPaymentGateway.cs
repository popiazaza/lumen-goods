using Lumen.Api.Infrastructure.Data;
using Lumen.Domain.Payments;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Lumen.Api.Payments;

/// <summary>
/// The in-process mock card gateway (BR-PAY-02). It plays an external system: it keeps its own records
/// (<see cref="MockGatewayCharge"/>, <see cref="MockGatewayRefund"/>) in its own short database calls, never inside a
/// Lumen transaction, and knows nothing of orders. Every call is keyed by the caller's id, so a repeat returns the
/// first result (BR-PAY-01 (c)). The test cards (<see cref="CardBehaviours"/>):
/// - 0018 / 0026: declined (insufficient funds / reported stolen); 0034: needs the one-time code (BR-PAY-03);
/// - 0042: answers after <see cref="PaymentOptions.SlowCardDelay"/> on the server clock, then approves (BR-PAY-04);
/// - 0059: errors on the first call, approves when the same payment id is sent again (BR-PAY-05);
/// - any other Luhn-valid number: approved.
/// </summary>
internal sealed class MockPaymentGateway(
    IServiceScopeFactory scopes,
    IOptionsMonitor<PaymentOptions> options,
    OneTimeCodePolicy codes,
    TimeProvider time,
    ILogger<MockPaymentGateway> logger) : IPaymentGateway
{
    public async Task<GatewayChargeResult> ChargeAsync(GatewayChargeRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<LumenDbContext>();

        if (await FindAsync(db, request.PaymentId, cancellationToken) is not null)
        {
            return await ContinueAsync(db, request.PaymentId, cancellationToken);
        }

        var behaviour = CardBehaviours.For(request.Card);
        var now = time.GetUtcNow();

        // 0042 answers SlowCardDelay after the first call. The timer starts before the charge is recorded, so a test that
        // advances a fake clock once it sees the charge always releases it.
        var slowAnswer = behaviour == CardBehaviour.ApproveAfterDelay ? Task.Delay(options.CurrentValue.SlowCardDelay, time, cancellationToken) : null;

        var charge = new MockGatewayCharge
        {
            IdempotencyKey = request.PaymentId,
            CardBrand = request.Card.Brand,
            CardLast4 = request.Card.Last4,
            Behaviour = behaviour,
            Amount = request.Amount,
            CreatedUtc = now,
        };
        (charge.Status, charge.DeclineReason, charge.ErrorCount) = behaviour switch
        {
            CardBehaviour.Approve => (MockGatewayChargeStatus.Approved, (DeclineReason?)null, 0),
            CardBehaviour.DeclineInsufficientFunds => (MockGatewayChargeStatus.Declined, DeclineReason.InsufficientFunds, 0),
            CardBehaviour.DeclineStolen => (MockGatewayChargeStatus.Declined, DeclineReason.CardReportedStolen, 0),
            CardBehaviour.RequireOneTimeCode => (MockGatewayChargeStatus.RequiresAction, null, 0),
            CardBehaviour.ApproveAfterDelay => (MockGatewayChargeStatus.Processing, null, 0),
            CardBehaviour.ErrorThenApprove => (MockGatewayChargeStatus.Error, null, 1),
            _ => throw new ArgumentOutOfRangeException(nameof(request), behaviour, "Unknown card behaviour."),
        };
        if (charge.Status is MockGatewayChargeStatus.Approved or MockGatewayChargeStatus.Declined)
        {
            charge.CompletedUtc = now;
        }

        db.MockGatewayCharges.Add(charge);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // A concurrent first call with the same key got there first: answer as a repeat.
            db.ChangeTracker.Clear();
            return await ContinueAsync(db, request.PaymentId, cancellationToken);
        }

        logger.LogInformation("Mock gateway: charge {Payment} for {Amount} USD on {Card}: {Status}",
            request.PaymentId, request.Amount, request.Card, charge.Status);
        if (slowAnswer is not null)
        {
            await slowAnswer;
            return await CompleteSlowChargeAsync(db, request.PaymentId, cancellationToken);
        }

        return ToResult(charge);
    }

    public async Task<GatewayChargeResult> SubmitOneTimeCodeAsync(Guid paymentId, string code, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<LumenDbContext>();
        var charge = await FindAsync(db, paymentId, cancellationToken);
        if (charge is null)
        {
            return new GatewayChargeResult(GatewayChargeStatus.NotFound);
        }

        if (charge.Status != MockGatewayChargeStatus.RequiresAction)
        {
            return ToResult(charge);
        }

        var now = time.GetUtcNow();
        var (status, reason) = codes.Check(charge.CreatedUtc, now, code) switch
        {
            OneTimeCodeResult.Approved => (MockGatewayChargeStatus.Approved, (DeclineReason?)null),
            OneTimeCodeResult.Expired => (MockGatewayChargeStatus.Declined, DeclineReason.OneTimeCodeExpired),
            _ => (MockGatewayChargeStatus.Declined, DeclineReason.WrongOneTimeCode),
        };

        // Conditional, so a code and the expiry sweep (GetChargeAsync) cannot both decide the same charge.
        await db.MockGatewayCharges
            .Where(c => c.IdempotencyKey == paymentId && c.Status == MockGatewayChargeStatus.RequiresAction)
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.Status, status)
                .SetProperty(c => c.DeclineReason, reason)
                .SetProperty(c => c.CompletedUtc, now), cancellationToken);
        return ToResult((await FindAsync(db, paymentId, cancellationToken))!);
    }

    public async Task<GatewayChargeResult> GetChargeAsync(Guid paymentId, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<LumenDbContext>();
        var charge = await FindAsync(db, paymentId, cancellationToken);
        if (charge is null)
        {
            return new GatewayChargeResult(GatewayChargeStatus.NotFound);
        }

        var now = time.GetUtcNow();
        if (charge.Status == MockGatewayChargeStatus.RequiresAction && now >= codes.ExpiresAt(charge.CreatedUtc))
        {
            // The gateway closes a code nobody entered in time; a late code is then declined, never approved.
            await db.MockGatewayCharges
                .Where(c => c.IdempotencyKey == paymentId && c.Status == MockGatewayChargeStatus.RequiresAction)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(c => c.Status, MockGatewayChargeStatus.Declined)
                    .SetProperty(c => c.DeclineReason, DeclineReason.OneTimeCodeExpired)
                    .SetProperty(c => c.CompletedUtc, now), cancellationToken);
            return ToResult((await FindAsync(db, paymentId, cancellationToken))!);
        }

        if (charge.Status == MockGatewayChargeStatus.Processing && now >= SlowAnswerDue(charge))
        {
            // The slow charge finished on the gateway's side even if the caller is no longer waiting for it.
            return await CompleteSlowChargeAsync(db, paymentId, cancellationToken);
        }

        return ToResult(charge);
    }

    public async Task<GatewayRefundResult> RefundAsync(GatewayRefundRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<LumenDbContext>();
        if (await db.MockGatewayRefunds.AsNoTracking().AnyAsync(r => r.IdempotencyKey == request.RefundId, cancellationToken))
        {
            return new GatewayRefundResult(true);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var amount = request.Amount;
        var taken = await db.MockGatewayCharges
            .Where(c => c.IdempotencyKey == request.PaymentId && c.Status == MockGatewayChargeStatus.Approved && c.RefundedAmount + amount <= c.Amount)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.RefundedAmount, c => c.RefundedAmount + amount), cancellationToken);
        if (taken != 1)
        {
            await transaction.RollbackAsync(cancellationToken);
            var charge = await FindAsync(db, request.PaymentId, cancellationToken);
            var why = charge is null ? "no charge with this payment id"
                : charge.Status != MockGatewayChargeStatus.Approved ? $"the charge is {charge.Status}, not approved"
                : $"{amount:0.00} would exceed the {charge.Amount - charge.RefundedAmount:0.00} left to refund";
            return new GatewayRefundResult(false, $"Refund refused: {why}.");
        }

        db.MockGatewayRefunds.Add(new MockGatewayRefund
        {
            IdempotencyKey = request.RefundId,
            ChargeIdempotencyKey = request.PaymentId,
            Amount = amount,
            CreatedUtc = time.GetUtcNow(),
        });
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // A concurrent call with the same refund id paid it out; this one's increment is rolled back.
            await transaction.RollbackAsync(CancellationToken.None);
        }

        logger.LogInformation("Mock gateway: refund {Refund} of {Amount} USD on charge {Payment}", request.RefundId, amount, request.PaymentId);
        return new GatewayRefundResult(true);
    }

    /// <summary>A repeated charge call: answers with what the first call decided, moving on where the card says so.</summary>
    private async Task<GatewayChargeResult> ContinueAsync(LumenDbContext db, Guid paymentId, CancellationToken cancellationToken)
    {
        var charge = (await FindAsync(db, paymentId, cancellationToken))!;
        switch (charge.Status)
        {
            case MockGatewayChargeStatus.Error when charge.Behaviour == CardBehaviour.ErrorThenApprove:
                // 0059: the retry with the same payment id approves (BR-PAY-05).
                var now = time.GetUtcNow();
                await db.MockGatewayCharges
                    .Where(c => c.IdempotencyKey == paymentId && c.Status == MockGatewayChargeStatus.Error)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(c => c.Status, MockGatewayChargeStatus.Approved)
                        .SetProperty(c => c.CompletedUtc, now), cancellationToken);
                return ToResult((await FindAsync(db, paymentId, cancellationToken))!);
            case MockGatewayChargeStatus.Processing:
                var wait = SlowAnswerDue(charge) - time.GetUtcNow();
                if (wait > TimeSpan.Zero)
                {
                    await Task.Delay(wait, time, cancellationToken);
                }

                return await CompleteSlowChargeAsync(db, paymentId, cancellationToken);
            default:
                return ToResult(charge);
        }
    }

    private async Task<GatewayChargeResult> CompleteSlowChargeAsync(LumenDbContext db, Guid paymentId, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        await db.MockGatewayCharges
            .Where(c => c.IdempotencyKey == paymentId && c.Status == MockGatewayChargeStatus.Processing)
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.Status, MockGatewayChargeStatus.Approved)
                .SetProperty(c => c.CompletedUtc, now), cancellationToken);
        return ToResult((await FindAsync(db, paymentId, cancellationToken))!);
    }

    private DateTimeOffset SlowAnswerDue(MockGatewayCharge charge) => charge.CreatedUtc + options.CurrentValue.SlowCardDelay;

    private static Task<MockGatewayCharge?> FindAsync(LumenDbContext db, Guid paymentId, CancellationToken cancellationToken) =>
        db.MockGatewayCharges.AsNoTracking().SingleOrDefaultAsync(c => c.IdempotencyKey == paymentId, cancellationToken);

    private GatewayChargeResult ToResult(MockGatewayCharge charge) => charge.Status switch
    {
        MockGatewayChargeStatus.Approved => new GatewayChargeResult(GatewayChargeStatus.Approved),
        MockGatewayChargeStatus.Declined => new GatewayChargeResult(GatewayChargeStatus.Declined, charge.DeclineReason),
        MockGatewayChargeStatus.RequiresAction => new GatewayChargeResult(GatewayChargeStatus.RequiresAction,
            OneTimeCodeIssuedUtc: charge.CreatedUtc, OneTimeCodeExpiresUtc: codes.ExpiresAt(charge.CreatedUtc)),
        MockGatewayChargeStatus.Processing => new GatewayChargeResult(GatewayChargeStatus.Processing),
        _ => GatewayChargeResult.Failure($"Gateway error on charge {charge.IdempotencyKey} (the issuer did not answer; retry with the same payment id)."),
    };
}
