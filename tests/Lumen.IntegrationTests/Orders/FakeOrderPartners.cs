using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace Lumen.IntegrationTests.Orders;

/// <summary>An order call as the fake partner saw it.</summary>
public sealed record OrderCall(string Method, string Path, string? Reference, string Body);

/// <summary>
/// Base for the order fakes: records calls and can play "the response was lost" (the partner did the work, Lumen never
/// heard back), which is what a timeout after the partner accepted looks like from Lumen's side.
/// </summary>
public abstract class FakeOrderPartner : HttpMessageHandler
{
    private readonly ConcurrentQueue<OrderCall> _calls = new();

    /// <summary>References whose next N answers are lost after the partner acted on them.</summary>
    public ConcurrentDictionary<string, int> LoseAnswers { get; } = new();

    /// <summary>When true every call answers 503: the partner got the request, and what it did is unknown.</summary>
    public bool Down { get; set; }

    /// <summary>When true every call fails before reaching the partner, as a refused connection does.</summary>
    public bool Unreachable { get; set; }

    /// <summary>Runs first on every call: to hold a call (a slow partner) or move the fake clock while it is "on the wire".</summary>
    public Func<HttpRequestMessage, Task>? OnRequest { get; set; }

    public IReadOnlyList<OrderCall> Calls => [.. _calls];

    public IReadOnlyList<OrderCall> CallsFor(string reference) => [.. _calls.Where(c => c.Reference == reference)];

    protected abstract Task<(HttpResponseMessage Response, string? Reference)> AnswerAsync(HttpRequestMessage request, string body);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        if (OnRequest is { } onRequest)
        {
            await onRequest(request);
        }

        if (Unreachable)
        {
            _calls.Enqueue(new OrderCall(request.Method.Method, request.RequestUri!.AbsolutePath, ReferenceOf(request, body), body));
            throw new HttpRequestException(HttpRequestError.ConnectionError,
                "No connection could be made because the target machine actively refused it.");
        }

        if (Down)
        {
            _calls.Enqueue(new OrderCall(request.Method.Method, request.RequestUri!.AbsolutePath, ReferenceOf(request, body), body));
            return Respond(request, Status(HttpStatusCode.ServiceUnavailable));
        }

        var (response, reference) = await AnswerAsync(request, body);
        _calls.Enqueue(new OrderCall(request.Method.Method, request.RequestUri!.AbsolutePath, reference, body));
        if (reference is not null && LoseAnswers.TryGetValue(reference, out var left) && left > 0)
        {
            LoseAnswers[reference] = left - 1;

            // What HttpClient throws when its timeout cancels the request.
            throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout of 10 seconds elapsing.");
        }

        return Respond(request, response);
    }

    protected virtual string? ReferenceOf(HttpRequestMessage request, string body) => null;

    protected override void Dispose(bool disposing)
    {
    }

    protected static HttpResponseMessage Status(HttpStatusCode status) => new(status) { Content = new ByteArrayContent([]) };

    private static HttpResponseMessage Respond(HttpRequestMessage request, HttpResponseMessage response)
    {
        response.RequestMessage = request;
        return response;
    }
}

/// <summary>
/// Harbor's <c>POST /v2/orders</c> written from the contract: 201 with a new order, 200 with the original for a used
/// external_ref, 409 INSUFFICIENT_STOCK, 422 INVALID_ITEM. Its order ids are numbers-as-strings unlike the stand-in's.
/// </summary>
public sealed class FakeHarborOrders : FakeOrderPartner
{
    public const string Key = "it-orders-harbor-key";

    private readonly ConcurrentDictionary<string, string> _orders = new();
    private int _nextId = 70000;

    /// <summary>Units Harbor has per item code; items not listed have plenty.</summary>
    public ConcurrentDictionary<string, int> Available { get; } = new();

    /// <summary>Item codes Harbor does not sell (unknown, discontinued or unpriced): 422.</summary>
    public ConcurrentDictionary<string, bool> Invalid { get; } = new();

    /// <summary>Orders Harbor holds, by external_ref.</summary>
    public IReadOnlyDictionary<string, string> Orders => _orders;

    protected override string? ReferenceOf(HttpRequestMessage request, string body) => TryRead(body)?.Ref;

