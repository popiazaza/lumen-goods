using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Lumen.Api.Infrastructure.OpenApi;

/// <summary>
/// Tidies the text that the OpenAPI XML-comment source generator copies from our /// comments, so the reference at
/// /api/docs reads as documentation rather than C#. The generator writes a member <c>&lt;see cref&gt;</c> as its full C#
/// signature ("IReadOnlyList&amp;lt;PlaceOrderLineDto&amp;gt;? PlaceOrderRequest.Lines"), indents text around
/// <c>&lt;para&gt;</c> and <c>&lt;list&gt;</c> by four spaces (a Markdown code block), numbers every list item "1.",
/// and turns an <c>&lt;example&gt;&lt;code&gt;</c> into a fenced string (or null when it is not strict JSON). The comments
/// keep their crefs, which the IDE and the compiler check; only the published text changes.
/// </summary>
internal sealed partial class XmlDocTextTransformer : IOpenApiDocumentTransformer
{
    public Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
    {
        new OpenApiWalker(new Visitor(new WireNames(document))).Walk(document);
        return Task.CompletedTask;
    }

    /// <summary>The cleaned text, with each member cref named as it appears on the wire where the document has it.</summary>
    internal static string? Clean(string? text, Func<string, string, string?> wireName)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return text;
        }

        var cleaned = Cref().Replace(text, match =>
        {
            var owner = match.Groups["owner"].Value;
            var member = match.Groups["member"].Value;
            var type = match.Groups["type"];

            // The type is optional (an enum value has none), so a sentence's first word can stand where it would be:
            // "See Lumen.Api". Such a word is kept, and the rest is only changed when it is a contract member.
            var typed = type.Success && LooksLikeType(type.Value);
            var prefix = type.Success && !typed ? type.Value + " " : "";
            if (wireName(owner, member) is { } wire)
            {
                return $"{prefix}`{wire}`";
            }

            if (!typed)
            {
                return match.Value;
            }

            // Not part of the contract (an option, a constant, a method): name it, without the C# signature.
            var call = match.Groups["args"].Success ? "()" : "";
            return $"`{owner.Replace("&lt;", "<", StringComparison.Ordinal).Replace("&gt;", ">", StringComparison.Ordinal)}.{member}{call}`";
        });

        var item = 0;
        var lines = cleaned.Split('\n').Select(line =>
        {
            var trimmed = line.Trim();
            return ListItem().IsMatch(trimmed) ? $"{++item}.{trimmed[2..]}" : trimmed;
        });
        return string.Join('\n', lines);
    }

    /// <summary>An example that is strict JSON inside the generator's fence, parsed; null when it is not.</summary>
    internal static JsonNode? CleanExample(JsonNode? example)
    {
        if (example is not JsonValue value || !value.TryGetValue<string>(out var text))
        {
            return example;
        }

        var fenced = text.Trim();
        if (!fenced.StartsWith("```", StringComparison.Ordinal) || !fenced.EndsWith("```", StringComparison.Ordinal))
        {
            return example;
        }

        try
        {
            return JsonNode.Parse(fenced.Trim('`'));
        }
        catch (JsonException)
        {
            // Some examples elide whole members or use comments, which reads fine in the code but is not JSON.
            return null;
        }
    }

    // A C# type as the generator prints it: a keyword or PascalCase name, generic arguments as entities, then ? or [].
    private const string TypePattern =
        @"(?:[A-Z]\w*|bool|byte|char|decimal|double|float|int|long|object|short|string|uint|ulong)(?:&lt;(?:[\w?, \[\]]|&lt;|&gt;)+?&gt;)?\??(?:\[\])?";

    /// <summary>
    /// A keyword, a nullable, generic or array type, or a PascalCase name with two or more capitals (OrderStatus,
    /// DateTime): English words that start a sentence have one.
    /// </summary>
    private static bool LooksLikeType(string type) =>
        char.IsLower(type[0]) || type.Contains('?', StringComparison.Ordinal) || type.Contains("&lt;", StringComparison.Ordinal)
        || type.EndsWith("[]", StringComparison.Ordinal) || type == "Guid" || type.Count(char.IsUpper) >= 2;

    [GeneratedRegex(@"(?<![\w.])(?:(?<type>" + TypePattern + @") )?(?<owner>[A-Z]\w*(?:&lt;[\w, ]+&gt;)?)\.(?<member>[A-Z]\w*)(?<args>\([^)]*\))?")]
    private static partial Regex Cref();

    [GeneratedRegex(@"^1\. ")]
    private static partial Regex ListItem();

    /// <summary>Property and enum value names of the document's own schemas, as the JSON writes them.</summary>
    private sealed class WireNames(OpenApiDocument document)
    {
        private readonly IDictionary<string, IOpenApiSchema> _schemas =
            document.Components?.Schemas ?? new Dictionary<string, IOpenApiSchema>();

        public string? Find(string owner, string member)
        {
            // PagedResult<T> is published once per T, as PagedResultOfProductDto and so on.
            var generic = owner.IndexOf("&lt;", StringComparison.Ordinal);
            var name = generic < 0 ? owner : owner[..generic];
            var schemas = _schemas.Where(s => generic < 0 ? s.Key == name : s.Key.StartsWith(name + "Of", StringComparison.Ordinal))
                .Select(s => s.Value).ToList();

            var property = JsonNamingPolicy.CamelCase.ConvertName(member);
            if (schemas.Any(s => s.Properties?.ContainsKey(property) == true))
            {
                return property;
            }

            var value = JsonNamingPolicy.SnakeCaseLower.ConvertName(member);
            return schemas.Any(s => s.Enum?.Any(e => e is JsonValue v && v.TryGetValue<string>(out var text) && text == value) == true)
                ? value
                : null;
        }
    }

    private sealed class Visitor(WireNames names) : OpenApiVisitorBase
    {
        public override void Visit(IOpenApiSchema schema)
        {
            switch (schema)
            {
                // A reference keeps only a description of its own; otherwise its Description reads the target's.
                case OpenApiSchemaReference reference when reference.Reference.Description is { } own:
                    reference.Reference.Description = Clean(own, names.Find);
                    break;
                case OpenApiSchema concrete:
                    concrete.Description = Clean(concrete.Description, names.Find);
                    if (concrete.Examples is { } examples)
                    {
                        var kept = examples.Select(CleanExample).OfType<JsonNode>().ToList();
                        concrete.Examples = kept.Count > 0 ? kept : null;
                    }

                    break;
            }
        }

        public override void Visit(OpenApiOperation operation)
        {
            operation.Summary = Clean(operation.Summary, names.Find);
            operation.Description = Clean(operation.Description, names.Find);
        }

        public override void Visit(IOpenApiParameter parameter)
        {
            if (parameter is OpenApiParameter concrete)
            {
                concrete.Description = Clean(concrete.Description, names.Find);
            }
        }

        public override void Visit(IOpenApiRequestBody requestBody)
        {
            if (requestBody is OpenApiRequestBody concrete)
            {
                concrete.Description = Clean(concrete.Description, names.Find);
            }
        }

        public override void Visit(IOpenApiResponse response)
        {
            if (response is OpenApiResponse concrete)
            {
                concrete.Description = Clean(concrete.Description, names.Find);
            }
        }
    }
}
