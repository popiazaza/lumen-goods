using Lumen.Api.Identity;
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
    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.ApplyConfigurationsFromAssembly(typeof(LumenDbContext).Assembly);
    }
}
