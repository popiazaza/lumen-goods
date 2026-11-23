using System.Globalization;
using System.Net;
using CsvHelper;
using Lumen.Api.Partners.Ports;
using Lumen.Domain.Catalog;
using Microsoft.Extensions.Options;

namespace Lumen.Api.Partners.Kestrel;

/// <summary>
/// Kestrel stock (<c>GET /stock?refs=a,b</c>): semicolon CSV, header row first. Requests are chunked to at most 20
/// distinct refs (the contract's limit). Columns are found by header name, not position; BOM, CRLF, quoting, blank
/// lines and extra columns are tolerated. A ref the partner leaves out is unknown (out of stock, quantity null), never
/// in stock without evidence (BR-CAT-01). A failed chunk leaves its products' stock as last read.
/// </summary>
internal sealed class KestrelStockSource(IHttpClientFactory httpClients, IOptionsMonitor<KestrelOptions> options) : IPartnerStockSource
{
    public string Partner => ProductSources.Kestrel;

    public async Task<StockFetchResult> FetchStockAsync(IReadOnlyCollection<string> sourceProductIds, CancellationToken cancellationToken)
    {
        var settings = options.CurrentValue;
        var batchSize = Math.Clamp(settings.StockBatchSize, 1, KestrelOptions.ContractMaxRefsPerStockRequest);
        var client = httpClients.CreateClient(KestrelCatalogSource.HttpClientName);

        var levels = new Dictionary<string, StockLevel>(StringComparer.Ordinal);
        var problems = new List<string>();
        var failures = new List<PartnerException>();
        var chunks = sourceProductIds.Where(id => !string.IsNullOrWhiteSpace(id) && !id.Contains(','))
            .Distinct(StringComparer.Ordinal)
            .Chunk(batchSize)
            .ToList();

        foreach (var chunk in chunks)
        {
            try
            {
                var rows = await GetChunkAsync(client, chunk, problems, cancellationToken);
                foreach (var reference in chunk)
                {
                    levels[reference] = rows.GetValueOrDefault(reference) ?? StockLevel.Unknown;
                }
            }
            catch (PartnerException ex)
            {
                failures.Add(ex);
            }
        }

        if (chunks.Count > 0 && failures.Count == chunks.Count)
        {
            throw new PartnerException($"Kestrel stock could not be read: {failures[0].Message}", failures[0].StatusCode, failures[0]);
        }

        return new StockFetchResult(levels, problems, [.. failures.Select(f => f.Message)]);
    }

    private static async Task<Dictionary<string, StockLevel>> GetChunkAsync(
        HttpClient client, string[] references, List<string> problems, CancellationToken cancellationToken)
    {
        // Each ref is escaped; the commas between them are the contract's separator.
        var url = "stock?refs=" + string.Join(",", references.Select(Uri.EscapeDataString));
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await client.SendToPartnerAsync(request, HttpCompletionOption.ResponseContentRead, "Kestrel", cancellationToken);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            var detail = await PartnerHttpClients.ReadSnippetAsync(response, 300, cancellationToken);
            throw new PartnerException(
                $"Kestrel GET /stock returned {(int)response.StatusCode} {response.ReasonPhrase}{(detail.Length > 0 ? $": {detail}" : "")}.",
                (int)response.StatusCode);
        }

        // ReadAsStringAsync honours the charset parameter and strips a BOM.
        var csv = await response.Content.ReadAsStringAsync(cancellationToken);
        return KestrelStockCsv.Parse(csv, problems);
    }
}

/// <summary>The /stock CSV, parsed by header name.</summary>
internal static class KestrelStockCsv
{
    // Units are whole numbers with an optional ASCII minus. Parsed with an explicit NumberFormatInfo, never a culture
    // (InvariantGlobalization is on, and a culture's rules can change), like KestrelPrice.
    private static readonly NumberFormatInfo UnitsFormat = new()
    {
        NegativeSign = "-",
        PositiveSign = "+",
    };

    private static readonly CsvReaderOptions Options = new()
    {
        Separator = ';',
        HasHeader = true,
        SkipEmptyLines = true,
        TrimWhitespace = true,
        FormatProvider = UnitsFormat,
    };

    public static Dictionary<string, StockLevel> Parse(string text, List<string> problems)
    {
        var rows = new Dictionary<string, StockLevel>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(text.TrimStart('﻿')))
        {
            throw new PartnerDataException("Kestrel stock CSV is empty (no header row).");
        }

        using var csv = CsvReader.FromText(text.TrimStart('﻿'), Options);
        csv.ReadHeader();

        // Matched exactly, else ignoring case and surrounding whitespace; the column order is not assumed.
        if (!csv.TryGetFieldIndex("ref", out var refColumn) || !csv.TryGetFieldIndex("units", out var unitsColumn))
        {
            throw new PartnerDataException($"Kestrel stock CSV has no ref/units header (got '{string.Join(";", csv.Headers)}').");
        }

        while (csv.Read())
        {
            var reference = csv.GetString(refColumn)?.Trim();
            if (string.IsNullOrEmpty(reference))
            {
                continue;
            }

            var units = csv.GetString(unitsColumn)?.Trim();
            if (int.TryParse(units, NumberStyles.AllowLeadingSign, UnitsFormat, out var quantity))
            {
                if (quantity < 0)
                {
                    problems.Add($"Kestrel stock for {reference}: negative units {quantity} read as 0.");
                }

                rows.TryAdd(reference, StockLevel.Exactly(quantity));
            }
            else
            {
                problems.Add($"Kestrel stock for {reference}: units '{units}' is not a whole number; stock unknown.");
                rows.TryAdd(reference, StockLevel.Unknown);
            }
        }

        return rows;
    }
}
