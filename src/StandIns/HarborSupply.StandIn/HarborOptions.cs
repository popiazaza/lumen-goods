using System.ComponentModel.DataAnnotations;

namespace HarborSupply.StandIn;

/// <summary>Settings from the "Harbor" configuration section. Every default is the published contract value.</summary>
internal sealed class HarborOptions
{
    public const string SectionName = "Harbor";

    /// <summary>The value every request must send in the X-Harbor-Key header.</summary>
    [Required(AllowEmptyStrings = false)]
    public string ApiKey { get; set; } = "harbor-dev-key-123";

    /// <summary>Requests allowed per key in each 60-second window.</summary>
    [Range(1, int.MaxValue)]
    public int RequestsPerMinute { get; set; } = 60;

    /// <summary>SQLite file. A relative path resolves against the content root, which is /app in the container.</summary>
    [Required(AllowEmptyStrings = false)]
    public string DatabasePath { get; set; } = "data/harbor.db";
}
