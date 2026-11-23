using System.Data.Common;
using Lumen.Api.Carts;
using Lumen.Api.Identity;
using Lumen.Api.Infrastructure.Data;
using Lumen.Domain.Catalog;
using Lumen.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Lumen.IntegrationTests.Carts;

/// <summary>
/// Fault injection for the cart services: the real <see cref="CartService"/> and <see cref="CartMergeService"/> on the
/// real database, through a <see cref="LumenDbContext"/> with EF Core interceptors that make a save race another request
/// or fail at commit.
/// </summary>
internal static class CartFaults
{
    /// <summary>A service provider whose <see cref="LumenDbContext"/> is the API's database with these interceptors.</summary>
    public static ServiceProvider DatabaseWith(this LumenApiFactory factory, params IInterceptor[] interceptors)
    {
        var connectionString = factory.Services.GetRequiredService<IConfiguration>().GetConnectionString("Lumen")!;
        var services = new ServiceCollection();
        services.AddDbContext<LumenDbContext>(options => options.UseNpgsql(connectionString).AddInterceptors(interceptors));
        return services.BuildServiceProvider();
    }

    public static CartMergeService MergeServiceOn(this LumenApiFactory factory, ServiceProvider database) => new(
        database.GetRequiredService<IServiceScopeFactory>(),
        factory.Services.GetRequiredService<GuestCartCookie>(),
        factory.Services.GetRequiredService<AvailabilityPolicy>(),
        TimeProvider.System,
        factory.Services.GetRequiredService<IOptions<CartOptions>>(),
        NullLogger<CartMergeService>.Instance);

    public static CartService CartServiceOn(this LumenApiFactory factory, LumenDbContext db) => new(
        db,
        factory.Services.GetRequiredService<GuestCartCookie>(),
        factory.Services.GetRequiredService<AvailabilityPolicy>(),
        TimeProvider.System,
        factory.Services.GetRequiredService<IOptions<CartOptions>>(),
        NullLogger<CartService>.Instance);

    /// <summary>A guest's request carrying the cart cookie, as sign-in or a cart call receives it.</summary>
    public static DefaultHttpContext GuestRequest(string guestToken, IServiceProvider services)
    {
        var http = new DefaultHttpContext { RequestServices = services };
        http.Request.Headers.Cookie = $"{GuestCartToken.CookieName}={guestToken}";
        return http;
    }

    /// <summary>The Set-Cookie headers the request's response would carry for the guest cart cookie.</summary>
    public static IEnumerable<string> GuestCookieHeaders(this HttpContext http) =>
        http.Response.Headers.SetCookie.Where(c => c is not null && c.StartsWith($"{GuestCartToken.CookieName}=", StringComparison.Ordinal))!;

    /// <summary>A customer account (customer role, no password): the merge only serves customers.</summary>
    public static async Task<Guid> NewShopperAsync(this LumenApiFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var email = $"shopper-{Guid.NewGuid():N}@example.test";
        var user = new AppUser { UserName = email, Email = email };
        (await users.CreateAsync(user)).Succeeded.Should().BeTrue();
        (await users.AddToRoleAsync(user, Roles.Customer)).Succeeded.Should().BeTrue();
        return user.Id;
    }
}

/// <summary>Runs <c>beforeSave(n)</c> before the n-th SaveChanges, e.g. another request committing in between.</summary>
internal sealed class BeforeSave(Func<int, Task> beforeSave) : SaveChangesInterceptor
{
    public int Saves { get; private set; }

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Saves++;
        await beforeSave(Saves);
        return result;
    }
}

/// <summary>Fails every commit, after all of the save's statements ran: the database must roll all of them back.</summary>
internal sealed class FailingCommit : DbTransactionInterceptor
{
    public const string Message = "Simulated failure while committing";

    public int Commits { get; private set; }

    public override ValueTask<InterceptionResult> TransactionCommittingAsync(
        DbTransaction transaction, TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
    {
        Commits++;
        throw new InvalidOperationException(Message);
    }
}
