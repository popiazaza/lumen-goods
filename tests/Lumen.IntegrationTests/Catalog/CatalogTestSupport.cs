using System.Net.Http.Json;
using System.Text.Json;
using Lumen.Api.Catalog;
using Lumen.Api.Identity;
using Lumen.Api.Infrastructure.Data;
using Lumen.Api.Infrastructure.Json;
using Lumen.Domain.Catalog;
using Lumen.IntegrationTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Lumen.IntegrationTests.Catalog;

/// <summary>
/// Helpers for catalogue tests. The test database is shared by the whole assembly (and holds the seed), so every test
/// tags the products it inserts with a unique marker in the name and narrows its queries with <c>q=marker</c>.
/// </summary>
internal static class CatalogTestSupport
{
    public static readonly JsonSerializerOptions Json = JsonDefaults.Create();

    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static readonly DateTimeOffset BaseTime = new(2026, 11, 1, 6, 0, 0, TimeSpan.Zero);

    /// <summary>A unique word to put in product names (letters and digits only, so it is not a LIKE wildcard).</summary>
    public static string NewMarker() => $"mk{Guid.NewGuid():N}";

    public static Product Own(
        string name,
        decimal? price = 10.00m,
        int onHand = 10,
        int reserved = 0,
        string category = ProductCategories.Apparel,
        DateTimeOffset? firstSeen = null,
        bool listed = true,
        string description = "") => new()
        {
            Source = ProductSources.Lumen,
            SourceProductId = $"T-{Guid.NewGuid():N}"[..20].ToUpperInvariant(),
            Name = name,
            Description = description,
            Category = category,
            PriceUsd = price,
            OnHand = onHand,
            Reserved = reserved,
            IsActive = listed,
            IsListed = listed,
            Images = [new ProductImage("own-media/lg-ap-001.png", true)],
            Attributes = new() { ["color"] = "Oat" },
            FirstSeenUtc = firstSeen ?? BaseTime,
            LastUpdatedUtc = firstSeen ?? BaseTime,
        };

    public static Product Partner(
        string source,
        string name,
        decimal? price = 10.00m,
        int? quantity = 20,
        AvailabilityStatus whenUnknown = AvailabilityStatus.InStock,
        string category = ProductCategories.Apparel,
        DateTimeOffset? firstSeen = null,
        bool active = true,
        bool listed = true) => new()
        {
            Source = source,
            SourceProductId = $"T-{Guid.NewGuid():N}"[..20].ToUpperInvariant(),
            Name = name,
            Category = category,
            PriceUsd = price,
            Quantity = quantity,
            StatusWhenQuantityUnknown = whenUnknown,
            IsActive = active,
            IsListed = listed,
            Images = [new ProductImage($"http://{source}:8080/partner-only/image-{Guid.NewGuid():N}.png", true)],
            FirstSeenUtc = firstSeen ?? BaseTime,
            LastUpdatedUtc = firstSeen ?? BaseTime,
        };

    public static async Task InsertAsync(this LumenApiFactory factory, params Product[] products)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<LumenDbContext>();
        db.Products.AddRange(products);
        await db.SaveChangesAsync(Ct);
    }

    /// <summary>Runs <paramref name="action"/> against the database directly (e.g. to simulate a stock reservation).</summary>
    public static async Task WithDbAsync(this LumenApiFactory factory, Func<LumenDbContext, Task> action)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        await action(scope.ServiceProvider.GetRequiredService<LumenDbContext>());
    }

    public static async Task<ApiSession> SignedInAsync(this LumenApiFactory factory, string role)
    {
        var (email, password) = role == Roles.Admin
            ? ("admin@lumengoods.test", "Admin!2026")
            : ("customer@lumengoods.test", "Customer!2026");
        var session = factory.CreateSession();
        await session.StartAsync();
        (await session.PostAsJsonAsync("/api/v1/auth/sign-in", new SignInRequest(email, password))).EnsureSuccessStatusCode();
        return session;
    }

    /// <summary>PUT with the XSRF header, as the Angular client sends it.</summary>
    public static async Task<HttpResponseMessage> PutAsJsonAsync<T>(this ApiSession session, string url, T body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, url) { Content = JsonContent.Create(body) };
        if (session.XsrfToken is { } token)
        {
            request.Headers.Add("X-XSRF-TOKEN", token);
        }

        return await session.Client.SendAsync(request, Ct);
    }

    public static async Task<T> ReadAsync<T>(this HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<T>(Json, Ct))!;

    public static async Task<JsonElement> ReadJsonAsync(this HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement.Clone();
}
