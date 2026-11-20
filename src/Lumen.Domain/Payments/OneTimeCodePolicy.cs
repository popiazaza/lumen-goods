using System.Security.Cryptography;
using System.Text;

namespace Lumen.Domain.Payments;

public enum OneTimeCodeResult
{
    Approved,
    Declined,
    Expired,
}

/// <summary>
/// BR-PAY-03: the one-time code for card 0034. The configured code approves until the code expires (5 minutes after
/// it was issued, on the server clock); any other code declines; any code at or after expiry is Expired.
/// A new payment attempt issues a new code, so the caller passes that attempt's issue time.
/// </summary>
public sealed class OneTimeCodePolicy(PaymentOptions options)
{
    private readonly PaymentOptions _options = DomainOptions.Validated(options);

    /// <summary>Shown on the payment page ("test code: 123456"): the mock sends no SMS.</summary>
    public string TestCode => _options.OneTimeCode;

    public DateTimeOffset ExpiresAt(DateTimeOffset issuedAt) => issuedAt + _options.OneTimeCodeLifetime;

    /// <param name="now">The server clock (pass <c>TimeProvider.GetUtcNow()</c>), never the browser's.</param>
    public OneTimeCodeResult Check(DateTimeOffset issuedAt, DateTimeOffset now, string? submittedCode)
    {
        if (now >= ExpiresAt(issuedAt))
        {
            return OneTimeCodeResult.Expired;
        }

        // Constant-time comparison, so response timing reveals nothing about how many leading digits were right.
        var expected = Encoding.UTF8.GetBytes(_options.OneTimeCode);
        var submitted = Encoding.UTF8.GetBytes(submittedCode?.Trim() ?? "");
        return CryptographicOperations.FixedTimeEquals(expected, submitted) ? OneTimeCodeResult.Approved : OneTimeCodeResult.Declined;
    }
}
