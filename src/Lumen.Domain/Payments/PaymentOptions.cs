using System.ComponentModel.DataAnnotations;

namespace Lumen.Domain.Payments;

/// <summary>Payment mock and payment window settings. Bound from the <c>Payments</c> configuration section.</summary>
public sealed class PaymentOptions
{
    public const string SectionName = "Payments";

    /// <summary>The code that approves a card 0034 payment (BR-PAY-03). The page shows it, as there is no SMS.</summary>
    [Required, RegularExpression("^[0-9]{4,8}$")]
    public string OneTimeCode { get; init; } = "123456";

    /// <summary>How long an issued one-time code stays valid (BR-PAY-03).</summary>
    [Range(typeof(TimeSpan), "00:00:10", "01:00:00")]
    public TimeSpan OneTimeCodeLifetime { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>How long the mock gateway takes to approve card 0042 (BR-PAY-04).</summary>
    [Range(typeof(TimeSpan), "00:00:00", "00:05:00")]
    public TimeSpan SlowCardDelay { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>
    /// How long after a gateway error the one automatic retry may still start (BR-PAY-05). Past it, the payment is no
    /// longer in progress and is closed as Failed, so a retry lost to a crash cannot hold the order forever.
    /// </summary>
    [Range(typeof(TimeSpan), "00:00:01", "00:05:00")]
    public TimeSpan AutomaticRetryWindow { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Payment attempts allowed per order; the last failure expires the order (BR-PAY-03, BR-STOCK-01).</summary>
    [Range(1, 20)]
    public int MaxAttemptsPerOrder { get; init; } = 5;

    /// <summary>How long a placed order holds its prices and reserved stock while awaiting payment (BR-CHK-01, BR-STOCK-01).</summary>
    [Range(typeof(TimeSpan), "00:01:00", "02:00:00")]
    public TimeSpan OrderHold { get; init; } = TimeSpan.FromMinutes(15);
}
