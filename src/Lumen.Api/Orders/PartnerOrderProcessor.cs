using Lumen.Api.Infrastructure.Data;
using Lumen.Api.Partners;
using Lumen.Api.Partners.Ports;
using Lumen.Api.Partners.Sync;
using Lumen.Domain.Orders;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Lumen.Api.Orders;

/// <summary>What the job needs to call a partner for one claimed partner order. Read in the claim transaction.</summary>
/// <param name="Poll">Ask for the decision on <paramref name="Ticket"/> instead of sending the order.</param>
/// <param name="OutcomeUnknownBefore">
/// Whether an earlier send's outcome was already unknown when this one was claimed (the claim itself marks the row
/// unknown until this send's result is saved, so a crash in between leaves it unknown).
/// </param>
internal sealed record PartnerOrderWork(
    Guid PartnerOrderId,
    Guid OrderId,
    string Source,
    bool Poll,
    string? Ticket,
    PartnerOrderRequest Request,
    bool OutcomeUnknownBefore = false);

/// <summary>What applying one partner call did.</summary>
internal enum PartnerOrderStep
{
    /// <summary>The partner will supply it; the line is Confirmed.</summary>
    Confirmed,

    /// <summary>The partner took it and decides later (a ticket); polling starts.</summary>
    Accepted,

    /// <summary>Still accepted and undecided; polled again later.</summary>
    StillWaiting,

    /// <summary>The partner will not supply it; the line is Rejected and refunded (BR-ORD-03).</summary>
    Rejected,

    /// <summary>Not accepted within the deadline and never received by the partner (BR-ORD-04); the line is Rejected and refunded.</summary>
    GivenUp,

    /// <summary>
    /// BR-ORD-04: the partner may hold the order but Lumen cannot tell (an unknown outcome at the deadline, or a refused
    /// resend after a lost answer): the line now needs an admin and is not refunded automatically.
    /// </summary>
    NeedsAttention,

    /// <summary>No answer to act on; tried again later with the same reference.</summary>
    RetryScheduled,

    /// <summary>The partner order was already final (e.g. an admin closed it): the answer changed nothing.</summary>
    Ignored,
}

