namespace Lumen.EndToEndTests;

/// <summary>
/// The accounts the tests use, each signed in once per run and shared by the tests that use it.
/// The customer is registered for this run under a unique email (a new customer's wallet starts at $500.00, BR-PAY-06),
/// so the tests never empty the cart or spend the wallet of the seeded demo customer a reviewer may be using. The admin
/// is the seeded admin; the tests only read with it.
/// Every call from the test runner reaches the stack from one address, and the API allows 5 registrations per 10 minutes
/// and 5 sign-ins per minute per address (RateLimiting in src/Lumen.Api/appsettings.json): one of each per run leaves room
/// for re-runs and for other callers such as the CI smoke step. Both happen on first use, inside a test, so without
/// <see cref="StackClient.UrlVariable"/> that test is skipped rather than the fixture failing.
/// </summary>
public sealed class SignedInAccounts : IDisposable
{
    // The demo admin the stack seeds (Seed section of src/Lumen.Api/appsettings.json).
    private const string AdminEmail = "admin@lumengoods.test";
    private const string AdminPassword = "Admin!2026";

    private readonly Lazy<Task<StackClient>> _customer = new(RegisterCustomerAsync);
    private readonly Lazy<Task<StackClient>> _admin = new(() => SignInAsync(AdminEmail, AdminPassword));

    public Task<StackClient> CustomerAsync() => _customer.Value;

    public Task<StackClient> AdminAsync() => _admin.Value;

    public void Dispose()
    {
        foreach (var client in new[] { _customer, _admin })
        {
            if (client.IsValueCreated && client.Value.IsCompletedSuccessfully)
            {
                client.Value.Result.Dispose();
            }
        }
    }

    /// <summary>Registration signs the new customer in, so no separate sign-in is spent.</summary>
    private static async Task<StackClient> RegisterCustomerAsync()
    {
        var client = await StackClient.StartAsync();
        var id = Guid.NewGuid().ToString("N");

        // Upper and lower case, a digit and a symbol: Identity's default password rules.
        await client.RegisterAsync($"e2e-{id}@lumengoods.test", $"E2e-{id}");
        return client;
    }

    private static async Task<StackClient> SignInAsync(string email, string password)
    {
        var client = await StackClient.StartAsync();
        await client.SignInAsync(email, password);
        return client;
    }
}
