using System.Net;
using System.Net.Http.Json;
using Lumen.Api.Identity;
using Lumen.IntegrationTests.Infrastructure;

namespace Lumen.IntegrationTests.Identity;

public sealed class AuthTests(LumenApiFactory factory)
{
    [Fact]
    public async Task Health_endpoint_reports_healthy_once_the_database_is_migrated()
    {
        var response = await factory.CreateClient().GetAsync("/health", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Should().Be("Healthy");
    }

    [Fact]
    public async Task Anonymous_session_issues_a_readable_xsrf_cookie()
    {
        var session = factory.CreateSession();

        var response = await session.GetAsync("/api/v1/auth/session");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<SessionResponse>(TestContext.Current.CancellationToken))
            .Should().BeEquivalentTo(SessionResponse.Anonymous);
        response.Headers.GetValues("Set-Cookie").Should().Contain(c => c.StartsWith("XSRF-TOKEN=") && !c.Contains("httponly"));
    }

    [Fact]
    public async Task State_changing_call_without_the_xsrf_header_is_rejected()
    {
        var session = factory.CreateSession();
        await session.StartAsync();

        var response = await session.PostAsJsonAsync("/api/v1/auth/sign-in",
            new SignInRequest("customer@lumengoods.test", "Customer!2026"), withXsrfHeader: false);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .Should().Contain("Missing or invalid XSRF token");
    }

    [Fact]
    public async Task Seeded_admin_can_sign_in_and_has_the_admin_role()
    {
        var session = factory.CreateSession();
        await session.StartAsync();

        var response = await session.PostAsJsonAsync("/api/v1/auth/sign-in", new SignInRequest("admin@lumengoods.test", "Admin!2026"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<SessionResponse>(TestContext.Current.CancellationToken);
        body!.Roles.Should().Equal(Roles.Admin);
    }

    [Fact]
    public async Task Wrong_password_gets_the_same_401_as_an_unknown_email()
    {
        var session = factory.CreateSession();
        await session.StartAsync();

        var wrongPassword = await session.PostAsJsonAsync("/api/v1/auth/sign-in", new SignInRequest("customer@lumengoods.test", "nope-nope"));
        var unknownEmail = await session.PostAsJsonAsync("/api/v1/auth/sign-in", new SignInRequest("nobody@lumengoods.test", "nope-nope"));

        wrongPassword.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        unknownEmail.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Registering_signs_the_new_customer_in_and_the_session_survives_the_next_request()
    {
        var session = factory.CreateSession();
        await session.StartAsync();
        var email = $"new-{Guid.NewGuid():N}@lumengoods.test";

        var registered = await session.PostAsJsonAsync("/api/v1/auth/register", new RegisterRequest(email, "Shopper!2026"));
        var current = await session.GetAsync("/api/v1/auth/session");

        registered.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await current.Content.ReadFromJsonAsync<SessionResponse>(TestContext.Current.CancellationToken);
        body.Should().BeEquivalentTo(new SessionResponse(true, email, [Roles.Customer]));
    }

    [Fact]
    public async Task Signing_out_after_signing_in_works_with_the_reissued_xsrf_token()
    {
        var session = factory.CreateSession();
        await session.StartAsync();
        var signedIn = await session.PostAsJsonAsync("/api/v1/auth/sign-in", new SignInRequest("customer@lumengoods.test", "Customer!2026"));

        var signedOut = await session.PostAsJsonAsync("/api/v1/auth/sign-out", new { });

        signedIn.StatusCode.Should().Be(HttpStatusCode.OK);
        signedOut.StatusCode.Should().Be(HttpStatusCode.OK);
        (await signedOut.Content.ReadFromJsonAsync<SessionResponse>(TestContext.Current.CancellationToken))
            .Should().BeEquivalentTo(SessionResponse.Anonymous);
    }

    [Fact]
    public async Task Xsrf_token_issued_before_sign_in_is_rejected_after_sign_in()
    {
        var session = factory.CreateSession();
        await session.StartAsync();
        var anonymousToken = session.XsrfToken;
        var signedIn = await session.PostAsJsonAsync("/api/v1/auth/sign-in", new SignInRequest("customer@lumengoods.test", "Customer!2026"));

        var withStaleToken = await session.PostAsJsonAsync("/api/v1/auth/sign-out", new { }, xsrfTokenOverride: anonymousToken);

        signedIn.StatusCode.Should().Be(HttpStatusCode.OK);
        session.XsrfToken.Should().NotBe(anonymousToken);
        withStaleToken.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