/// <summary>
/// The database side of the partner order job (BR-ORD-01, BR-ORD-03, BR-ORD-04): claims a due partner order in one short
/// transaction, and applies the partner's answer in another, with the partner call in between, outside any transaction
/// (the job makes it). Every step is idempotent: an answer applied twice, or after the partner order became final,
/// changes nothing (<see cref="PartnerOrderUpdates"/>), and a line is refunded once however often it is rejected.
/// </summary>
internal sealed class PartnerOrderProcessor(
    LumenDbContext db,
    IOptionsMonitor<PartnerOrdersOptions> options,
    SellerDirectory sellers,
    TimeProvider clock,
    ILogger<PartnerOrderProcessor> logger)
{
    /// <summary>The partner status page groups errors by operation; these are the ordering ones.</summary>
    public const string ErrorOperation = "order";

    private const int MaxErrorLength = 2000;

    private static readonly string[] OpenStatuses =
        [nameof(PartnerOrderStatus.Pending), nameof(PartnerOrderStatus.Submitted), nameof(PartnerOrderStatus.Accepted)];

    private readonly HashSet<string> _partnersWithNewErrors = new(StringComparer.Ordinal);

    /// <summary>
    /// Claims the partner's next due partner order: a due row of a paid order only (BR-ORD-01: never one of an unpaid
    /// order), locked with <c>FOR UPDATE SKIP LOCKED</c> so concurrent job instances never take the same row, then
    /// reserved for <see cref="PartnerOrdersOptions.ClaimTimeout"/> before the transaction commits, since the partner call
    /// happens after it. A send moves Pending to Submitted and marks the outcome unknown until its result is saved.
    /// </summary>
    /// <param name="onlyOrderIds">Restricts the claim to these orders (tests share one database); null for all.</param>
    /// <returns>The claimed work, or null when nothing of this partner is due.</returns>
    public async Task<PartnerOrderWork?> ClaimNextAsync(string partner, IReadOnlyCollection<Guid>? onlyOrderIds, CancellationToken cancellationToken)
    {
        db.ChangeTracker.Clear();
        var now = clock.GetUtcNow();
        var settings = options.CurrentValue;
        var scoped = onlyOrderIds is not null;
        var orderIds = onlyOrderIds?.ToArray() ?? [];

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // A Pending row with no due time yet is due at once.
        var ids = await db.Database.SqlQuery<Guid>($"""
            SELECT p."Id" AS "Value"
            FROM "PartnerOrders" AS p
            JOIN "OrderLines" AS l ON l."Id" = p."OrderLineId"
            JOIN "Orders" AS o ON o."Id" = l."OrderId"
            WHERE p."Source" = {partner}
              AND p."Status" = ANY({OpenStatuses})
              AND (p."NextAttemptUtc" <= {now} OR (p."NextAttemptUtc" IS NULL AND p."Status" = {nameof(PartnerOrderStatus.Pending)}))
              AND o."Status" = {nameof(OrderStatus.Paid)}
              AND (NOT {scoped} OR o."Id" = ANY({orderIds}))
            ORDER BY p."NextAttemptUtc" NULLS FIRST, p."Id"
            LIMIT 1
            FOR UPDATE OF p SKIP LOCKED
            """).ToListAsync(cancellationToken);

        if (ids.Count == 0)
        {
            await transaction.CommitAsync(cancellationToken);
            return null;
        }

        var partnerOrder = await db.PartnerOrders.Include(p => p.OrderLine).SingleAsync(p => p.Id == ids[0], cancellationToken);
        var line = partnerOrder.OrderLine!;
        var shipTo = (await db.Orders.AsNoTracking().SingleAsync(o => o.Id == line.OrderId, cancellationToken)).ShipTo;
        var poll = partnerOrder.Status == PartnerOrderStatus.Accepted && !string.IsNullOrEmpty(partnerOrder.PartnerOrderId);
        var unknownBefore = partnerOrder.OutcomeUnknown;
        if (partnerOrder.Status == PartnerOrderStatus.Pending)
        {
            partnerOrder.Status = OrderTransitions.PartnerOrder.Move(partnerOrder.Status, PartnerOrderStatus.Submitted);
        }

        if (!poll)
        {
            // Pessimistic until this send's result is saved: if the job dies after sending, the partner may have it.
            partnerOrder.OutcomeUnknown = true;
        }

        partnerOrder.SubmittedUtc ??= now;
        partnerOrder.Attempts++;
        partnerOrder.NextAttemptUtc = now + settings.ClaimTimeout;
        partnerOrder.ClaimedUntilUtc = now + settings.ClaimTimeout;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new PartnerOrderWork(
            partnerOrder.Id,
            line.OrderId,
            partnerOrder.Source,
            poll,
            partnerOrder.PartnerOrderId,
            new PartnerOrderRequest(
                partnerOrder.ExternalRef,
                line.SourceProductId,
                line.Quantity,
                new PartnerShipTo(shipTo.Name, shipTo.Line1, shipTo.Line2, shipTo.City, shipTo.Region, shipTo.PostalCode, shipTo.Country)),
            unknownBefore);
    }

    /// <summary>
    /// Applies one partner call's result in a short transaction holding the order and partner order row locks: the
    /// answer through <see cref="PartnerOrderUpdates.ApplyPartnerAnswer"/> (refunding a rejected line, BR-ORD-03), or,
    /// with no answer, a retry with back-off and, past the deadline, giving up or handing the line to an admin (BR-ORD-04).
    /// </summary>
    public async Task<PartnerOrderStep> ApplyAsync(PartnerOrderWork work, PartnerOrderResult result, CancellationToken cancellationToken)
    {
        db.ChangeTracker.Clear();
        var now = clock.GetUtcNow();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.LockOrderAsync(work.OrderId, cancellationToken);
        await db.LockPartnerOrderAsync(work.PartnerOrderId, cancellationToken);

        var order = await db.Orders
            .Include(o => o.Lines).ThenInclude(l => l.PartnerOrder)
            .Include(o => o.Shipments)
            .AsSplitQuery()
            .SingleAsync(o => o.Id == work.OrderId, cancellationToken);
        var line = order.Lines.Single(l => l.PartnerOrder?.Id == work.PartnerOrderId);
        var partnerOrder = line.PartnerOrder!;

        // The call this claim was for is over, whatever it brought back.
        partnerOrder.ClaimedUntilUtc = null;

        PartnerOrderStep step;
        if (OrderTransitions.PartnerOrder.IsFinal(partnerOrder.Status))
        {
            // Decided meanwhile (an admin closed it after the claim expired, or another instance applied an answer).
            ReportLateAnswer(line, partnerOrder, result, now);
            partnerOrder.NextAttemptUtc = null;
            step = PartnerOrderStep.Ignored;
        }
        else
        {
            PrepareForAnswer(order, line, partnerOrder, now);
            result = RefuseOverlongId(result, partnerOrder.Source);
            step = result.Answer is { } answer
                ? await ApplyAnswerAsync(order, line, partnerOrder, answer, result, work, now, cancellationToken)
                : await ApplyNoAnswerAsync(order, line, partnerOrder, result, work, now, cancellationToken);
        }

        LineRejection.DeriveOrderStatus(order);
        await db.SaveChangesAsync(cancellationToken);
        await TrimErrorsAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return step;
    }

    /// <summary>
    /// BR-ORD-04: the partner's lines whose partner accepted the order but has not decided within
    /// <see cref="PartnerOrdersOptions.NeedsAttentionAfter"/> are flagged for an admin. Nothing is refunded: the goods may
    /// still ship. Polling goes on, so a late decision still lands. Orders or partner orders being changed right now are
    /// skipped until the next run.
    /// </summary>
    /// <returns>How many lines were flagged.</returns>
    public async Task<int> FlagUndecidedAsync(string partner, IReadOnlyCollection<Guid>? onlyOrderIds, CancellationToken cancellationToken)
    {
        db.ChangeTracker.Clear();
        var now = clock.GetUtcNow();
        var settings = options.CurrentValue;
        var cutoff = now - settings.NeedsAttentionAfter;

        var query = db.PartnerOrders.AsNoTracking()
            .Where(p => p.Source == partner && p.Status == PartnerOrderStatus.Accepted && p.AcceptedUtc <= cutoff
                        && p.OrderLine!.Status == OrderLineStatus.AwaitingPartner);
        if (onlyOrderIds is not null)
        {
            query = query.Where(p => onlyOrderIds.Contains(p.OrderLine!.OrderId));
        }

        var due = await query
            .OrderBy(p => p.AcceptedUtc)
            .Select(p => new { p.Id, p.OrderLine!.OrderId })
            .Take(settings.BatchSize)
            .ToListAsync(cancellationToken);

        var flagged = 0;
        foreach (var group in due.GroupBy(d => d.OrderId))
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            if (!await db.TryLockOrderAsync(group.Key, cancellationToken))
            {
                continue;
            }

            // Only partner orders no job instance is working on right now: their row lock is free.
            var lockable = (await db.TryLockPartnerOrdersAsync([.. group.Select(d => d.Id)], cancellationToken)).ToHashSet();
            var order = await db.Orders
                .Include(o => o.Lines).ThenInclude(l => l.PartnerOrder)
                .AsSplitQuery()
                .SingleAsync(o => o.Id == group.Key, cancellationToken);
            foreach (var line in order.Lines.Where(l => l.PartnerOrder is { } p && lockable.Contains(p.Id)))
            {
                var partnerOrder = line.PartnerOrder!;
                if (line.Status != OrderLineStatus.AwaitingPartner || partnerOrder.Status != PartnerOrderStatus.Accepted
                    || partnerOrder.AcceptedUtc is not { } accepted || PartnerOrderSchedule.NeedsAttentionAt(accepted, settings) > now)
                {
                    continue; // decided meanwhile
                }

                var seller = sellers.Name(partnerOrder.Source);
                line.Status = OrderTransitions.Line.Move(line.Status, OrderLineStatus.NeedsAttention);
                line.StatusReason = $"No decision from {seller} {PartnerOrderSchedule.Describe(settings.NeedsAttentionAfter)} after it accepted the order";
                AddError(partnerOrder.Source, null,
                    $"{partnerOrder.ExternalRef} (ticket {partnerOrder.PartnerOrderId}): accepted but undecided after "
                    + $"{PartnerOrderSchedule.Describe(settings.NeedsAttentionAfter)}; an admin must confirm or reject the line.", now);
                logger.LogWarning("Partner order {ExternalRef} at {Partner} is undecided {Waited} after acceptance; the line needs attention",
                    partnerOrder.ExternalRef, partnerOrder.Source, now - accepted);
                flagged++;
            }

            LineRejection.DeriveOrderStatus(order);
            await db.SaveChangesAsync(cancellationToken);
            await TrimErrorsAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        return flagged;
    }

    private void PrepareForAnswer(Order order, OrderLine line, PartnerOrder partnerOrder, DateTimeOffset now)
    {
        // The payment transaction moves partner lines to AwaitingPartner; repair it here (the order is paid, so the
        // table allows it) rather than leave a placed partner order whose answer can never be applied.
        if (line.Status == OrderLineStatus.Pending && order.Status == OrderStatus.Paid)
        {
            logger.LogWarning("Line {LineNumber} of order {OrderNumber} was still Pending after payment; moved to AwaitingPartner",
                line.LineNumber, order.Number);
            line.Status = OrderTransitions.Line.Move(line.Status, OrderLineStatus.AwaitingPartner);
        }

        // Answers apply to a sent partner order (the claim moves it; this covers a row claimed by an older instance).
        if (partnerOrder.Status == PartnerOrderStatus.Pending)
        {
            partnerOrder.Status = OrderTransitions.PartnerOrder.Move(partnerOrder.Status, PartnerOrderStatus.Submitted);
            partnerOrder.SubmittedUtc ??= now;
        }
    }

    /// <summary>
    /// Partner ids are opaque and never cut. One longer than Lumen stores cannot be kept, so the answer is not applied:
    /// it is recorded as an error, and as the partner may hold the order, its outcome counts as unknown (BR-ORD-04).
    /// </summary>
    private static PartnerOrderResult RefuseOverlongId(PartnerOrderResult result, string partner) =>
        result.Answer is not null && result.PartnerOrderId is { Length: > PartnerOrder.MaxPartnerOrderIdLength } id
            ? result with
            {
                Outcome = PartnerOrderOutcome.RetryLater,
                PartnerOrderId = null,
                OutcomeUnknown = true,
                Error = $"{partner} answered {result.Outcome} with an order id of {id.Length} characters, longer than the "
                        + $"{PartnerOrder.MaxPartnerOrderIdLength} Lumen stores; ids are never cut, so the answer was not applied.",
            }
            : result;

    private async Task<PartnerOrderStep> ApplyAnswerAsync(
        Order order, OrderLine line, PartnerOrder partnerOrder, PartnerOrderStatus answer, PartnerOrderResult result, PartnerOrderWork work,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        var settings = options.CurrentValue;
        var seller = sellers.Name(partnerOrder.Source);

        // BR-ORD-04: a resend refused in a way that does not rule out an order the partner already has (Kestrel's 400),
        // after an earlier send went unanswered or after the partner acknowledged the order (a ticket that was later
        // not found): it may still ship, so an admin decides; nothing is refunded and nothing resent.
        if (answer == PartnerOrderStatus.Rejected && !work.Poll && result.OutcomeUnknown
            && (work.OutcomeUnknownBefore || partnerOrder.AcceptedUtc is not null))
        {
            var reason = string.IsNullOrWhiteSpace(result.Reason) ? "REJECTED" : result.Reason.Trim();
            partnerOrder.OutcomeUnknown = true;
            partnerOrder.LastError = LineRejection.Truncate($"{seller} refused a resend ({reason}) after an earlier send whose answer was lost.", MaxErrorLength);
            return Escalate(line, partnerOrder,
                $"{seller} refused a resend ({reason}) after an earlier send whose answer was lost; it may still have the order",
                nextAttemptUtc: null, now);
        }

        // Harbor's order_id, or Kestrel's ticket (also one adopted from 409 DUPLICATE_REFERENCE): opaque, stored exactly.
        if (result.PartnerOrderId is { } partnerId && partnerId != partnerOrder.PartnerOrderId)
        {
            if (partnerOrder.PartnerOrderId is not null)
            {
                logger.LogWarning("Partner order {ExternalRef}: {Partner} now names it {New} instead of {Old}",
                    partnerOrder.ExternalRef, partnerOrder.Source, partnerId, partnerOrder.PartnerOrderId);
            }

            partnerOrder.PartnerOrderId = partnerId;
        }

        var update = PartnerOrderUpdates.ApplyPartnerAnswer(partnerOrder.Status, line.Status, answer);
        if (update.Ignored)
        {
            ReportLateAnswer(line, partnerOrder, result, now);
            partnerOrder.NextAttemptUtc = null;
            return PartnerOrderStep.Ignored;
        }

        // The partner answered for this reference: whatever it says, the outcome is no longer unknown.
        partnerOrder.OutcomeUnknown = false;
        switch (update.PartnerOrder)
        {
            case PartnerOrderStatus.Accepted:
            {
                var newlyAccepted = partnerOrder.Status != PartnerOrderStatus.Accepted;
                partnerOrder.Status = update.PartnerOrder;
                partnerOrder.AcceptedUtc ??= now;
                partnerOrder.NextAttemptUtc = partnerOrder.PartnerOrderId is null
                    ? now + settings.RetryDelay // accepted without a ticket: resend to get it (409 with the ticket)
                    : PartnerOrderSchedule.NextPoll(partnerOrder.AcceptedUtc.Value, now, result.RetryAfter, settings);
                return newlyAccepted ? PartnerOrderStep.Accepted : PartnerOrderStep.StillWaiting;
            }

            case PartnerOrderStatus.Confirmed:
                partnerOrder.Status = update.PartnerOrder;
                partnerOrder.DecidedUtc = now;
                partnerOrder.NextAttemptUtc = null;
                line.Status = update.Line;
                line.StatusReason = null;
                return PartnerOrderStep.Confirmed;

            case PartnerOrderStatus.Rejected:
            {
                var reason = string.IsNullOrWhiteSpace(result.Reason) ? "REJECTED" : result.Reason.Trim();
                var paymentId = await LineRejection.SucceededPaymentIdAsync(db, order.Id, cancellationToken);
                var refund = LineRejection.Reject(order, line, paymentId, $"Not supplied by {seller} ({reason})", now);
                if (refund is not null)
                {
                    db.Refunds.Add(refund);
                }

                partnerOrder.Status = update.PartnerOrder;
                partnerOrder.Reason = LineRejection.Truncate(reason);
                partnerOrder.DecidedUtc = now;
                partnerOrder.NextAttemptUtc = null;
                line.StatusReason = LineRejection.Truncate(reason);

                // A stock rejection is business as usual; "invalid request" or "invalid item" points at a data or
                // mapping problem the admin should see on the partner status page.
                if (result.HttpStatus is 400 or 422)
                {
                    AddError(partnerOrder.Source, result.HttpStatus, $"{partnerOrder.ExternalRef}: rejected as invalid ({reason}); line refunded.", now);
                }

                return PartnerOrderStep.Rejected;
            }

            default:
                throw new InvalidOperationException($"Unexpected partner order status {update.PartnerOrder} after answer {answer}.");
        }
    }

    private async Task<PartnerOrderStep> ApplyNoAnswerAsync(
        Order order, OrderLine line, PartnerOrder partnerOrder, PartnerOrderResult result, PartnerOrderWork work, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var settings = options.CurrentValue;
        var seller = sellers.Name(partnerOrder.Source);
        var error = string.IsNullOrWhiteSpace(result.Error) ? $"{seller} gave no usable answer ({result.Outcome})." : result.Error.Trim();
        partnerOrder.LastError = LineRejection.Truncate(error, MaxErrorLength);
        if (line.Status == OrderLineStatus.NeedsAttention)
        {
            // Already in front of an admin: a resend every few minutes must not push other errors off the partner page.
            logger.LogWarning("Partner order {ExternalRef} (waiting for an admin) still has no answer: {Error}", partnerOrder.ExternalRef, error);
        }
        else
        {
            AddError(partnerOrder.Source, result.HttpStatus, $"{partnerOrder.ExternalRef}: {error}", now);
        }

        if (!work.Poll)
        {
            // Unknown stays unknown: only this send having provably not reached the partner leaves the earlier state.
            partnerOrder.OutcomeUnknown = work.OutcomeUnknownBefore || result.OutcomeUnknown;
        }

        if (result.Outcome == PartnerOrderOutcome.NotFound)
        {
            // The partner does not know the ticket: resending the same reference gets it back (409 with the ticket).
            partnerOrder.PartnerOrderId = null;
        }

        if (partnerOrder.Status == PartnerOrderStatus.Submitted)
        {
            // "Within 30 minutes of payment": the partner order row is written in the payment transaction, so its
            // creation time stands in when the order carries no payment time.
            var giveUpAt = PartnerOrderSchedule.GiveUpAt(order.PaidUtc ?? partnerOrder.CreatedUtc, settings);
            if (now < giveUpAt)
            {
                partnerOrder.NextAttemptUtc = PartnerOrderSchedule.NextSend(partnerOrder.Attempts, now, result.RetryAfter, giveUpAt, settings);
                return PartnerOrderStep.RetryScheduled;
            }

            if (!partnerOrder.OutcomeUnknown)
            {
                // Every send failed before reaching the partner, or was refused unseen: it cannot have the order.
                await GiveUpAsync(order, line, partnerOrder, seller, now, cancellationToken);
                return PartnerOrderStep.GivenUp;
            }

            // BR-ORD-04: a send may have reached the partner, so it may ship: an admin decides, nothing is refunded. Sends
            // go on slowly with the same reference, so a partner that comes back settles it (200, or 409 with a ticket).
            var wait = result.RetryAfter is { } retryAfter && retryAfter > settings.MaxRetryDelay ? retryAfter : settings.MaxRetryDelay;
            return Escalate(line, partnerOrder,
                $"No answer from {seller} within {PartnerOrderSchedule.Describe(settings.GiveUpAfter)} of payment, and it may have received the order",
                now + wait, now);
        }

        // Accepted: never given up automatically (BR-ORD-04), the goods may still ship. Keep asking.
        partnerOrder.NextAttemptUtc = partnerOrder.PartnerOrderId is null
            ? now + (result.RetryAfter is { } wait2 && wait2 > settings.RetryDelay ? wait2 : settings.RetryDelay)
            : PartnerOrderSchedule.NextPoll(partnerOrder.AcceptedUtc ?? now, now, result.RetryAfter, settings);
        return PartnerOrderStep.RetryScheduled;
    }

    /// <summary>
    /// An answer that arrives once the partner order or its line is final changes nothing. When it contradicts that
    /// decision (the partner has the order of a line that was refunded, or refuses one that was confirmed) the admin must
    /// know: it goes onto the partner status page, not only into the log.
    /// </summary>
    private void ReportLateAnswer(OrderLine line, PartnerOrder partnerOrder, PartnerOrderResult result, DateTimeOffset now)
    {
        var seller = sellers.Name(partnerOrder.Source);
        var contradiction = (result.Answer, line.Status) switch
        {
            (PartnerOrderStatus.Confirmed or PartnerOrderStatus.Accepted, OrderLineStatus.Rejected) =>
                $"{seller} took the order ({result.Outcome}{(result.PartnerOrderId is null ? "" : $", id {result.PartnerOrderId}")}) after its line "
                + "was rejected and refunded: the goods may ship to a customer who got the money back. Check with the partner.",
            (PartnerOrderStatus.Rejected, OrderLineStatus.Confirmed) =>
                $"{seller} rejected the order ({result.Reason ?? "REJECTED"}) after its line was confirmed: the goods will not come. "
                + "Check with the partner and the customer.",
            _ => null,
        };

        if (contradiction is null)
        {
            logger.LogInformation("Partner order {ExternalRef} is already {Status} (line {LineStatus}); the {Outcome} answer is ignored",
                partnerOrder.ExternalRef, partnerOrder.Status, line.Status, result.Outcome);
            return;
        }

        AddError(partnerOrder.Source, result.HttpStatus, $"{partnerOrder.ExternalRef}: {contradiction}", now);
        logger.LogWarning("Partner order {ExternalRef}: {Contradiction}", partnerOrder.ExternalRef, contradiction);
    }

    /// <summary>Hands the line to an admin (NeedsAttention) once; later calls only reschedule.</summary>
    private PartnerOrderStep Escalate(OrderLine line, PartnerOrder partnerOrder, string reason, DateTimeOffset? nextAttemptUtc, DateTimeOffset now)
    {
        partnerOrder.NextAttemptUtc = nextAttemptUtc;
        if (line.Status != OrderLineStatus.AwaitingPartner)
        {
            return PartnerOrderStep.RetryScheduled;
        }

        line.Status = OrderTransitions.Line.Move(line.Status, OrderLineStatus.NeedsAttention);
        line.StatusReason = LineRejection.Truncate(reason);
        AddError(partnerOrder.Source, null, $"{partnerOrder.ExternalRef}: {reason}; an admin must confirm or reject the line.", now);
        logger.LogWarning("Partner order {ExternalRef} needs an admin: {Reason}", partnerOrder.ExternalRef, reason);
        return PartnerOrderStep.NeedsAttention;
    }

    /// <summary>
    /// BR-ORD-04: not accepted within the deadline, after a last attempt at it, and no send ever reached the partner: the
    /// line is refunded (BR-ORD-03).
    /// </summary>
    private async Task GiveUpAsync(Order order, OrderLine line, PartnerOrder partnerOrder, string seller, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var settings = options.CurrentValue;
        var reason = $"Not accepted by {seller} within {PartnerOrderSchedule.Describe(settings.GiveUpAfter)} of payment";
        var paymentId = await LineRejection.SucceededPaymentIdAsync(db, order.Id, cancellationToken);
        var refund = LineRejection.Reject(order, line, paymentId, $"Not supplied by {seller} (not accepted within {PartnerOrderSchedule.Describe(settings.GiveUpAfter)})", now);
        if (refund is not null)
        {
            db.Refunds.Add(refund);
        }

        partnerOrder.Status = OrderTransitions.PartnerOrder.Move(partnerOrder.Status, PartnerOrderStatus.GivenUp);
        partnerOrder.Reason = reason;
        partnerOrder.DecidedUtc = now;
        partnerOrder.NextAttemptUtc = null;
        line.StatusReason = reason;
        AddError(partnerOrder.Source, null, $"{partnerOrder.ExternalRef}: given up, {reason.ToLowerInvariant()}; line refunded.", now);
        logger.LogWarning("Partner order {ExternalRef} given up after {Attempts} attempts: {Reason}", partnerOrder.ExternalRef, partnerOrder.Attempts, reason);
    }

    /// <summary>Appends to the partner's recent errors on the admin partner status page (operation "order").</summary>
    private void AddError(string partner, int? statusCode, string message, DateTimeOffset now)
    {
        db.PartnerSyncErrors.Add(new PartnerSyncError
        {
            Partner = partner,
            Operation = ErrorOperation,
            StatusCode = statusCode,
            Message = LineRejection.Truncate(message, MaxErrorLength),
            OccurredUtc = now,
        });
        _partnersWithNewErrors.Add(partner);
    }

    /// <summary>Keeps the partner's newest errors only, like the catalogue sync does.</summary>
    private async Task TrimErrorsAsync(CancellationToken cancellationToken)
    {
        foreach (var partner in _partnersWithNewErrors)
        {
            var stale = db.PartnerSyncErrors
                .Where(e => e.Partner == partner)
                .OrderByDescending(e => e.OccurredUtc)
                .ThenByDescending(e => e.Id)
                .Skip(PartnerSyncService.ErrorsKept)
                .Select(e => e.Id);
            await db.PartnerSyncErrors.Where(e => stale.Contains(e.Id)).ExecuteDeleteAsync(cancellationToken);
        }

        _partnersWithNewErrors.Clear();
    }
}
