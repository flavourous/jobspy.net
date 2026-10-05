using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using JobSpy.Desktop.Models;

namespace JobSpy.Desktop.Services;

public static class JobLocationClassifier
{
    private const double CoventryLatitude = 52.4068;
    private const double CoventryLongitude = -1.5197;
    private const double CommuteRadiusMiles = 12;
    private static readonly HttpClient HttpClient = CreateHttpClient();
    private static readonly ConcurrentDictionary<string, Lazy<Task<bool?>>> RelocationCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly SemaphoreSlim ApiRequestGate = new(2);

    private static readonly IReadOnlyDictionary<string, (double Latitude, double Longitude)> Locations =
        new Dictionary<string, (double Latitude, double Longitude)>(StringComparer.OrdinalIgnoreCase)
        {
            ["Coventry"] = (52.4068, -1.5197),
            ["Warwick"] = (52.2823, -1.5849),
            ["Royal Leamington Spa"] = (52.2852, -1.5200),
            ["Leamington Spa"] = (52.2852, -1.5200),
            ["Leamington"] = (52.2852, -1.5200),
            ["Kenilworth"] = (52.3490, -1.5820),
            ["Rugby"] = (52.3709, -1.2650),
            ["Nuneaton"] = (52.5225, -1.4680),
            ["Bedworth"] = (52.4791, -1.4690),
            ["Solihull"] = (52.4128, -1.7782),
            ["Birmingham"] = (52.4862, -1.8904),
            ["Worcester"] = (52.1936, -2.2216),
            ["Stratford-upon-Avon"] = (52.1917, -1.7083),
            ["Hinckley"] = (52.5440, -1.3730),
            ["Leicester"] = (52.6369, -1.1398),
            ["Bristol"] = (51.4545, -2.5879),
            ["Oxford"] = (51.7520, -1.2577),
            ["London"] = (51.5072, -0.1276),
            ["Manchester"] = (53.4808, -2.2426),
            ["Liverpool"] = (53.4084, -2.9916),
            ["Leeds"] = (53.8008, -1.5491),
            ["Sheffield"] = (53.3811, -1.4701),
            ["Nottingham"] = (52.9548, -1.1581),
            ["Derby"] = (52.9225, -1.4746),
            ["Newcastle"] = (54.9783, -1.6178),
            ["Cardiff"] = (51.4816, -3.1791),
            ["Edinburgh"] = (55.9533, -3.1883),
            ["Glasgow"] = (55.8642, -4.2518),
        };

    public static bool NeedsRelocation(JobPosting posting)
    {
        return TryGetNeedsRelocation(posting) ?? true;
    }

    public static async Task<bool?> ResolveNeedsRelocationAsync(JobPosting posting)
    {
        var knownResult = TryGetNeedsRelocation(posting);
        if (knownResult.HasValue)
        {
            return knownResult;
        }

        var location = posting.Location?.Trim();
        if (string.IsNullOrWhiteSpace(location))
        {
            return null;
        }

        var cachedResult = RelocationCache.GetOrAdd(
            location,
            value => new Lazy<Task<bool?>>(() => ResolveUnknownLocationAsync(value)));
        var result = await cachedResult.Value.ConfigureAwait(false);
        if (!result.HasValue)
        {
            RelocationCache.TryRemove(location, out _);
        }

        return result;
    }

    private static bool? TryGetNeedsRelocation(JobPosting posting)
    {
        var location = posting.Location?.Trim();
        if (posting.IsRemote
            || string.IsNullOrWhiteSpace(location)
            || location.Contains("remote", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (posting.RequiresRelocation.HasValue)
        {
            return posting.RequiresRelocation.Value;
        }

        var match = Locations
            .Where(entry => location.Contains(entry.Key, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(entry => entry.Key.Length)
            .Select(entry => ((double Latitude, double Longitude)?)entry.Value)
            .FirstOrDefault();
        return match.HasValue
            ? DistanceMiles(CoventryLatitude, CoventryLongitude, match.Value.Latitude, match.Value.Longitude) > CommuteRadiusMiles
            : null;
    }

    private static async Task<bool?> ResolveUnknownLocationAsync(string location)
    {
        var query = location.Contains("united kingdom", StringComparison.OrdinalIgnoreCase)
            ? location
            : $"{location}, United Kingdom";
        var requestUri = $"https://photon.komoot.io/api/?q={Uri.EscapeDataString(query)}&limit=1&lang=en";

        await ApiRequestGate.WaitAsync().ConfigureAwait(false);
        try
        {
            using var response = await HttpClient.GetAsync(requestUri).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using var content = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(content).ConfigureAwait(false);
            if (!document.RootElement.TryGetProperty("features", out var features))
            {
                return null;
            }

            foreach (var feature in features.EnumerateArray())
            {
                if (!feature.TryGetProperty("properties", out var properties)
                    || !properties.TryGetProperty("countrycode", out var countryCode)
                    || !string.Equals(countryCode.GetString(), "GB", StringComparison.OrdinalIgnoreCase)
                    || !feature.TryGetProperty("geometry", out var geometry)
                    || !geometry.TryGetProperty("coordinates", out var coordinates)
                    || coordinates.GetArrayLength() < 2)
                {
                    continue;
                }

                var longitude = coordinates[0].GetDouble();
                var latitude = coordinates[1].GetDouble();
                return DistanceMiles(CoventryLatitude, CoventryLongitude, latitude, longitude) > CommuteRadiusMiles;
            }

            return null;
        }
        catch (Exception exception) when (exception is HttpRequestException
            or TaskCanceledException
            or JsonException
            or InvalidOperationException)
        {
            return null;
        }
        finally
        {
            ApiRequestGate.Release();
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("JobSpy.Desktop/1.0");
        return client;
    }

    private static double DistanceMiles(double latitudeA, double longitudeA, double latitudeB, double longitudeB)
    {
        const double earthRadiusMiles = 3958.7613;
        var latitudeDelta = DegreesToRadians(latitudeB - latitudeA);
        var longitudeDelta = DegreesToRadians(longitudeB - longitudeA);
        var haversine = Math.Pow(Math.Sin(latitudeDelta / 2), 2)
            + Math.Cos(DegreesToRadians(latitudeA))
            * Math.Cos(DegreesToRadians(latitudeB))
            * Math.Pow(Math.Sin(longitudeDelta / 2), 2);
        return earthRadiusMiles * 2 * Math.Asin(Math.Sqrt(haversine));
    }

    private static double DegreesToRadians(double degrees) => degrees * Math.PI / 180;
}