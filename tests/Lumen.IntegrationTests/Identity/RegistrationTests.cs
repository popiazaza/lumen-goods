using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using Lumen.Api.Identity;
using Lumen.Api.Infrastructure.Data;
using Lumen.IntegrationTests.Infrastructure;
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
