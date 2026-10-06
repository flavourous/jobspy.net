using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace JobSpy.Desktop.Services;

public static class SalaryCurrencyConverter
{
    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(8),
    };

    private static IReadOnlyDictionary<string, decimal> _gbpPerUnit = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase)
    {
        ["GBP"] = 1m,
    };

    public static async Task<bool> RefreshRatesAsync()
    {
        try
        {
            using var response = await HttpClient.GetAsync(
                "https://api.frankfurter.dev/v1/latest?base=GBP&symbols=USD,EUR").ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using var content = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(content).ConfigureAwait(false);
            var root = document.RootElement;
            if (!string.Equals(root.GetProperty("base").GetString(), "GBP", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var rates = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase)
            {
                ["GBP"] = 1m,
            };
            foreach (var rate in root.GetProperty("rates").EnumerateObject())
            {
                var foreignUnitsPerGbp = rate.Value.GetDecimal();
                if (foreignUnitsPerGbp > 0)
                {
                    rates[rate.Name] = 1m / foreignUnitsPerGbp;
                }
            }

            Volatile.Write(ref _gbpPerUnit, rates);
            return true;
        }
        catch (Exception exception) when (exception is HttpRequestException
            or TaskCanceledException
            or JsonException
            or KeyNotFoundException
            or InvalidOperationException)
        {
            return false;
        }
    }

    public static decimal? ToGbp(decimal amount, string? currency)
    {
        var normalizedCurrency = currency?.Trim().ToUpperInvariant() switch
        {
            "£" or "GBP" => "GBP",
            "€" or "EUR" => "EUR",
            "$" or "US$" or "USD" => "USD",
            _ => null,
        };
        var rates = Volatile.Read(ref _gbpPerUnit);
        if (normalizedCurrency is null || !rates.TryGetValue(normalizedCurrency, out var rate))
        {
            return null;
        }

        return amount * rate;
    }
}