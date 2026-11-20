using System.ComponentModel.DataAnnotations;

namespace Lumen.Api.Payments;

/// <summary>
/// Settings of the payment window job and the refund job (section <c>PaymentJobs</c>; every value has a working
/// default). The payment rules themselves (hold, code lifetime, retry window, attempts) are <c>Payments</c>.
/// </summary>
public sealed class PaymentJobsOptions
{
    public const string SectionName = "PaymentJobs";

    /// <summary>
    /// A card charge still Processing this long after the slow-card delay (<c>Payments:SlowCardDelay</c>) has lost its
    /// worker (a restart, a crash); the job then asks the gateway what happened to it instead of waiting forever.
    /// </summary>
    [Range(typeof(TimeSpan), "00:00:05", "01:00:00")]
    public TimeSpan StaleChargeGrace { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>Most payments, orders or refunds one job run handles; the rest wait for the next run.</summary>
    [Range(1, 1000)]
    public int BatchSize { get; init; } = 50;

    /// <summary>
    /// How long a card refund claimed by a job run is left alone. If the process dies after calling the gateway, the
    /// refund is picked up again after this and resent with the same refund id, which the gateway answers with the
    /// first result.
    /// </summary>
    [Range(typeof(TimeSpan), "00:00:10", "01:00:00")]
    public TimeSpan RefundLease { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>First wait before a failed refund is retried (same refund id); doubles per attempt up to <see cref="RefundMaxRetryDelay"/>.</summary>
    [Range(typeof(TimeSpan), "00:00:01", "01:00:00")]
    public TimeSpan RefundRetryDelay { get; init; } = TimeSpan.FromMinutes(1);

    [Range(typeof(TimeSpan), "00:00:01", "1.00:00:00")]
    public TimeSpan RefundMaxRetryDelay { get; init; } = TimeSpan.FromHours(1);
}
