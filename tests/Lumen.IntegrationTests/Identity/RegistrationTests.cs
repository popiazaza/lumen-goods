using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using Lumen.Api.Identity;
using Lumen.Api.Infrastructure.Data;
using Lumen.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace Lumen.IntegrationTests.Identity;

public sealed class RegistrationTests(LumenApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_registration_that_fails_to_add_the_customer_role_leaves_no_account_behind()
    {
        var failRole = new FailNextRoleInsert();
        await using var app = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.ConfigureDbContext<LumenDbContext>(options => options.AddInterceptors(failRole))));
        var session = new ApiSession(app.CreateClient());
        await session.StartAsync();
        var email = $"half-{Guid.NewGuid():N}@lumengoods.test";

        failRole.Armed = true;
        var failed = await session.PostAsJsonAsync("/api/v1/auth/register", new RegisterRequest(email, "Shopper!2026"));
        var retried = await session.PostAsJsonAsync("/api/v1/auth/register", new RegisterRequest(email, "Shopper!2026"));

        failed.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        retried.StatusCode.Should().Be(HttpStatusCode.OK, "the failed registration was rolled back, so the email is still free");
        (await retried.Content.ReadFromJsonAsync<SessionResponse>(Ct))!.Roles.Should().Equal(Roles.Customer);
    }

    [Fact]
    public async Task A_refused_password_lists_each_missing_rule_in_plain_words_under_Password()
    {
        var session = factory.CreateSession();
        await session.StartAsync();

        var response = await session.PostAsJsonAsync(
            "/api/v1/auth/register", new RegisterRequest($"weak-{Guid.NewGuid():N}@example.test", "abcdefgh"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var problem = await response.Content.ReadFromJsonAsync<HttpValidationProblemDetails>(Ct);
        problem!.Errors.Should().ContainKey(nameof(RegisterRequest.Password)).WhoseValue.Should().BeEquivalentTo(
            "Add a symbol, such as ! or #.", "Add a number (0-9).", "Add an upper-case letter (A-Z).");
    }

    [Fact]
    public async Task A_short_password_is_refused_with_the_minimum_length()
    {
        var session = factory.CreateSession();
        await session.StartAsync();

        var response = await session.PostAsJsonAsync(
            "/api/v1/auth/register", new RegisterRequest($"short-{Guid.NewGuid():N}@example.test", "Ab1!"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var problem = await response.Content.ReadFromJsonAsync<HttpValidationProblemDetails>(Ct);
        problem!.Errors[nameof(RegisterRequest.Password)].Should().Equal("Use at least 8 characters.");
    }

    [Fact]
    public async Task A_taken_email_says_so_once_and_suggests_signing_in()
    {
        var email = $"taken-{Guid.NewGuid():N}@example.test";
        var first = factory.CreateSession();
        await first.StartAsync();
        (await first.PostAsJsonAsync("/api/v1/auth/register", new RegisterRequest(email, "Shopper!2026")))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        var session = factory.CreateSession();
        await session.StartAsync();

        var response = await session.PostAsJsonAsync("/api/v1/auth/register", new RegisterRequest(email, "Shopper!2026"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var problem = await response.Content.ReadFromJsonAsync<HttpValidationProblemDetails>(Ct);
        problem!.Errors[nameof(RegisterRequest.Email)].Should().Equal(PlainIdentityErrorDescriber.EmailTaken);
    }

    /// <summary>Fails the next save that adds a user to a role, as a dropped connection would.</summary>
    private sealed class FailNextRoleInsert : DbCommandInterceptor
    {
        public bool Armed { get; set; }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            FailIfArmed(command);
            return ValueTask.FromResult(result);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            FailIfArmed(command);
            return ValueTask.FromResult(result);
        }

        private void FailIfArmed(DbCommand command)
        {
            if (Armed && command.CommandText.Contains("INSERT INTO \"AspNetUserRoles\"", StringComparison.Ordinal))
            {
                Armed = false;
                throw new InvalidOperationException("Connection lost while adding the role (test).");
            }
        }
    }
}
