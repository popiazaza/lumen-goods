using Lumen.Api.Carts;
using Lumen.Api.Catalog;
using Lumen.Api.Identity;
using Lumen.Api.Orders;
using Lumen.Api.Partners;
using Lumen.Api.Payments;
using Lumen.Api.Wallets;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Lumen.Api.Infrastructure.Data;

/// <summary>
/// The single Lumen Goods database context. Feature folders contribute their entity configurations
/// through <see cref="IEntityTypeConfiguration{TEntity}"/> classes in this assembly.
/// </summary>
public sealed class LumenDbContext(DbContextOptions<LumenDbContext> options)
    : IdentityDbContext<AppUser, IdentityRole<Guid>, Guid>(options)
{
    public DbSet<Product> Products => Set<Product>();

    public DbSet<PartnerSyncState> PartnerSyncStates => Set<PartnerSyncState>();

    public DbSet<PartnerSyncError> PartnerSyncErrors => Set<PartnerSyncError>();

    public DbSet<Cart> Carts => Set<Cart>();

    public DbSet<CartLine> CartLines => Set<CartLine>();

    public DbSet<Order> Orders => Set<Order>();

    public DbSet<OrderLine> OrderLines => Set<OrderLine>();

    public DbSet<OrderShipment> OrderShipments => Set<OrderShipment>();

    public DbSet<PartnerOrder> PartnerOrders => Set<PartnerOrder>();

    public DbSet<Payment> Payments => Set<Payment>();

    public DbSet<PaymentEvent> PaymentEvents => Set<PaymentEvent>();

    public DbSet<Refund> Refunds => Set<Refund>();

    public DbSet<RefundLine> RefundLines => Set<RefundLine>();

    public DbSet<Wallet> Wallets => Set<Wallet>();

    public DbSet<WalletTransaction> WalletTransactions => Set<WalletTransaction>();

    public DbSet<MockGatewayCharge> MockGatewayCharges => Set<MockGatewayCharge>();

    public DbSet<MockGatewayRefund> MockGatewayRefunds => Set<MockGatewayRefund>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        // Table and column names follow EF Core's PascalCase defaults everywhere (like the Identity tables), so
        // hand-written SQL always quotes them the same way.
        builder.HasPostgresExtension("pg_trgm");
        builder.ApplyConfigurationsFromAssembly(typeof(LumenDbContext).Assembly);
        CatalogDbFunctions.Register(builder);
    }
}
