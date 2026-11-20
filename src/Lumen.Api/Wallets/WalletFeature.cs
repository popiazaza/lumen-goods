using System.ComponentModel.DataAnnotations;

namespace Lumen.Api.Wallets;

/// <summary>Wallet settings (BR-PAY-06, A-38). Bound from the <c>Wallet</c> configuration section.</summary>
public sealed class WalletOptions
{
    public const string SectionName = "Wallet";

    /// <summary>Opening balance of the seeded demo customer: enough for the catalogue's items above 1.000 EUR.</summary>
    [Range(typeof(decimal), "0", "1000000")]
    public decimal SeededCustomerBalance { get; init; } = 2000.00m;

    /// <summary>Opening balance of every newly registered customer (mock money; there is no top-up).</summary>
    [Range(typeof(decimal), "0", "1000000")]
    public decimal NewCustomerBalance { get; init; } = 500.00m;

    /// <summary>How many transactions <c>GET /wallet</c> lists.</summary>
    [Range(1, 200)]
    public int RecentTransactionsShown { get; init; } = 20;
}

/// <summary>
/// The wallet (BR-PAY-06): balance, transactions, opening balances. Wallet payments and refunds are written by the
/// payments and refunds work in their own transactions. Wired once in Program.cs.
/// </summary>
internal static class WalletFeature
{
    public static IServiceCollection AddWallet(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<WalletOptions>()
            .Bind(configuration.GetSection(WalletOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddScoped<WalletService>();
        return services;
    }

    /// <summary>
    /// Wallet endpoints under /api/v1 (signed-in customer):
    /// <list type="bullet">
    /// <item><c>GET /wallet</c> → <see cref="WalletDto"/>; calls <see cref="WalletService.EnsureOpenAsync"/> first, so a
    /// customer whose wallet was not opened at registration gets it now (paying by wallet does the same)</item>
    /// </list>
    /// </summary>
    public static RouteGroupBuilder MapWalletEndpoints(this RouteGroupBuilder api) => api;
}
