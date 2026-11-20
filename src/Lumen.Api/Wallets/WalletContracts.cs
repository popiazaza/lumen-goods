using System.Text.Json.Serialization;
using Lumen.Api.Infrastructure.Json;

namespace Lumen.Api.Wallets;

/// <summary>
/// <c>GET /api/v1/wallet</c> (signed-in customer): the balance and the most recent transactions, newest first. The
/// payment page compares <see cref="Balance"/> with the order total to enable the wallet option or say why not (BR-PAY-06).
/// </summary>
/// <param name="Currency">Always USD.</param>
/// <example>
/// <code>
/// { "balance": 1924.54, "currency": "USD", "recentTransactions": [
///   { "id": "0193a5f8-...", "createdUtc": "2026-11-20T08:01:10Z", "kind": "payment", "amount": -75.46, "balanceAfter": 1924.54, "orderNumber": "LG-7K3M9QX2" },
///   { "id": "0193a0aa-...", "createdUtc": "2026-11-20T06:00:00Z", "kind": "seed", "amount": 2000.00, "balanceAfter": 2000.00, "orderNumber": null } ] }
/// </code>
/// </example>
public sealed record WalletDto(
    [property: JsonConverter(typeof(TwoDecimalJsonConverter))] decimal Balance,
    string Currency,
    IReadOnlyList<WalletTransactionDto> RecentTransactions);

/// <param name="Kind">seed | payment | refund.</param>
/// <param name="Amount">Signed: payments are negative, the opening balance and refunds positive.</param>
/// <param name="OrderNumber">The order paid or refunded; null for the opening balance.</param>
public sealed record WalletTransactionDto(
    Guid Id,
    DateTime CreatedUtc,
    WalletTransactionKind Kind,
    [property: JsonConverter(typeof(TwoDecimalJsonConverter))] decimal Amount,
    [property: JsonConverter(typeof(TwoDecimalJsonConverter))] decimal BalanceAfter,
    string? OrderNumber);