    protected override Task<(HttpResponseMessage Response, string? Reference)> AnswerAsync(HttpRequestMessage request, string body)
    {
        if (!request.Headers.TryGetValues("X-Harbor-Key", out var keys) || keys.Single() != Key)
        {
            return Task.FromResult((Error(HttpStatusCode.Unauthorized, "UNAUTHORIZED", "Missing or invalid API key."), (string?)null));
        }

        if (request.Method != HttpMethod.Post || request.RequestUri!.AbsolutePath != "/v2/orders")
        {
            return Task.FromResult((Error(HttpStatusCode.NotFound, "NOT_FOUND", "No such route."), (string?)null));
        }

        if (TryRead(body) is not { } order)
        {
            return Task.FromResult((Error(HttpStatusCode.BadRequest, "INVALID_REQUEST", "Malformed order."), (string?)null));
        }

        lock (_orders)
        {
            if (_orders.TryGetValue(order.Ref, out var existing))
            {
                return Task.FromResult((Json(HttpStatusCode.OK, new JsonObject { ["order_id"] = existing, ["status"] = "ACCEPTED" }), (string?)order.Ref));
            }

            if (Invalid.ContainsKey(order.Item))
            {
                return Task.FromResult((Error(HttpStatusCode.UnprocessableEntity, "INVALID_ITEM", $"Item {order.Item} is discontinued."), (string?)order.Ref));
            }

            if (Available.TryGetValue(order.Item, out var available) && available < order.Qty)
            {
                var error = new JsonObject
                {
                    ["error"] = new JsonObject
                    {
                        ["code"] = "INSUFFICIENT_STOCK", ["message"] = "Not enough stock.", ["item_code"] = order.Item, ["available"] = available,
                    },
                };
                return Task.FromResult((Json(HttpStatusCode.Conflict, error), (string?)order.Ref));
            }

            var id = Interlocked.Increment(ref _nextId).ToString(System.Globalization.CultureInfo.InvariantCulture);
            _orders[order.Ref] = id;
            return Task.FromResult((Json(HttpStatusCode.Created, new JsonObject { ["order_id"] = id, ["status"] = "ACCEPTED" }), (string?)order.Ref));
        }
    }

    private static (string Ref, string Item, int Qty)? TryRead(string body)
    {
        try
        {
            var root = JsonNode.Parse(body)!.AsObject();
            var line = root["lines"]!.AsArray().Single()!.AsObject();
            return ((string)root["external_ref"]!, (string)line["item_code"]!, (int)line["qty"]!);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or NullReferenceException or FormatException)
        {
            return null;
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, JsonNode body) =>
        new(status) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Error(HttpStatusCode status, string code, string message) =>
        Json(status, new JsonObject { ["error"] = new JsonObject { ["code"] = code, ["message"] = message } });
}

/// <summary>
/// Kestrel's <c>POST /orders</c> and <c>GET /orders/{ticket}</c> written from the contract: 202 RECEIVED with a ticket,
/// 409 DUPLICATE_REFERENCE with the ticket, 400 for unknown refs; the decision per product ref is set by the test
/// (RECEIVED until then). Its tickets look nothing like the stand-in's KT-5521.
/// </summary>
public sealed class FakeKestrelOrders : FakeOrderPartner
{
    public const string User = "it-orders-user";
    public const string Password = "it-orders-secret";

    private readonly ConcurrentDictionary<string, string> _ticketsByReference = new();
    private readonly ConcurrentDictionary<string, string> _refByTicket = new();

    /// <summary>The state (and reason) Kestrel reports for orders of a product ref; RECEIVED when not set.</summary>
    public ConcurrentDictionary<string, (string State, string? Reason)> Decisions { get; } = new();

    /// <summary>Makes the ticket for a new order; tickets are opaque, any length or shape the partner likes.</summary>
    public Func<string> NewTicket { get; set; } = () => $"tk_{Guid.NewGuid():N}"[..14];

    /// <summary>Product refs Kestrel does not know: 400 INVALID_REQUEST, checked before the reference, as the stand-in does.</summary>
    public ConcurrentDictionary<string, bool> Unknown { get; } = new();

    public IReadOnlyDictionary<string, string> TicketsByReference => _ticketsByReference;

