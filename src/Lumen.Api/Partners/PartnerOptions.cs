using System.ComponentModel.DataAnnotations;
using Lumen.Domain.Catalog;

namespace Lumen.Api.Partners;

/// <summary>
/// Settings every partner adapter has (section <c>Partners:{Partner}</c>). A partner whose <see cref="BaseUrl"/> is
/// empty is "not configured": it is listed on the admin page but its sync job does not run.
/// </summary>
public abstract class PartnerOptions : IValidatableObject
{
    /// <summary>The partner's display name (admin page, storefront seller name).</summary>
    [Required]
    public string Name { get; set; } = "";

    /// <summary>Root of the partner's API, e.g. <c>http://harbor:8080</c>. Paths from the contract are appended to it.</summary>
    public string? BaseUrl { get; set; }

    /// <summary>
    /// Partner category code → Lumen category. Codes are compared trimmed and case-insensitively; products in a code
    /// that is not listed here, or mapped to an empty value (to hide a code by configuration), are not shown in the shop.
    /// </summary>
    public Dictionary<string, string> CategoryMap { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>How often the catalogue is checked for changes.</summary>
    public TimeSpan CatalogueInterval { get; set; }

    /// <summary>How often a full catalogue pass runs (reconciles removals and anything an incremental pass missed).</summary>
    public TimeSpan FullSyncInterval { get; set; }

    /// <summary>
    /// For partners whose catalogue carries no stock (Kestrel): how often stock is re-read for every listed product.
    /// Stock is also read right after a catalogue change and on "Refresh now".
    /// </summary>
    public TimeSpan StockInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// BR-SYNC-02 mass-deletion guard: removals from a full catalogue are held back (the rest is applied) when they are
    /// at least <see cref="MinRemovalsToHold"/> products AND more than this share of the listed ones.
    /// </summary>
    [Range(0.01, 1.0)]
    public double MaxRemovalFraction { get; set; } = 0.5;

    /// <summary>BR-SYNC-02: below this many removals the guard never holds, so a small catalogue can shrink normally.</summary>
    [Range(1, 1_000_000)]
    public int MinRemovalsToHold { get; set; } = 10;

    /// <summary>
    /// Extra origins (scheme://host[:port]) partner images may be fetched from, e.g. a partner CDN. The BaseUrl origin is
    /// always allowed and is the only one that receives the partner's credentials.
    /// </summary>
    public List<string> ImageOrigins { get; set; } = [];

    public PartnerResilienceOptions Resilience { get; set; } = new();

    /// <summary>The base URL with a trailing slash (so relative paths append to it), or null when not configured.</summary>
    public Uri? BaseUri => PartnerUrls.ParseBaseUrl(BaseUrl);

    public bool IsConfigured => BaseUri is not null;

    /// <summary>Origins partner images may come from: the BaseUrl origin plus <see cref="ImageOrigins"/>.</summary>
    public IReadOnlyList<Uri> AllowedImageOrigins() =>
        [.. new[] { BaseUri }.Concat(ImageOrigins.Select(PartnerUrls.ParseBaseUrl)).OfType<Uri>()];

    public virtual IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!string.IsNullOrWhiteSpace(BaseUrl) && BaseUri is null)
        {
            yield return new ValidationResult($"BaseUrl '{BaseUrl}' must be an absolute http or https URL.", [nameof(BaseUrl)]);
        }

        foreach (var (code, category) in CategoryMap)
        {
            if (!string.IsNullOrWhiteSpace(category) && !ProductCategories.IsValid(category.Trim()))
            {
                yield return new ValidationResult(
                    $"CategoryMap maps '{code}' to '{category}', which is not one of: {string.Join(", ", ProductCategories.All)}.",
                    [nameof(CategoryMap)]);
            }
        }

        foreach (var origin in ImageOrigins.Where(o => PartnerUrls.ParseBaseUrl(o) is null))
        {
            yield return new ValidationResult($"ImageOrigins entry '{origin}' must be an absolute http or https URL.", [nameof(ImageOrigins)]);
        }

        if (CatalogueInterval <= TimeSpan.Zero || FullSyncInterval <= TimeSpan.Zero || StockInterval <= TimeSpan.Zero)
        {
            yield return new ValidationResult("CatalogueInterval, FullSyncInterval and StockInterval must be positive.", [nameof(CatalogueInterval)]);
        }

        foreach (var result in Resilience.Validate())
        {
            yield return result;
        }
    }
}

/// <summary>Per-partner HTTP resilience (Microsoft.Extensions.Http.Resilience pipeline). Retries apply to GETs only.</summary>
public sealed class PartnerResilienceOptions
{
    /// <summary>One HTTP attempt. Measures the partner only: time spent queued for a rate-limit token is outside it.</summary>
    public TimeSpan AttemptTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>All attempts, back-off and Retry-After waits together.</summary>
    public TimeSpan TotalTimeout { get; set; } = TimeSpan.FromSeconds(60);

