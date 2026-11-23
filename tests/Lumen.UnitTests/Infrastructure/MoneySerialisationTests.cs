using System.Collections;
using System.Reflection;
using System.Text.Json.Serialization;
using Lumen.Api.Infrastructure.Json;

namespace Lumen.UnitTests.Infrastructure;

/// <summary>
/// BR-MONEY-01: every amount the API sends has exactly two decimals. The scale comes from a per-property
/// <see cref="TwoDecimalJsonConverter"/>, so a new money field on a response without it would serialise whatever scale
/// the arithmetic left (49.9, 16.2200). This finds every decimal the API can answer with, by reflection, so the
/// attribute cannot be forgotten silently.
/// </summary>
public sealed class MoneySerialisationTests
{
    private static readonly Assembly Api = typeof(TwoDecimalJsonConverter).Assembly;

    /// <summary>
    /// Response shapes: every Lumen API type named *Dto, *Problem or *Response, plus everything they contain. Partner
    /// adapters' own wire types (Lumen.Api.Partners.Harbor/Kestrel) are inbound and never sent to a client.
    /// </summary>
    private static IReadOnlyCollection<Type> ResponseTypes()
    {
        var roots = Api.GetTypes()
            .Where(t => t.Namespace is { } ns && !ns.StartsWith("Lumen.Api.Partners.Harbor", StringComparison.Ordinal)
                        && !ns.StartsWith("Lumen.Api.Partners.Kestrel", StringComparison.Ordinal))
            .Where(t => !t.IsInterface)
            .Where(t => t.Name.EndsWith("Dto", StringComparison.Ordinal) || t.Name.EndsWith("Problem", StringComparison.Ordinal)
                        || t.Name.EndsWith("Response", StringComparison.Ordinal));

        var seen = new HashSet<Type>();
        var queue = new Queue<Type>(roots);
        while (queue.TryDequeue(out var type))
        {
            if (type.Assembly != Api || !seen.Add(type))
            {
                continue;
            }

            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                foreach (var inner in Unwrap(property.PropertyType))
                {
                    queue.Enqueue(inner);
                }
            }
        }

        return seen;
    }

    /// <summary>The type itself, or its element/generic argument types for collections, nullables and generics.</summary>
    private static IEnumerable<Type> Unwrap(Type type)
    {
        if (type.IsArray)
        {
            return Unwrap(type.GetElementType()!);
        }

        if (type.IsGenericType)
        {
            var arguments = type.GetGenericArguments().SelectMany(Unwrap);
            return type.Assembly == Api ? arguments.Prepend(type.GetGenericTypeDefinition()) : arguments;
        }

        return [type];
    }

    private static bool IsMoney(Type type) => type == typeof(decimal) || type == typeof(decimal?);

    private static bool IsMoneyCollection(Type type) =>
        type != typeof(string) && typeof(IEnumerable).IsAssignableFrom(type) && Unwrap(type).Any(IsMoney);

    [Fact]
    [Trait("BR", "BR-MONEY-01")]
    public void BR_MONEY_01_every_decimal_on_a_response_type_is_written_with_two_decimals()
    {
        var types = ResponseTypes();
        var decimals = types
            .SelectMany(t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Where(p => IsMoney(p.PropertyType) || IsMoneyCollection(p.PropertyType))
            .ToList();

        // The walk must reach the money-bearing responses, or it proves nothing.
        types.Select(t => t.Name).Should().Contain(["ProductDto", "PriceDto", "CartDto", "OrderDetailDto", "WalletDto", "CheckoutPreviewDto"]);
        decimals.Should().HaveCountGreaterThan(30);

        var missing = decimals
            .Where(p => p.GetCustomAttribute<JsonIgnoreAttribute>() is null)
            .Where(p => p.GetCustomAttribute<JsonConverterAttribute>()?.ConverterType != typeof(TwoDecimalJsonConverter))
            .Select(p => $"{p.DeclaringType!.Name}.{p.Name}");
        missing.Should().BeEmpty("every amount is sent with exactly two decimals (TwoDecimalJsonConverter on the property)");
    }
}
