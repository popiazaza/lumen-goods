using System.Net;
using Lumen.Api.Identity;
using Lumen.Api.Infrastructure.RateLimiting;
using Lumen.IntegrationTests.Catalog;
using Lumen.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace Lumen.IntegrationTests.Identity;

/// <summary>
/// Inbound rate limits on sign-in, on the real API. The test server has no client address, so a test names its client
/// in X-Forwarded-For as nginx does, each with its own documentation address so no two tests share a limit.
/// </summary>
public sealed class RateLimitTests(LumenApiFactory factory)
{
    private static int _lastAddress;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_sixth_rapid_sign_in_from_one_client_gets_429_with_retry_after_and_other_clients_are_not_affected()
    {
        var client = await SessionFromAsync(NewClientAddress());

        var attempts = new List<HttpResponseMessage>();
        for (var i = 0; i < 6; i++)
        {
            attempts.Add(await client.PostAsJsonAsync("/api/v1/auth/sign-in", new SignInRequest(UnknownEmail(), "nope-nope")));
        }

        var otherClient = await SessionFromAsync(NewClientAddress());
        var fromElsewhere = await otherClient.PostAsJsonAsync("/api/v1/auth/sign-in", new SignInRequest(UnknownEmail(), "nope-nope"));

        attempts.Take(5).Select(r => r.StatusCode).Should().AllBeEquivalentTo(HttpStatusCode.Unauthorized);
        var limited = attempts[5];
        limited.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        limited.Headers.RetryAfter!.Delta.Should().BeGreaterThan(TimeSpan.Zero).And.BeLessThanOrEqualTo(TimeSpan.FromMinutes(1));
        limited.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        var problem = await limited.ReadJsonAsync();
        (problem.GetProperty("type").GetString(), problem.GetProperty("status").GetInt32())
            .Should().Be((RateLimitProblemTypes.RateLimited, 429));
        fromElsewhere.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "the limit is per client address");
    }

    [Fact]
    public async Task A_burst_of_concurrent_wrong_passwords_for_one_email_counts_every_attempt_and_ends_in_lockout()
    {
        var email = $"burst-{Guid.NewGuid():N}@lumengoods.test";
        var owner = factory.CreateSession();
        await owner.StartAsync();
        (await owner.PostAsJsonAsync("/api/v1/auth/register", new RegisterRequest(email, "Correct!2026"))).EnsureSuccessStatusCode();

        // Five guesses at the same moment, each from its own address (as from a botnet), so no per-client limit applies.
        var guessers = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => SessionFromAsync(NewClientAddress())));
        var guesses = await Task.WhenAll(guessers.Select(g => g.PostAsJsonAsync("/api/v1/auth/sign-in", new SignInRequest(email, "Wrong!2026"))));
        var rightPassword = await owner.PostAsJsonAsync("/api/v1/auth/sign-in", new SignInRequest(email, "Correct!2026"));

        // Identity's lockout (5 failures) counted all five: four plain failures, and the fifth locks the account.
        guesses.Count(r => r.StatusCode == HttpStatusCode.Unauthorized).Should().Be(4);
        var locking = guesses.Single(r => r.StatusCode == HttpStatusCode.TooManyRequests);
        (await locking.ReadJsonAsync()).GetProperty("title").GetString().Should().Be("Too many failed attempts");
        rightPassword.StatusCode.Should().Be(HttpStatusCode.TooManyRequests, "the account is locked, even for the right password");
        await using var scope = factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        (await users.IsLockedOutAsync((await users.FindByEmailAsync(email))!)).Should().BeTrue();
    }

    private async Task<ApiSession> SessionFromAsync(string clientAddress)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Forwarded-For", clientAddress);
        var session = new ApiSession(client);
        await session.StartAsync();
        return session;
    }

    /// <summary>A fresh address from the benchmarking range 198.18.0.0/15 (RFC 2544) for each client in this run.</summary>
    private static string NewClientAddress()
    {
        var n = Interlocked.Increment(ref _lastAddress);
        return $"198.18.{n / 250}.{n % 250 + 1}";
    }

    private static string UnknownEmail() => $"nobody-{Guid.NewGuid():N}@lumengoods.test";
}
