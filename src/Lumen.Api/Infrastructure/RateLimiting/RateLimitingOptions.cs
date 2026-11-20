using System.ComponentModel.DataAnnotations;

namespace Lumen.Api.Infrastructure.RateLimiting;

/// <summary>
/// Inbound rate limits (section <c>RateLimiting</c>). Fixed windows: at most <see cref="WindowLimit.PermitLimit"/>
/// requests per <see cref="WindowLimit.Window"/> per partition; the next one gets 429 with <c>Retry-After</c>.
/// </summary>
public sealed class RateLimitingOptions : IValidatableObject
{
    public const string SectionName = "RateLimiting";

    /// <summary>Sign-in attempts per client IP, right or wrong: slows password guessing and mass lockouts.</summary>
    public WindowLimit SignInPerClient { get; set; } = new() { PermitLimit = 5, Window = TimeSpan.FromMinutes(1) };

    /// <summary>
    /// Sign-ins for one email run one at a time, so Identity's lockout counts every wrong password; this many more wait
    /// their turn, the rest get 429 at once.
    /// </summary>
    [Range(0, 1000)]
    public int SignInQueuePerEmail { get; set; } = 10;

    /// <summary>Registrations per client IP (each new account gets a wallet).</summary>
    public WindowLimit RegisterPerClient { get; set; } = new() { PermitLimit = 5, Window = TimeSpan.FromMinutes(10) };

    /// <summary>Place order per customer: each one checks the partners live and may reserve stock.</summary>
    public WindowLimit PlaceOrderPerCustomer { get; set; } = new() { PermitLimit = 10, Window = TimeSpan.FromMinutes(1) };

    /// <summary>Payment starts per customer: each card payment calls the gateway.</summary>
    public WindowLimit PaymentPerCustomer { get; set; } = new() { PermitLimit = 10, Window = TimeSpan.FromMinutes(1) };

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        foreach (var (name, limit) in new[]
        {
            (nameof(SignInPerClient), SignInPerClient), (nameof(RegisterPerClient), RegisterPerClient),
            (nameof(PlaceOrderPerCustomer), PlaceOrderPerCustomer), (nameof(PaymentPerCustomer), PaymentPerCustomer),
        })
        {
            if (limit is null || limit.PermitLimit is < 1 or > 100_000 || limit.Window < TimeSpan.FromSeconds(1) || limit.Window > TimeSpan.FromDays(1))
            {
                yield return new ValidationResult($"{name}: PermitLimit must be 1 to 100000 and Window 1 second to 1 day.", [name]);
            }
        }
    }
}

public sealed class WindowLimit
{
    public int PermitLimit { get; set; }

    public TimeSpan Window { get; set; }
}
