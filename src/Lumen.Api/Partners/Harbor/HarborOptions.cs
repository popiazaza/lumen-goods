using System.ComponentModel.DataAnnotations;

namespace Lumen.Api.Partners.Harbor;

/// <summary>Harbor Supply Co. (section <c>Partners:Harbor</c>).</summary>
public sealed class HarborOptions : PartnerOptions
{
    public const string SectionName = "Partners:Harbor";

    /// <summary>Harbor's published limit: 60 requests per minute per key, whatever they are for.</summary>
    public const int ContractRequestsPerMinute = 60;

    public HarborOptions()
    {
        Name = "Harbor Supply Co.";
        CatalogueInterval = TimeSpan.FromSeconds(60);
        // BR-SYNC-01: removals only show in a full pass; 10 min + a 60 s tick + the run stays inside 15 min.
        FullSyncInterval = TimeSpan.FromMinutes(10);
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
    /// BR-CHK-02: at most this many lines of one Place order are checked live. Each check is a GET through the shared
    /// rate limiter (one token every 1.5 s, burst 5), and the whole check has <c>Checkout:PartnerCheckTimeout</c> (5 s),
    /// so about 8 calls fit when the bucket is full and fewer just after a sync: 6 leaves that margin. The tightest
    /// lines go first; the rest come back NotChecked and are ordered only on a fresh sync (<c>Checkout:SyncedDataMaxAge</c>).
    /// </summary>
    [Range(1, 50)]
    public int MaxLiveChecks { get; set; } = 6;

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

        if (RateLimit.RequestsPerMinute < 1 || RateLimit.Burst < 1 || RateLimit.ImageRequestsPerMinute < 1 || RateLimit.ImageBurst < 1
            || RateLimit.QueueLimit < 0 || RateLimit.ImageQueueLimit < 0)
        {
            yield return new ValidationResult("RateLimit: rates and bursts must be at least 1 and queue limits not negative.", [nameof(RateLimit)]);
        }

        if (RateLimit.WorstCasePerMinute > ContractRequestsPerMinute)
        {
            yield return new ValidationResult(
                $"RateLimit: Burst + RequestsPerMinute + ImageBurst + ImageRequestsPerMinute is {RateLimit.WorstCasePerMinute}, "
                + $"more than Harbor's {ContractRequestsPerMinute} requests per minute.",
                [nameof(RateLimit)]);
        }
    }
}

/// <summary>
/// Client-side token buckets for Harbor's one budget of 60 requests per minute per key, split in two so images can never
/// starve the catalogue sync or (later) order calls: those share the main bucket (40/min, burst 5); image fetches have
/// their own, smaller bucket (10/min, burst 5); during a burst of uncached images a browser's request waits at most
/// Partners:Media:RateLimitWait for a token and otherwise gets a 503, and warming takes only spare tokens. A token bucket
/// lets at most burst + rate through in any 60-second window, however it is aligned, so the two together stay at
/// (5 + 40) + (5 + 10) = 60. Validation refuses settings that would exceed Harbor's limit.
/// </summary>
public sealed class HarborRateLimitOptions
{
    /// <summary>Catalogue sync and order calls.</summary>
    public int RequestsPerMinute { get; set; } = 40;

    public int Burst { get; set; } = 5;

    /// <summary>Requests waiting for a main-bucket token; beyond this a call fails fast instead of queueing.</summary>
    public int QueueLimit { get; set; } = 1000;

    /// <summary>Partner image fetches (the image proxy).</summary>
    public int ImageRequestsPerMinute { get; set; } = 10;

    public int ImageBurst { get; set; } = 5;

    public int ImageQueueLimit { get; set; } = 500;

    /// <summary>The most requests both buckets together can let through in any 60-second window.</summary>
    public int WorstCasePerMinute => Burst + RequestsPerMinute + ImageBurst + ImageRequestsPerMinute;
}
