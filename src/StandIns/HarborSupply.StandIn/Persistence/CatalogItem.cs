namespace HarborSupply.StandIn.Persistence;

/// <summary>
/// One Harbor catalogue item. Price, stock and the discontinued flag change only through the Set* methods,
/// which bump <see cref="UpdatedAt"/>: the contract's updated_since filter depends on every change doing so.
/// Items are never deleted; they are discontinued.
/// </summary>
internal sealed class CatalogItem
{
    public required string ItemCode { get; init; }

    public required string Title { get; init; }

    public required string Summary { get; init; }

    /// <summary>BAGS, TEES, OUTERWEAR, KITCHEN, CAMP or MISC.</summary>
    public required string Dept { get; init; }

    /// <summary>Null means "price on request": shown, but not sellable online.</summary>
    public int? UnitPriceCents { get; private set; }

    public int OnHand { get; private set; }

    public bool Discontinued { get; private set; }

    /// <summary>Image views such as "front" and "side"; the first is the primary image.</summary>
    public List<string> ImageViews { get; init; } = [];

    public List<ItemProp> Props { get; init; } = [];

    /// <summary>UTC, whole seconds (the precision the contract shows), so updated_since compares exactly what clients see.</summary>
    public DateTime UpdatedAt { get; private set; }

    public static CatalogItem Create(
        string itemCode, string dept, string title, string summary, int? unitPriceCents, int onHand,
        bool discontinued, DateTime updatedAt, List<string> imageViews, List<ItemProp> props) =>
        new()
        {
            ItemCode = itemCode,
            Dept = dept,
            Title = title,
            Summary = summary,
            UnitPriceCents = unitPriceCents,
            OnHand = onHand,
            Discontinued = discontinued,
            UpdatedAt = updatedAt,
            ImageViews = imageViews,
            Props = props,
        };

    public void SetOnHand(int onHand, DateTimeOffset now)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(onHand);
        if (onHand != OnHand)
        {
            OnHand = onHand;
            Touch(now);
        }
    }

    public void SetPrice(int? unitPriceCents, DateTimeOffset now)
    {
        if (unitPriceCents < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(unitPriceCents), "A price cannot be negative.");
        }

        if (unitPriceCents != UnitPriceCents)
        {
            UnitPriceCents = unitPriceCents;
            Touch(now);
        }
    }

    public void SetDiscontinued(bool discontinued, DateTimeOffset now)
    {
        if (discontinued != Discontinued)
        {
            Discontinued = discontinued;
            Touch(now);
        }
    }

    private void Touch(DateTimeOffset now) =>
        UpdatedAt = new DateTime(now.UtcTicks - (now.UtcTicks % TimeSpan.TicksPerSecond), DateTimeKind.Utc);
}

internal sealed class ItemProp
{
    public required string Name { get; init; }

    public required string Value { get; init; }
}
