namespace Lumen.EndToEndTests;

/// <summary>
/// The seeded demo accounts, each signed in once and shared by the tests that use it. Every sign-in from the test
/// runner reaches the stack from one address, and the API allows 5 sign-ins per minute per address (RateLimiting in
/// src/Lumen.Api/appsettings.json): one per account leaves room for re-runs and for other callers such as the CI smoke
/// step. Signing in happens on first use, inside a test, so without <see cref="StackClient.UrlVariable"/> that test is
/// skipped rather than the fixture failing.
/// </summary>
public sealed class SignedInAccounts : IDisposable
{
    // The demo accounts the stack seeds (Seed section of src/Lumen.Api/appsettings.json).
    private const string CustomerEmail = "customer@lumengoods.test";
    private const string CustomerPassword = "Customer!2026";
    private const string AdminEmail = "admin@lumengoods.test";
    private const string AdminPassword = "Admin!2026";

    private readonly Lazy<Task<StackClient>> _customer = new(() => SignInAsync(CustomerEmail, CustomerPassword));
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

    private static async Task<StackClient> SignInAsync(string email, string password)
    {
        var client = await StackClient.StartAsync();
        await client.SignInAsync(email, password);
        return client;
    }
}