    /// <summary>Kestrel loses the ticket of this reference: polling it answers 404 (a resend still finds the reference).</summary>
    public void ForgetTicket(string reference) => _refByTicket.TryRemove(_ticketsByReference[reference], out _);

    protected override string? ReferenceOf(HttpRequestMessage request, string body) =>
        TryRead(body)?.Reference ?? ReferenceOfTicket(request);

    protected override Task<(HttpResponseMessage Response, string? Reference)> AnswerAsync(HttpRequestMessage request, string body)
    {
        var expected = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{User}:{Password}"));
        if (request.Headers.Authorization?.ToString() != expected)
        {
            return Task.FromResult((Status(HttpStatusCode.Unauthorized), (string?)null));
        }

        var path = request.RequestUri!.AbsolutePath;
        if (request.Method == HttpMethod.Post && path == "/orders")
        {
            if (TryRead(body) is not { } order)
            {
                return Task.FromResult((Error("INVALID_REQUEST", "Malformed purchase order"), (string?)null));
            }

            if (Unknown.ContainsKey(order.Ref))
            {
                return Task.FromResult((Error("INVALID_REQUEST", $"Unknown ref: {order.Ref}"), (string?)order.Reference));
            }

            lock (_ticketsByReference)
            {
                if (_ticketsByReference.TryGetValue(order.Reference, out var existing))
                {
                    return Task.FromResult((Xml(HttpStatusCode.Conflict, new XElement("Error", new XAttribute("code", "DUPLICATE_REFERENCE"), new XAttribute("ticket", existing))),
                        (string?)order.Reference));
                }

                var ticket = NewTicket();
                _ticketsByReference[order.Reference] = ticket;
                _refByTicket[ticket] = order.Ref;
                return Task.FromResult((Xml(HttpStatusCode.Accepted, new XElement("OrderAck", new XAttribute("status", "RECEIVED"), new XAttribute("ticket", ticket))),
                    (string?)order.Reference));
            }
        }

        if (request.Method == HttpMethod.Get && path.StartsWith("/orders/", StringComparison.Ordinal))
        {
            var ticket = Uri.UnescapeDataString(path["/orders/".Length..]);
            if (!_refByTicket.TryGetValue(ticket, out var productRef))
            {
                return Task.FromResult((Status(HttpStatusCode.NotFound), ReferenceOfTicket(request)));
            }

            var (state, reason) = Decisions.TryGetValue(productRef, out var decision) ? decision : ("RECEIVED", null);
            var status = new XElement("OrderStatus", new XAttribute("ticket", ticket), new XAttribute("state", state),
                reason is null ? null : new XAttribute("reason", reason));
            return Task.FromResult((Xml(HttpStatusCode.OK, status), ReferenceOfTicket(request)));
        }

        return Task.FromResult((Status(HttpStatusCode.NotFound), (string?)null));
    }

    private string? ReferenceOfTicket(HttpRequestMessage request)
    {
        var path = request.RequestUri!.AbsolutePath;
        if (!path.StartsWith("/orders/", StringComparison.Ordinal))
        {
            return null;
        }

        var ticket = Uri.UnescapeDataString(path["/orders/".Length..]);
        return _ticketsByReference.FirstOrDefault(kv => kv.Value == ticket).Key;
    }

    private static (string Reference, string Ref, int Qty)? TryRead(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            var root = XDocument.Parse(body).Root!;
            var line = root.Elements("Line").Single();
            return (root.Attribute("reference")!.Value, line.Attribute("ref")!.Value, int.Parse(line.Attribute("qty")!.Value, System.Globalization.CultureInfo.InvariantCulture));
        }
        catch (Exception e) when (e is System.Xml.XmlException or InvalidOperationException or NullReferenceException or FormatException)
        {
            return null;
        }
    }

    private static HttpResponseMessage Xml(HttpStatusCode status, XElement body) =>
        new(status) { Content = new StringContent(body.ToString(SaveOptions.DisableFormatting), Encoding.UTF8, "application/xml") };

    private static HttpResponseMessage Error(string code, string message) =>
        Xml(HttpStatusCode.BadRequest, new XElement("Error", new XAttribute("code", code), new XAttribute("message", message)));
}
