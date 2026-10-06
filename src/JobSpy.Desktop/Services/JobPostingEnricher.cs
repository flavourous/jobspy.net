using System;
using System.Linq;
using System.Text.RegularExpressions;
using JobSpy.Desktop.Models;

namespace JobSpy.Desktop.Services;

public static class JobPostingEnricher
{
    public const int CurrentVersion = 1;

    private const string CurrencyPattern = @"£|€|\$|GBP\b|USD\b|EUR\b";
    private const string AmountPattern = @"(?:\d{1,3}(?:,\d{3})+(?:\.\d+)?|\d{4,6}(?:\.\d+)?|\d{2,3}(?:\.\d+)?)";
    private const string PeriodPattern = @"(?:/\s*(?:yr|year|annum|mo|month|wk|week|day|hr|hour)|per\s+(?:year|annum|month|week|day|hour)|annually|annual|monthly|weekly|daily|hourly)";

    private static readonly Regex SalaryRangePattern = new(
        $@"(?<![\w\d.,])(?<currency>{CurrencyPattern})?\s*(?<first>{AmountPattern})(?<firstScale>\s*[kKmM])?\s*(?:-|–|—|to)\s*(?<secondCurrency>{CurrencyPattern})?\s*(?<second>{AmountPattern})(?<secondScale>\s*[kKmM])?(?:\s*(?<trailingCurrency>GBP\b|USD\b|EUR\b))?(?:\s*(?<period>{PeriodPattern}))?(?![\w\d.,])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex SalaryAmountPattern = new(
        $@"(?<![\w\d.,])(?<currency>{CurrencyPattern})?\s*(?<amount>{AmountPattern})(?<scale>\s*[kKmM])?(?:\s*(?<trailingCurrency>GBP\b|USD\b|EUR\b))?(?:\s*(?<period>{PeriodPattern}))?(?![\w\d.,])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex SalaryContextPattern = new(
        @"\b(?:salary|salaries|compensation|remuneration|pay|rate|earnings|package|OTE|day rate|hourly rate)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex ExplicitTypePattern = new(
        @"\b(?:job\s*type|employment\s*type|type)\s*[:\-]\s*(?<type>[^\r\n.;]{1,100})",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex RoleTypePattern = new(
        @"\b(?<type>fixed[- ]term(?:\s+contract)?|FTC|contract|freelance|temporary|part[- ]time|full[- ]time|permanent)\s+(?:role|position|opportunity)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex FixedTermPattern = new(
        @"\b(?:FTC|fixed[- ]term(?:\s+contract)?)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static void Enrich(JobPosting posting)
    {
        posting.EnrichedSalary = ExtractSalary(posting.Title, posting.Description);
        posting.EnrichedType = ExtractType(posting.Title, posting.Description);
        posting.EnrichmentVersion = CurrentVersion;
    }

    private static string? ExtractSalary(string? title, string? description)
    {
        var titleSalary = FindSalary(title, isTitle: true);
        return titleSalary ?? FindSalary(description, isTitle: false);
    }

    private static string? FindSalary(string? text, bool isTitle)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        foreach (Match match in SalaryRangePattern.Matches(text))
        {
            var hasScale = match.Groups["firstScale"].Success || match.Groups["secondScale"].Success;
            if (isTitle || hasScale || HasSalaryContext(text, match) || match.Groups["period"].Success)
            {
                return Normalize(match.Value);
            }
        }

        foreach (Match match in SalaryAmountPattern.Matches(text))
        {
            var hasScale = match.Groups["scale"].Success;
            var hasAnnualOrDayRate = Regex.IsMatch(
                match.Groups["period"].Value,
                @"day|hour|year|annum|annual",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (isTitle || HasSalaryContext(text, match) || hasAnnualOrDayRate)
            {
                return Normalize(match.Value);
            }
        }

        return null;
    }

    private static bool HasSalaryContext(string text, Match match)
    {
        var start = match.Index;
        while (start > 0 && text[start - 1] is not '.' and not ';' and not '!' and not '?' and not '\n' and not '\r')
        {
            start--;
        }

        var end = match.Index + match.Length;
        while (end < text.Length && text[end] is not '.' and not ';' and not '!' and not '?' and not '\n' and not '\r')
        {
            end++;
        }

        return SalaryContextPattern.IsMatch(text[start..end]);
    }

    private static string? ExtractType(string? title, string? description)
    {
        var titleType = ParseType(title);
        if (titleType is not null)
        {
            return titleType;
        }

        if (string.IsNullOrWhiteSpace(description))
        {
            return null;
        }

        var explicitType = ExplicitTypePattern.Match(description);
        if (explicitType.Success)
        {
            var parsed = ParseType(explicitType.Groups["type"].Value);
            if (parsed is not null)
            {
                return parsed;
            }
        }

        if (FixedTermPattern.IsMatch(description))
        {
            return "Fixed-term contract";
        }

        return ParseType(RoleTypePattern.Match(description).Groups["type"].Value);
    }

    private static string? ParseType(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var normalized = text.Replace('–', '-').Replace('—', '-');
        if (Regex.IsMatch(normalized, @"\b(?:FTC|fixed[- ]term)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return "Fixed-term contract";
        }

        var types = new[]
        {
            (Pattern: @"\bfull[- ]time\b", Label: "Full-time"),
            (Pattern: @"\bpart[- ]time\b", Label: "Part-time"),
            (Pattern: @"\bpermanent\b", Label: "Permanent"),
            (Pattern: @"\btemporary\b", Label: "Temporary"),
            (Pattern: @"\bfreelance\b", Label: "Freelance"),
            (Pattern: @"\bcontract(?:or)?\b", Label: "Contract"),
        };
        var matches = types
            .Where(type => Regex.IsMatch(normalized, type.Pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            .Select(type => type.Label)
            .ToArray();
        return matches.Length == 0 ? null : string.Join(", ", matches);
    }

    private static string Normalize(string value) => Regex.Replace(value.Trim(), @"\s+", " ");
}