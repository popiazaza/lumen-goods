using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Validation;

namespace Lumen.IntegrationTests.Infrastructure;

/// <summary>
/// Request validation (AddValidation) covers what the client sends, never the services a handler is given. Since
/// ASP.NET Core 11 the validation generator also walks [AsParameters] records, so a record that bundles services
/// (a DbContext among them) would be validated member by member: every DbSet enumerated on every request, and a 500
/// once an order's navigations form a cycle. Such records carry [SkipValidation].
/// </summary>
public sealed class RequestValidationScopeTests(LumenApiFactory factory)
{
    [Fact]
    public void No_AsParameters_record_that_holds_a_service_is_validated_as_request_input()
    {
        var isService = factory.Services.GetRequiredService<IServiceProviderIsService>();
        var validation = factory.Services.GetRequiredService<IOptions<ValidationOptions>>().Value;

        var bundles = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .Select(e => e.Metadata.GetMetadata<MethodInfo>())
            .OfType<MethodInfo>()
            .SelectMany(m => m.GetParameters())
            .Where(p => p.GetCustomAttribute<AsParametersAttribute>() is not null)
            .Select(p => p.ParameterType)
            .Distinct()
            .Where(t => t.GetProperties().Any(p => isService.IsService(p.PropertyType)))
            .ToList();

        bundles.Should().NotBeEmpty("the admin product endpoints and sign-in bind their services with [AsParameters]");
        bundles.Should().AllSatisfy(t => validation.TryGetValidatableTypeInfo(t, out _).Should()
            .BeFalse($"{t.Name} holds services, not request input"));
    }
}
