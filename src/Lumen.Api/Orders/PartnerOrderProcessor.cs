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
internal sealed record PartnerOrderWork(
    Guid PartnerOrderId,
    Guid OrderId,
    string Source,
    bool Poll,
    string? Ticket,
    PartnerOrderRequest Request);

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

    /// <summary>Not accepted within the deadline (BR-ORD-04); the line is Rejected and refunded.</summary>
    GivenUp,

    /// <summary>No answer to act on; tried again later with the same reference.</summary>
    RetryScheduled,

    /// <summary>The partner order was already final (e.g. an admin closed it): the answer changed nothing.</summary>
    Ignored,
}

/// <summary>
/// The database side of the partner order job (BR-ORD-01, BR-ORD-03, BR-ORD-04): claims due partner orders in one short
/// transaction, and applies each partner's answer in another, with all partner I/O in between, outside any transaction
/// (the job does it). Every step is idempotent: an answer applied twice, or after the partner order became final,
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
    /// Claims the partner orders due now: due rows of paid orders only (BR-ORD-01: never one of an unpaid order), locked
    /// with <c>FOR UPDATE SKIP LOCKED</c> so concurrent job instances never take the same row, then reserved for
    /// <see cref="PartnerOrdersOptions.ClaimTimeout"/> before the transaction commits, since the partner call happens after it.
    /// A Pending partner order moves to Submitted here, as it is about to be sent.
    /// </summary>
    /// <param name="onlyOrderIds">Restricts the run to these orders (tests share one database); null for all.</param>
    public async Task<IReadOnlyList<PartnerOrderWork>> ClaimDueAsync(IReadOnlyCollection<Guid>? onlyOrderIds, CancellationToken cancellationToken)
    {
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
            WHERE p."Status" = ANY({OpenStatuses})
              AND (p."NextAttemptUtc" <= {now} OR (p."NextAttemptUtc" IS NULL AND p."Status" = {nameof(PartnerOrderStatus.Pending)}))
              AND o."Status" = {nameof(OrderStatus.Paid)}
              AND (NOT {scoped} OR o."Id" = ANY({orderIds}))
            ORDER BY p."NextAttemptUtc" NULLS FIRST, p."Id"
            LIMIT {settings.BatchSize}
            FOR UPDATE OF p SKIP LOCKED
            """).ToListAsync(cancellationToken);

        if (ids.Count == 0)
        {
            await transaction.CommitAsync(cancellationToken);
            return [];
        }

        var claimed = await db.PartnerOrders.Include(p => p.OrderLine).Where(p => ids.Contains(p.Id)).ToListAsync(cancellationToken);
        var lineOrderIds = claimed.Select(p => p.OrderLine!.OrderId).Distinct().ToList();
        var orders = await db.Orders.AsNoTracking().Where(o => lineOrderIds.Contains(o.Id)).ToDictionaryAsync(o => o.Id, cancellationToken);

        var work = new List<PartnerOrderWork>(claimed.Count);
        foreach (var partnerOrder in claimed.OrderBy(p => ids.IndexOf(p.Id)))
        {
            var line = partnerOrder.OrderLine!;
            var shipTo = orders[line.OrderId].ShipTo;
            var poll = partnerOrder.Status == PartnerOrderStatus.Accepted && !string.IsNullOrEmpty(partnerOrder.PartnerOrderId);
            if (partnerOrder.Status == PartnerOrderStatus.Pending)
            {
                partnerOrder.Status = OrderTransitions.PartnerOrder.Move(partnerOrder.Status, PartnerOrderStatus.Submitted);
            }

            partnerOrder.SubmittedUtc ??= now;
            partnerOrder.Attempts++;
            partnerOrder.NextAttemptUtc = now + settings.ClaimTimeout;

            work.Add(new PartnerOrderWork(
                partnerOrder.Id,
                line.OrderId,
                partnerOrder.Source,
                poll,
                partnerOrder.PartnerOrderId,
                new PartnerOrderRequest(
                    partnerOrder.ExternalRef,
                    line.SourceProductId,
                    line.Quantity,
                    new PartnerShipTo(shipTo.Name, shipTo.Line1, shipTo.Line2, shipTo.City, shipTo.Region, shipTo.PostalCode, shipTo.Country))));
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return work;
    }

    /// <summary>
    /// Applies one partner call's result in a short transaction holding the order row lock: the answer through
    /// <see cref="PartnerOrderUpdates.ApplyPartnerAnswer"/> (refunding a rejected line, BR-ORD-03), or, with no answer,
    /// a retry with back-off, or giving up once the partner order is past its deadline (BR-ORD-04).
    /// </summary>
    public async Task<PartnerOrderStep> ApplyAsync(PartnerOrderWork work, PartnerOrderResult result, CancellationToken cancellationToken)
    {
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

        PartnerOrderStep step;
        if (OrderTransitions.PartnerOrder.IsFinal(partnerOrder.Status))
        {
            // Decided meanwhile (an admin closed it, or another instance applied an answer): nothing left to ask.
            logger.LogInformation("Partner order {ExternalRef} is already {Status}; the {Outcome} answer is ignored",
                partnerOrder.ExternalRef, partnerOrder.Status, result.Outcome);
            partnerOrder.NextAttemptUtc = null;
            step = PartnerOrderStep.Ignored;
        }
        else
        {
            PrepareForAnswer(order, line, partnerOrder, now);
            step = result.Answer is { } answer
                ? await ApplyAnswerAsync(order, line, partnerOrder, answer, result, now, cancellationToken)
                : await ApplyNoAnswerAsync(order, line, partnerOrder, result, now, cancellationToken);
        }

        LineRejection.DeriveOrderStatus(order);
        await db.SaveChangesAsync(cancellationToken);
        await TrimErrorsAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return step;
    }

    /// <summary>
    /// BR-ORD-04: lines whose partner accepted the order but has not decided within
    /// <see cref="PartnerOrdersOptions.NeedsAttentionAfter"/> are flagged for an admin. Nothing is refunded: the goods may
    /// still ship. Polling goes on, so a late decision still lands. Orders being changed right now are skipped until the next run.
    /// </summary>
    /// <returns>How many lines were flagged.</returns>
    public async Task<int> FlagUndecidedAsync(IReadOnlyCollection<Guid>? onlyOrderIds, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var settings = options.CurrentValue;
        var cutoff = now - settings.NeedsAttentionAfter;

        var query = db.PartnerOrders.AsNoTracking()
            .Where(p => p.Status == PartnerOrderStatus.Accepted && p.AcceptedUtc <= cutoff && p.OrderLine!.Status == OrderLineStatus.AwaitingPartner);
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

            var order = await db.Orders
                .Include(o => o.Lines).ThenInclude(l => l.PartnerOrder)
                .AsSplitQuery()
                .SingleAsync(o => o.Id == group.Key, cancellationToken);
            var partnerOrderIds = group.Select(d => d.Id).ToHashSet();
            foreach (var line in order.Lines.Where(l => l.PartnerOrder is { } p && partnerOrderIds.Contains(p.Id)))
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

    private async Task<PartnerOrderStep> ApplyAnswerAsync(
        Order order, OrderLine line, PartnerOrder partnerOrder, PartnerOrderStatus answer, PartnerOrderResult result, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var settings = options.CurrentValue;

        // Harbor's order_id, or Kestrel's ticket (also one adopted from 409 DUPLICATE_REFERENCE): opaque, stored as sent.
        if (result.PartnerOrderId is { } partnerId && partnerId != partnerOrder.PartnerOrderId)
        {
            if (partnerOrder.PartnerOrderId is not null)
            {
                logger.LogWarning("Partner order {ExternalRef}: {Partner} now names it {New} instead of {Old}",
                    partnerOrder.ExternalRef, partnerOrder.Source, partnerId, partnerOrder.PartnerOrderId);
            }

            partnerOrder.PartnerOrderId = LineRejection.Truncate(partnerId, 64);
        }

        var update = PartnerOrderUpdates.ApplyPartnerAnswer(partnerOrder.Status, line.Status, answer);
        if (update.Ignored)
        {
            logger.LogInformation("Partner order {ExternalRef}: {Reason}", partnerOrder.ExternalRef, update.IgnoredBecause);
            partnerOrder.NextAttemptUtc = null;
            return PartnerOrderStep.Ignored;
        }

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
                var refund = LineRejection.Reject(order, line, paymentId, $"Not supplied by {sellers.Name(partnerOrder.Source)} ({reason})", now);
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
        Order order, OrderLine line, PartnerOrder partnerOrder, PartnerOrderResult result, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var settings = options.CurrentValue;
        var seller = sellers.Name(partnerOrder.Source);
        var error = string.IsNullOrWhiteSpace(result.Error) ? $"{seller} gave no usable answer ({result.Outcome})." : result.Error.Trim();
        partnerOrder.LastError = LineRejection.Truncate(error, MaxErrorLength);
        AddError(partnerOrder.Source, result.HttpStatus, $"{partnerOrder.ExternalRef}: {error}", now);

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
            if (now >= giveUpAt)
            {
                await GiveUpAsync(order, line, partnerOrder, seller, now, cancellationToken);
                return PartnerOrderStep.GivenUp;
            }

            partnerOrder.NextAttemptUtc = PartnerOrderSchedule.NextSend(partnerOrder.Attempts, now, result.RetryAfter, giveUpAt, settings);
            return PartnerOrderStep.RetryScheduled;
        }

        // Accepted: never given up automatically (BR-ORD-04), the goods may still ship. Keep asking.
        partnerOrder.NextAttemptUtc = partnerOrder.PartnerOrderId is null
            ? now + (result.RetryAfter is { } wait && wait > settings.RetryDelay ? wait : settings.RetryDelay)
            : PartnerOrderSchedule.NextPoll(partnerOrder.AcceptedUtc ?? now, now, result.RetryAfter, settings);
        return PartnerOrderStep.RetryScheduled;
    }

    /// <summary>BR-ORD-04: not accepted within the deadline, after a last attempt at it: the line is refunded (BR-ORD-03).</summary>
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
