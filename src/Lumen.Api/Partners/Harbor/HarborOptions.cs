using System.ComponentModel.DataAnnotations;

namespace Lumen.Api.Partners.Harbor;

/// <summary>Harbor Supply Co. (section <c>Partners:Harbor</c>).</summary>
public sealed class HarborOptions : PartnerOptions
{
    public const string SectionName = "Partners:Harbor";

    public HarborOptions()
    {
        Name = "Harbor Supply Co.";
        CatalogueInterval = TimeSpan.FromSeconds(60);
        FullSyncInterval = TimeSpan.FromMinutes(15);
        Resilience = new PartnerResilienceOptions { AttemptTimeout = TimeSpan.FromSeconds(10), TotalTimeout = TimeSpan.FromSeconds(90) };
    }

    /// <summary>Sent as X-Harbor-Key, only to Harbor's own origin.</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>The contract allows 1 to 25; Lumen always sends it explicitly.</summary>
    [Range(1, 25)]
    public int PageSize { get; set; } = 25;

    /// <summary>A pass that needs more pages than this is abandoned (guards against a cursor that never ends).</summary>
    [Range(1, 1_000_000)]
    public int MaxPagesPerPass { get; set; } = 4000;

    /// <summary>
    /// Incremental passes ask for changes since (newest updated_at seen − this overlap), covering clock skew, second
    /// truncation and items that changed while a pass was paging. Re-reading an item is harmless: upserts are idempotent.
    /// </summary>
    public TimeSpan WatermarkOverlap { get; set; } = TimeSpan.FromMinutes(5);

    public HarborRateLimitOptions RateLimit { get; set; } = new();

    public override IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        foreach (var result in base.Validate(validationContext))
        {
            yield return result;
        }

        if (IsConfigured && string.IsNullOrWhiteSpace(ApiKey))
        {
            yield return new ValidationResult("Partners:Harbor:ApiKey is required when BaseUrl is set.", [nameof(ApiKey)]);
        }

        if (WatermarkOverlap < TimeSpan.Zero)
        {
            yield return new ValidationResult("WatermarkOverlap must not be negative.", [nameof(WatermarkOverlap)]);
        }

        if (RateLimit.RequestsPerMinute is < 1 or > 6000 || RateLimit.Burst < 1 || RateLimit.QueueLimit < 0)
        {
            yield return new ValidationResult("RateLimit: RequestsPerMinute 1 to 6000, Burst at least 1, QueueLimit not negative.", [nameof(RateLimit)]);
        }
    }
}

/// <summary>
/// Client-side token bucket shared by every Harbor call in this process (catalogue, images, later orders). Harbor
/// allows 60 requests per minute per key; Burst + RequestsPerMinute must stay at or below that so no 60-second window,
/// however it is aligned, sees more than 60 requests: 10 + 50 = 60.
/// </summary>
public sealed class HarborRateLimitOptions
{
    public int RequestsPerMinute { get; set; } = 50;

    public int Burst { get; set; } = 10;

    /// <summary>Requests waiting for a token; beyond this a call fails fast instead of queueing.</summary>
    public int QueueLimit { get; set; } = 1000;
}
