using System.ComponentModel.DataAnnotations;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Options;

namespace Lumen.Api.Partners.Kestrel;

/// <summary>Kestrel Outfitters (section <c>Partners:Kestrel</c>).</summary>
public sealed class KestrelOptions : PartnerOptions
{
    public const string SectionName = "Partners:Kestrel";

    /// <summary>The contract caps /stock at 20 refs per request.</summary>
    public const int ContractMaxRefsPerStockRequest = 20;

    public KestrelOptions()
    {
        Name = "Kestrel Outfitters";
        CatalogueInterval = TimeSpan.FromMinutes(3);

        // Every 200 is the whole catalogue anyway; this forces one unconditional fetch per hour in case a
        // Last-Modified ever gets stuck.
        FullSyncInterval = TimeSpan.FromHours(1);

        // The feed takes 2 to 4 s and fails about 1 in 10 times with 503: an attempt gets well over 4 s, and three
        // retries make a run fail only about once in 10,000.
        Resilience = new PartnerResilienceOptions { AttemptTimeout = TimeSpan.FromSeconds(15), TotalTimeout = TimeSpan.FromSeconds(75) };
    }

    public string Username { get; set; } = "";

    public string Password { get; set; } = "";

    [Range(1, ContractMaxRefsPerStockRequest)]
    public int StockBatchSize { get; set; } = ContractMaxRefsPerStockRequest;

    /// <summary>A feed larger than this is refused (XmlReader MaxCharactersInDocument).</summary>
    [Range(1024, 2_000_000_000)]
    public long MaxFeedCharacters { get; set; } = 50_000_000;

    /// <summary>Longest a feed download and parse may take, body included.</summary>
    public TimeSpan FeedTimeout { get; set; } = TimeSpan.FromMinutes(2);

    public override IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        foreach (var result in base.Validate(validationContext))
        {
            yield return result;
        }

        if (IsConfigured && (string.IsNullOrEmpty(Username) || string.IsNullOrEmpty(Password)))
        {
            yield return new ValidationResult("Partners:Kestrel:Username and Password are required when BaseUrl is set.", [nameof(Username)]);
        }

        if (FeedTimeout <= TimeSpan.Zero)
        {
            yield return new ValidationResult("FeedTimeout must be positive.", [nameof(FeedTimeout)]);
        }
    }
}

/// <summary>HTTP Basic credentials on requests to Kestrel's origin only (feed picture URLs may point at other hosts).</summary>
internal sealed class KestrelBasicAuthHandler(IOptionsMonitor<KestrelOptions> options) : OriginBoundCredentialsHandler
{
    protected override Uri? PartnerOrigin => options.CurrentValue.BaseUri;

    protected override void AddCredentials(HttpRequestMessage request)
    {
        var settings = options.CurrentValue;
        var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{settings.Username}:{settings.Password}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", token);
    }

    protected override void RemoveCredentials(HttpRequestMessage request) => request.Headers.Authorization = null;
}
