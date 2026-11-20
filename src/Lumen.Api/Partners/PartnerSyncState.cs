using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Lumen.Api.Partners;

/// <summary>Per-partner catalogue sync bookkeeping, shown on the admin Partner status page.</summary>
public sealed class PartnerSyncState
{
    /// <summary>Partner code, equal to its product source (e.g. "harbor").</summary>
    public required string Partner { get; set; }

    public DateTimeOffset? LastAttemptUtc { get; set; }

    /// <summary>When the catalogue was last refreshed successfully ("when its catalogue was last refreshed").</summary>
    public DateTimeOffset? LastSuccessUtc { get; set; }

    public string? LastError { get; set; }

    /// <summary>Products currently listed in the shop from this partner ("how many products are live").</summary>
    public int LiveProductCount { get; set; }

    /// <summary>Partner-side incremental watermark (Harbor: newest updated_at seen; Kestrel: feed Last-Modified).</summary>
    public string? Watermark { get; set; }

    public DateTimeOffset? LastFullSyncUtc { get; set; }

    public DateTimeOffset? StockRefreshedUtc { get; set; }

    /// <summary>Set by the admin "Refresh now" button; the sync job runs as soon as it sees it.</summary>
    public DateTimeOffset? RefreshRequestedUtc { get; set; }

    /// <summary>
    /// BR-SYNC-02: products missing from the partner's last full catalogue that the mass-deletion guard kept listed.
    /// Zero when nothing is held back.
    /// </summary>
    public int HeldRemovals { get; set; }

    /// <summary>
    /// Set by "Refresh now" with confirmRemovals: the next full pass also applies the removals the guard would hold back.
    /// Cleared by that run.
    /// </summary>
    public DateTimeOffset? RemovalsConfirmedUtc { get; set; }
}

/// <summary>Recent partner errors ("recent errors" on the admin page). Old rows are trimmed by the sync jobs.</summary>
public sealed class PartnerSyncError
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public required string Partner { get; set; }

    public DateTimeOffset OccurredUtc { get; set; }

    /// <summary>What was being done, e.g. "catalogue", "stock", "image", "order".</summary>
    public required string Operation { get; set; }

    public int? StatusCode { get; set; }

    public required string Message { get; set; }
}

internal sealed class PartnerSyncStateConfiguration : IEntityTypeConfiguration<PartnerSyncState>
{
    public void Configure(EntityTypeBuilder<PartnerSyncState> builder)
    {
        builder.HasKey(s => s.Partner);
        builder.Property(s => s.Partner).HasMaxLength(32);
        builder.Property(s => s.LastError).HasMaxLength(2000);
        builder.Property(s => s.Watermark).HasMaxLength(200);
        builder.Property(s => s.HeldRemovals).HasDefaultValue(0);
    }
}

internal sealed class PartnerSyncErrorConfiguration : IEntityTypeConfiguration<PartnerSyncError>
{
    public void Configure(EntityTypeBuilder<PartnerSyncError> builder)
    {
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).ValueGeneratedNever();
        builder.Property(e => e.Partner).HasMaxLength(32);
        builder.Property(e => e.Operation).HasMaxLength(32);
        builder.Property(e => e.Message).HasMaxLength(2000);
        builder.HasIndex(e => new { e.Partner, e.OccurredUtc });
    }
}
