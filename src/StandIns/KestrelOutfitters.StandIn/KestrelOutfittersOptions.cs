using Microsoft.Extensions.Options;

namespace KestrelOutfitters.StandIn;

/// <summary>
/// Every default is the published contract value. Overrides exist so tests and demos can make the quirks
/// deterministic (for example <c>KestrelOutfitters__FeedFailureRate=0</c>) without changing the code path.
/// </summary>
internal sealed class KestrelOutfittersOptions
{
    // Not "Kestrel": that configuration section belongs to ASP.NET Core's Kestrel web server.
    public const string SectionName = "KestrelOutfitters";

    public string Username { get; set; } = "lumen";

    public string Password { get; set; } = "kestrel-secret";

    /// <summary>Relative paths resolve against the working directory (<c>/app</c> in the container).</summary>
    public string DatabasePath { get; set; } = "data/kestrel.db";

    /// <summary>Base of the picture URLs in the feed. Empty: the scheme and host of the incoming request.</summary>
    public string? PublicBaseUrl { get; set; }

    public TimeSpan FeedLatencyMin { get; set; } = TimeSpan.FromSeconds(2);

    public TimeSpan FeedLatencyMax { get; set; } = TimeSpan.FromSeconds(4);

    /// <summary>Share of authenticated feed requests answered with 503 ("about 1 in 10").</summary>
    public double FeedFailureRate { get; set; } = 0.1;

    public TimeSpan OrderDecisionDelayMin { get; set; } = TimeSpan.FromSeconds(10);

    public TimeSpan OrderDecisionDelayMax { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>How often the background service looks for orders whose decision time has passed.</summary>
    public TimeSpan OrderDecisionPollInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Fixes the sequence of latencies, 503s and decision delays. Empty: a different sequence per run.</summary>
    public int? RandomSeed { get; set; }

    public static KestrelOutfittersOptions From(IConfiguration configuration) =>
        configuration.GetSection(SectionName).Get<KestrelOutfittersOptions>() ?? new KestrelOutfittersOptions();
}

internal sealed class KestrelOutfittersOptionsValidator : IValidateOptions<KestrelOutfittersOptions>
{
    public ValidateOptionsResult Validate(string? name, KestrelOutfittersOptions options)
    {
        var errors = new List<string>();

        // RFC 7617: the user-id of Basic credentials cannot contain a colon.
        if (string.IsNullOrEmpty(options.Username) || options.Username.Contains(':', StringComparison.Ordinal))
        {
            errors.Add("Username is required and must not contain ':'.");
        }

        if (string.IsNullOrEmpty(options.Password))
        {
            errors.Add("Password is required.");
        }

        if (string.IsNullOrWhiteSpace(options.DatabasePath))
        {
            errors.Add("DatabasePath is required.");
        }

        if (!string.IsNullOrEmpty(options.PublicBaseUrl) && !Uri.TryCreate(options.PublicBaseUrl, UriKind.Absolute, out _))
        {
            errors.Add("PublicBaseUrl must be an absolute URL.");
        }

        CheckRange(errors, "FeedLatency", options.FeedLatencyMin, options.FeedLatencyMax);
        CheckRange(errors, "OrderDecisionDelay", options.OrderDecisionDelayMin, options.OrderDecisionDelayMax);

        if (!(options.FeedFailureRate is >= 0 and <= 1))
        {
            errors.Add("FeedFailureRate must be between 0 and 1.");
        }

        if (options.OrderDecisionPollInterval <= TimeSpan.Zero)
        {
            errors.Add("OrderDecisionPollInterval must be positive.");
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }

    private static void CheckRange(List<string> errors, string name, TimeSpan min, TimeSpan max)
    {
        if (min < TimeSpan.Zero || max < min)
        {
            errors.Add($"{name}Min must be zero or more and {name}Max must not be less than {name}Min.");
        }
    }
}
