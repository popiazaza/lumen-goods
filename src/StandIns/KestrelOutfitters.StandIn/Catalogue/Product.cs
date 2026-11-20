namespace KestrelOutfitters.StandIn.Catalogue;

/// <summary>A Kestrel catalogue entry plus its stock (stock is served by /stock, never by the feed).</summary>
internal sealed class Product
{
    public required string Ref { get; init; }

    public string? NameDe { get; init; }

    public string? NameEn { get; init; }

    /// <summary>Partner HTML, deliberately not guaranteed to be safe (the contract says so).</summary>
    public string? DescDe { get; init; }

    public string? DescEn { get; init; }

    public required string Group { get; init; }

    public decimal PriceEur { get; set; }

    public bool HasThumb { get; init; }

    public bool HasLarge { get; init; }

    public List<ProductSpec> Specs { get; init; } = [];

    public bool Active { get; set; } = true;

    public int Units { get; set; }

    public DateOnly? RestockDate { get; set; }
}

internal sealed record ProductSpec(string Key, string Value);

/// <summary>Single row holding when the catalogue (not the stock) last changed: the feed's Last-Modified.</summary>
internal sealed class CatalogueState
{
    public int Id { get; init; } = 1;

    public DateTimeOffset LastModified { get; set; }

    /// <summary>
    /// HTTP dates have whole-second precision, so every change moves Last-Modified to a strictly later second.
    /// Otherwise a change made in the same second as Lumen's last fetch would be answered with 304 forever.
    /// </summary>
    public void RecordChange(DateTimeOffset now)
    {
        var second = ToHttpDatePrecision(now);
        LastModified = second > LastModified ? second : LastModified.AddSeconds(1);
    }

    public static DateTimeOffset ToHttpDatePrecision(DateTimeOffset value) =>
        new(value.UtcTicks - (value.UtcTicks % TimeSpan.TicksPerSecond), TimeSpan.Zero);
}