    public int MaxRetries { get; set; } = 3;

    /// <summary>First back-off delay; doubles per retry, with jitter. A Retry-After header overrides it.</summary>
    public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Circuit breaker: opens when this share of attempts fails within the sampling window.</summary>
    public double BreakerFailureRatio { get; set; } = 0.5;

    public int BreakerMinimumThroughput { get; set; } = 10;

    public TimeSpan BreakerSamplingDuration { get; set; } = TimeSpan.FromSeconds(60);

    public TimeSpan BreakerBreakDuration { get; set; } = TimeSpan.FromSeconds(30);

    internal IEnumerable<ValidationResult> Validate()
    {
        if (AttemptTimeout <= TimeSpan.Zero || TotalTimeout <= AttemptTimeout)
        {
            yield return new ValidationResult("Resilience: AttemptTimeout must be positive and TotalTimeout longer than it.");
        }

        if (MaxRetries is < 0 or > 10 || RetryBaseDelay < TimeSpan.Zero)
        {
            yield return new ValidationResult("Resilience: MaxRetries must be 0 to 10 and RetryBaseDelay not negative.");
        }

        if (BreakerFailureRatio is <= 0 or > 1 || BreakerMinimumThroughput < 2
            || BreakerSamplingDuration < TimeSpan.FromSeconds(1) || BreakerBreakDuration < TimeSpan.FromSeconds(1))
        {
            yield return new ValidationResult("Resilience: circuit breaker settings are out of range.");
        }
    }
}

/// <summary>Settings shared by all partners (section <c>Partners</c>).</summary>
public sealed class PartnersOptions
{
    public const string SectionName = "Partners";

    /// <summary>
    /// Attribute key synonyms applied after camelCasing (e.g. <c>colour</c> → <c>color</c>), on top of
    /// <see cref="AttributeKeys.DefaultSynonyms"/>.
    /// </summary>
    public Dictionary<string, string> AttributeKeySynonyms { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The effective synonym table: the defaults, overridden by configuration.</summary>
    public IReadOnlyDictionary<string, string> EffectiveSynonyms()
    {
        var synonyms = new Dictionary<string, string>(AttributeKeys.DefaultSynonyms, StringComparer.OrdinalIgnoreCase);
        foreach (var (from, to) in AttributeKeySynonyms)
        {
            synonyms[from] = to;
        }

        return synonyms;
    }
}

/// <summary>Background jobs (section <c>Jobs</c>). Tests switch them off and call the jobs' RunOnceAsync directly.</summary>
public sealed class JobsOptions
{
    public const string SectionName = "Jobs";

    public bool Enabled { get; set; } = true;

    /// <summary>How often each partner job wakes to see whether work is due or "Refresh now" was pressed.</summary>
    [Range(typeof(TimeSpan), "00:00:01", "00:05:00")]
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);
}

/// <summary>Currency conversion (section <c>Fx</c>). A fixed configured rate (assumption A-07).</summary>
public sealed class FxOptions
{
    public const string SectionName = "Fx";

    /// <summary>USD per 1 EUR.</summary>
    [Range(typeof(decimal), "0.0001", "1000")]
    public decimal EurToUsd { get; set; } = 1.0850m;
}

/// <summary>Image proxy (section <c>Partners:Media</c>).</summary>
public sealed class MediaOptions
{
    public const string SectionName = "Partners:Media";

    [Range(1024, 50 * 1024 * 1024)]
    public long MaxImageBytes { get; set; } = 5 * 1024 * 1024;

    /// <summary>Total bytes of partner images kept in memory.</summary>
    [Range(1024 * 1024, 4L * 1024 * 1024 * 1024)]
    public long CacheSizeLimitBytes { get; set; } = 100 * 1024 * 1024;

    [Range(typeof(TimeSpan), "00:00:01", "7.00:00:00")]
    public TimeSpan CacheDuration { get; set; } = TimeSpan.FromHours(24);

    /// <summary>A failed fetch is remembered this long, so a down partner costs one slow request per image, not one per page view.</summary>
    [Range(typeof(TimeSpan), "00:00:01", "01:00:00")]
    public TimeSpan FailureCacheDuration { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>Longest a browser waits for a partner image before it gets a 502 (and shows a placeholder).</summary>
    [Range(typeof(TimeSpan), "00:00:01", "00:05:00")]
    public TimeSpan ResponseTimeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Longest one partner image fetch may take, waiting for a Harbor rate-limit token included. It outlives a browser
    /// that gave up, so the image is cached for the next view.
    /// </summary>
    [Range(typeof(TimeSpan), "00:00:01", "00:30:00")]
    public TimeSpan MaxFetchDuration { get; set; } = TimeSpan.FromSeconds(90);
}
