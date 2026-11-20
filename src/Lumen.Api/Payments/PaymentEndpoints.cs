using Lumen.Api.Checkout;
using Lumen.Api.Infrastructure.Data;
using Lumen.Api.Infrastructure.RateLimiting;
using Lumen.Domain.Orders;
using Lumen.Domain.Payments;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Lumen.Api.Payments;

internal static class PaymentEndpoints
{
    public static RouteGroupBuilder MapPayments(this RouteGroupBuilder api)
    {
        var payments = api.MapGroup("/orders/{number}/payments").WithTags("Payments").RequireAuthorization()
            .ProducesProblem(StatusCodes.Status401Unauthorized).ProducesProblem(StatusCodes.Status404NotFound);
        payments.MapPost("", Pay).WithName("PayOrder")
            .RequireRateLimiting(RateLimitPolicies.Payment)
            .Produces<PaymentStatusDto>(StatusCodes.Status202Accepted)
            .Produces<PaymentInProgressProblem>(StatusCodes.Status409Conflict, "application/problem+json")
            .ProducesValidationProblem().ProducesProblem(StatusCodes.Status429TooManyRequests);
        payments.MapGet("/current", Current).WithName("GetCurrentPayment");
        payments.MapPost("/current/one-time-code", SubmitOneTimeCode).WithName("SubmitOneTimeCode")
            .Produces<PaymentInProgressProblem>(StatusCodes.Status409Conflict, "application/problem+json")
            .ProducesValidationProblem();
        return api;
    }

    /// <summary>
    /// 202 with the payment: a card charge runs on in the background (poll <c>GET .../payments/current</c> while it is
    /// created or processing); a wallet payment has already succeeded or been declined.
    /// </summary>
    private static async Task<IResult> Pay(
        string number, PayRequest request, HttpContext http, PaymentService payments, PaymentStatusReader statuses, CancellationToken cancellationToken)
    {
        if (http.User.CustomerId() is not { } customerId)
        {
            return TypedResults.Unauthorized();
        }

        if (!OrderNumberGenerator.IsWellFormed(number))
        {
            return OrderNotFound(number);
        }

        var outcome = await payments.PayAsync(customerId, number, request, cancellationToken);
        switch (outcome)
        {
            case PayOutcome.Started started:
                var status = (await statuses.CurrentAsync(started.OrderId, settle: false, cancellationToken))!;
                return TypedResults.Accepted($"/api/v1/orders/{number}/payments/current", status);
            case PayOutcome.Invalid invalid:
                return TypedResults.ValidationProblem(invalid.Errors, title: "The payment details are not valid");
            case PayOutcome.InProgress inProgress:
                return await InProgressAsync(inProgress.OrderId, statuses, cancellationToken);
            case PayOutcome.NotPayable notPayable:
                return TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, type: PaymentProblemTypes.OrderNotPayable,
                    title: "This order cannot be paid", detail: notPayable.Detail);
            case PayOutcome.SamePaymentOnly sameOnly:
                return TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, type: PaymentProblemTypes.SamePaymentOnly,
                    title: "Try the same card again", detail: sameOnly.Detail);
            default:
                return OrderNotFound(number);
        }
    }

    /// <summary>The page polls this while the payment is created or processing; it also settles what is due (BR-PAY-04).</summary>
    private static async Task<Results<Ok<PaymentStatusDto>, ProblemHttpResult, UnauthorizedHttpResult>> Current(
        string number, HttpContext http, LumenDbContext db, PaymentStatusReader statuses, CancellationToken cancellationToken)
    {
        if (http.User.CustomerId() is not { } customerId)
        {
            return TypedResults.Unauthorized();
        }

        var orderId = await FindOrderAsync(db, customerId, number, cancellationToken);
        if (orderId is null || await statuses.CurrentAsync(orderId.Value, settle: true, cancellationToken) is not { } status)
        {
            return orderId is null ? OrderNotFound(number) : NoPayment(number);
        }

        return TypedResults.Ok(status);
    }

    /// <summary>
    /// BR-PAY-03: the code for card 0034. The gateway checks it on the server clock (123456 before expiry approves; any
    /// other code, or any code after expiry, ends the attempt). Not cancellable once sent, so a closed tab cannot leave
    /// an approved code unrecorded.
    /// </summary>
    private static async Task<IResult> SubmitOneTimeCode(
        string number,
        OneTimeCodeRequest request,
        HttpContext http,
        LumenDbContext db,
        IPaymentGateway gateway,
        PaymentRecorder recorder,
        PaymentStatusReader statuses,
        CancellationToken cancellationToken)
    {
        if (http.User.CustomerId() is not { } customerId)
        {
            return TypedResults.Unauthorized();
        }

        if (string.IsNullOrWhiteSpace(request.Code) || request.Code.Length > 16)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["code"] = ["Enter the code."] });
        }

        var orderId = await FindOrderAsync(db, customerId, number, cancellationToken);
        if (orderId is null)
        {
            return OrderNotFound(number);
        }

        var payment = await db.Payments.AsNoTracking()
            .Where(p => p.OrderId == orderId)
            .OrderByDescending(p => p.CreatedUtc).ThenByDescending(p => p.Id)
            .FirstOrDefaultAsync(cancellationToken);
        switch (payment?.Status)
        {
            case null:
                return NoPayment(number);
            case PaymentStatus.RequiresAction:
                var answer = await gateway.SubmitOneTimeCodeAsync(payment.Id, request.Code, CancellationToken.None);
                await recorder.RecordAsync(payment.Id, answer, CancellationToken.None);
                break;
            case PaymentStatus.Created or PaymentStatus.Processing or PaymentStatus.Error:
                return await InProgressAsync(orderId.Value, statuses, cancellationToken);
        }

        // An outcome already reached (a double submit, or the code expired meanwhile): show it.
        return TypedResults.Ok(await statuses.CurrentAsync(orderId.Value, settle: false, cancellationToken));
    }

    private static async Task<IResult> InProgressAsync(Guid orderId, PaymentStatusReader statuses, CancellationToken cancellationToken)
    {
        var current = await statuses.CurrentAsync(orderId, settle: false, cancellationToken);
        if (current is null)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, type: PaymentProblemTypes.PaymentInProgress,
                title: "A payment is already in progress", detail: "Another payment of this order is being started.");
        }

        var detail = current.Status == PaymentStatus.RequiresAction
            ? "Your card payment is waiting for its one-time code."
            : "Your payment is still being confirmed.";
        return TypedResults.Json(new PaymentInProgressProblem(detail, current), statusCode: StatusCodes.Status409Conflict,
            contentType: "application/problem+json");
    }

    private static async Task<Guid?> FindOrderAsync(LumenDbContext db, Guid customerId, string number, CancellationToken cancellationToken) =>
        OrderNumberGenerator.IsWellFormed(number)
            ? await db.Orders.AsNoTracking().Where(o => o.Number == number && o.CustomerId == customerId)
                .Select(o => (Guid?)o.Id).SingleOrDefaultAsync(cancellationToken)
            : null;

    private static ProblemHttpResult OrderNotFound(string number) =>
        TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "Order not found", detail: $"There is no order {number} in your account.");

    private static ProblemHttpResult NoPayment(string number) =>
        TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "No payment yet", detail: $"Order {number} has no payment yet.");
}
