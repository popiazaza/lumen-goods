using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Diagnostics;

namespace HarborSupply.StandIn.Http;

/// <summary>
/// Harbor's standard error body, used for every 4xx and 5xx:
/// <c>{ "error": { "code": "...", "message": "..." } }</c>. The 409 adds item_code and available inside error.
/// </summary>
internal static class HarborErrors
{
    public static IResult Result(int status, string code, string message) =>
        TypedResults.Json(new ErrorEnvelope(new ErrorBody(code, message)), HarborJson.Options, statusCode: status);

    public static IResult InvalidRequest(string message) => Result(StatusCodes.Status400BadRequest, "INVALID_REQUEST", message);

    public static IResult InsufficientStock(string itemCode, int available) =>
        TypedResults.Json(
            new ErrorEnvelope(new StockErrorBody("INSUFFICIENT_STOCK", $"Only {available} of {itemCode} available.", itemCode, available)),
            HarborJson.Options,
            statusCode: StatusCodes.Status409Conflict);

    public static Task WriteAsync(HttpContext context, int status, string code, string message)
    {
        context.Response.StatusCode = status;
        return context.Response.WriteAsJsonAsync(new ErrorEnvelope(new ErrorBody(code, message)), HarborJson.Options, context.RequestAborted);
    }

    /// <summary>
    /// Replaces ASP.NET Core's own error output (empty 404/405 bodies, exception pages, ProblemDetails)
    /// with the standard body, so no framework format leaks through the contract.
    /// </summary>
    public static WebApplication UseHarborErrorBodies(this WebApplication app)
    {
        app.UseExceptionHandler(new ExceptionHandlerOptions
        {
            ExceptionHandler = context =>
            {
                var error = context.Features.Get<IExceptionHandlerFeature>()?.Error;
                var status = error is BadHttpRequestException bad ? bad.StatusCode : StatusCodes.Status500InternalServerError;
                var message = status >= 500 ? "An unexpected error occurred." : "The request could not be read.";
                return WriteAsync(context, status, CodeFor(status), message);
            },
        });

        app.UseStatusCodePages(context =>
        {
            var status = context.HttpContext.Response.StatusCode;
            return WriteAsync(context.HttpContext, status, CodeFor(status), MessageFor(status));
        });

        return app;
    }

    private static string CodeFor(int status) => status switch
    {
        StatusCodes.Status401Unauthorized => "UNAUTHORIZED",
        StatusCodes.Status404NotFound => "NOT_FOUND",
        StatusCodes.Status405MethodNotAllowed => "METHOD_NOT_ALLOWED",
        StatusCodes.Status413PayloadTooLarge => "PAYLOAD_TOO_LARGE",
        StatusCodes.Status429TooManyRequests => "RATE_LIMITED",
        >= 500 => "INTERNAL_ERROR",
        _ => "INVALID_REQUEST",
    };

    private static string MessageFor(int status) => status switch
    {
        StatusCodes.Status404NotFound => "No resource exists at this path.",
        StatusCodes.Status405MethodNotAllowed => "This method is not allowed on this path.",
        >= 500 => "An unexpected error occurred.",
        _ => $"The request failed with status {status}.",
    };

    private sealed record ErrorEnvelope([property: JsonPropertyName("error")] object Error);

    private sealed record ErrorBody(
        [property: JsonPropertyName("code")] string Code,
        [property: JsonPropertyName("message")] string Message);

    private sealed record StockErrorBody(
        [property: JsonPropertyName("code")] string Code,
        [property: JsonPropertyName("message")] string Message,
        [property: JsonPropertyName("item_code")] string ItemCode,
        [property: JsonPropertyName("available")] int Available);
}
