using System;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using JobSpy.Desktop.Models;

namespace JobSpy.Desktop.Services;

public static class JobPostingEnricher
{
    public const int CurrentVersion = 7;

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
        @"\b(?:salary|salaries|compensation|remuneration|pay\s+range|pay\s+rate|earnings?\s+(?:range|between|of)|OTE|day\s+rate|daily\s+rate|hourly\s+rate)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex NonSalaryContextPattern = new(
        @"\b(?:valuation|valued\s+at|market\s+cap|revenue|turnover|funding|fundrais(?:e|ing)|raised|investment|portfolio|assets?\s+under\s+management|AUM|budget|costs?|contract\s+value|retirement|pension|salary\s+sacrifice)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex RetirementPlanPattern = new(
        @"\b401\s*\(?\s*k\s*\)?\b",
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
    private static readonly Regex FortyHourWeekPattern = new(
        @"\b40\s*(?:hours?|hrs?|h)\s*(?:(?:per|a)\s+|/\s*)week\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex LimitedDurationPattern = new(
        @"\b(?:contract(?:or)?|FTC|fixed[- ]term|temporary|freelance|intern(?:ship)?|seasonal|limited[- ]duration|\d+\s*(?:months?|years?))\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex HybridWorkPatternRegex = new(
        @"^hybrid\s*(?::|\(\s*|\s+)?(?<days>\d+(?:\.\d+)?)\s*(?:days?(?:\s*(?:/|per)\s*week)?|d\s*/\s*w(?:eek)?)?\s*\)?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static void Enrich(JobPosting posting)
    {
        posting.EnrichedType = ExtractType(posting.Title, posting.Description);
        posting.EnrichedLocation = null;
        posting.EnrichedWorkPattern = posting.IsRemote ? "remote" : null;
        posting.EnrichedSalary = ExtractSalary(posting.Title, posting.Description);
        var salaryDetails = ParseSalary(posting.EnrichedSalary, posting.EffectiveJobType);
        posting.EnrichedSalaryMinAmount = salaryDetails?.MinAmount;
        posting.EnrichedSalaryAmount = salaryDetails?.MaxAmount;
        posting.EnrichedSalaryCurrency = salaryDetails?.Currency ?? posting.Currency ?? "GBP";
        posting.EnrichedSalaryInterval = salaryDetails?.Interval;
        posting.EnrichmentVersion = CurrentVersion;
    }

    public static bool ApplyAiCorrection(
        JobPosting posting,
        string? employmentType,
        string? location,
        string? salary,
        string? workPattern)
    {
        var changed = false;
        if (string.IsNullOrWhiteSpace(posting.JobType))
        {
            var normalizedType = ParseType(employmentType);
            if (normalizedType is not null && normalizedType != posting.EnrichedType)
            {
                posting.EnrichedType = normalizedType;
                changed = true;
            }
        }

        if (string.IsNullOrWhiteSpace(posting.Location))
        {
            var normalizedLocation = NormalizeAiLocation(location);
            if (normalizedLocation is not null && normalizedLocation != posting.EnrichedLocation)
            {
                posting.EnrichedLocation = normalizedLocation;
                changed = true;
            }
        }

        if (!posting.MinAmount.HasValue && !posting.MaxAmount.HasValue)
        {
            var normalizedSalary = NormalizeAiSalary(salary);
            var salaryDetails = ParseSalary(normalizedSalary, posting.EffectiveJobType);
            if (normalizedSalary is not null && salaryDetails is not null && normalizedSalary != posting.EnrichedSalary)
            {
                posting.EnrichedSalary = normalizedSalary;
                posting.EnrichedSalaryMinAmount = salaryDetails?.MinAmount;
                posting.EnrichedSalaryAmount = salaryDetails?.MaxAmount;
                posting.EnrichedSalaryCurrency = salaryDetails?.Currency ?? posting.Currency ?? "GBP";
                posting.EnrichedSalaryInterval = salaryDetails?.Interval;
                changed = true;
            }
        }

        if (!posting.IsRemote)
        {
            var normalizedWorkPattern = NormalizeAiWorkPattern(workPattern);
            if (normalizedWorkPattern is not null && normalizedWorkPattern != posting.EnrichedWorkPattern)
            {
                posting.EnrichedWorkPattern = normalizedWorkPattern;
                changed = true;
            }
        }

        if (changed)
        {
            posting.AiCorrected = true;
        }

        return changed;
    }

    internal static bool IsValidAiEmploymentType(string? value) =>
        string.IsNullOrWhiteSpace(value) || ParseType(value) is not null;

    internal static string? NormalizeAiEmploymentType(string? value) => ParseType(value);

    internal static bool HasAiEmploymentEvidence(JobPosting posting)
    {
        var title = posting.Title ?? string.Empty;
        var description = posting.Description ?? string.Empty;
        return ParseType(title) is not null
            || ExplicitTypePattern.IsMatch(description)
            || FixedTermPattern.IsMatch(description)
            || RoleTypePattern.IsMatch(description)
            || Regex.IsMatch(description, @"\b(?:full[- ]time|part[- ]time|permanent|temporary|freelance)\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            || FortyHourWeekPattern.IsMatch($"{title}\n{description}");
    }

    internal static bool RequiresPermanentClassification(JobPosting posting)
    {
        var source = $"{posting.Title}\n{posting.Description}";
        return FortyHourWeekPattern.IsMatch(source) && !LimitedDurationPattern.IsMatch(source);
    }

    internal static bool HasAiSalaryEvidence(JobPosting posting) =>
        ExtractSalary(posting.Title, posting.Description) is not null;

    internal static bool IsValidAiLocation(string? value) =>
        string.IsNullOrWhiteSpace(value) || NormalizeAiLocation(value) is not null;

    internal static bool IsValidAiSalary(string? value) =>
        string.IsNullOrWhiteSpace(value) || ParseSalary(NormalizeAiSalary(value), null) is not null;

    internal static bool IsValidAiWorkPattern(string? value) =>
        string.IsNullOrWhiteSpace(value) || NormalizeAiWorkPattern(value) is not null;

    internal static string? NormalizeAiLocation(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = Normalize(value);
        return normalized.Length <= 200 && !normalized.Any(char.IsControl) ? normalized : null;
    }

    internal static string? NormalizeAiSalary(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : Normalize(value);

    internal static string? NormalizeAiWorkPattern(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim().ToLowerInvariant().Replace('-', ' ');
        if (normalized is "remote" or "fully remote" or "remote only" or "work from home" or "wfh")
        {
            return "remote";
        }

        if (normalized is "onsite" or "on site" or "on premises" or "in office" or "office based")
        {
            return "onsite";
        }

        if (normalized == "hybrid")
        {
            return "hybrid";
        }

        var hybridMatch = HybridWorkPatternRegex.Match(value.Trim());
        if (hybridMatch.Success
            && decimal.TryParse(hybridMatch.Groups["days"].Value, NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out var daysPerWeek)
            && daysPerWeek > 0
            && daysPerWeek <= 7)
        {
            return $"hybrid:{daysPerWeek.ToString("0.###", CultureInfo.InvariantCulture)}";
        }

        return null;
    }

    public static string? FormatWorkPattern(string? value)
    {
        var normalized = NormalizeAiWorkPattern(value);
        if (normalized is null)
        {
            return null;
        }

        if (normalized == "remote")
        {
            return "Remote";
        }

        if (normalized == "onsite")
        {
            return "On-site";
        }

        if (normalized == "hybrid")
        {
            return "Hybrid (days/week not stated)";
        }

        var daysPerWeek = decimal.Parse(normalized[("hybrid:".Length)..], CultureInfo.InvariantCulture);
        var dayLabel = daysPerWeek == 1 ? "day" : "days";
        return $"Hybrid ({daysPerWeek.ToString("0.###", CultureInfo.InvariantCulture)} {dayLabel}/week)";
    }

    private static SalaryDetails? ParseSalary(string? salary, string? jobType)
    {
        if (string.IsNullOrWhiteSpace(salary))
        {
            return null;
        }

        var range = SalaryRangePattern.Match(salary);
        var amountMatch = range.Success ? range : SalaryAmountPattern.Match(salary);
        if (!amountMatch.Success)
        {
            return null;
        }

        var firstAmountGroup = range.Success ? amountMatch.Groups["first"] : amountMatch.Groups["amount"];
        var firstScaleGroup = range.Success ? amountMatch.Groups["firstScale"] : amountMatch.Groups["scale"];
        if (!decimal.TryParse(firstAmountGroup.Value.Replace(",", string.Empty), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var firstAmount))
        {
            return null;
        }

        firstAmount = ApplyScale(firstAmount, firstScaleGroup.Value);
        var minimumAmount = firstAmount;
        var maximumAmount = firstAmount;
        if (range.Success
            && decimal.TryParse(amountMatch.Groups["second"].Value.Replace(",", string.Empty), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var secondAmount))
        {
            var secondScale = amountMatch.Groups["secondScale"].Value;
            if (string.IsNullOrWhiteSpace(firstScaleGroup.Value) && !string.IsNullOrWhiteSpace(secondScale))
            {
                firstAmount = ApplyScale(firstAmount, secondScale);
            }

            secondAmount = ApplyScale(secondAmount, string.IsNullOrWhiteSpace(secondScale) ? firstScaleGroup.Value : secondScale);
            minimumAmount = Math.Min(firstAmount, secondAmount);
            maximumAmount = Math.Max(firstAmount, secondAmount);
        }

        var currencyText = amountMatch.Groups["currency"].Value;
        if (string.IsNullOrWhiteSpace(currencyText))
        {
            currencyText = amountMatch.Groups["secondCurrency"].Value;
        }

        if (string.IsNullOrWhiteSpace(currencyText))
        {
            currencyText = amountMatch.Groups["trailingCurrency"].Value;
        }

        var currency = string.IsNullOrWhiteSpace(currencyText)
            ? null
            : currencyText switch
            {
                "£" => "GBP",
                "€" => "EUR",
                "$" => "USD",
                _ => currencyText.ToUpperInvariant(),
            };
        var period = amountMatch.Groups["period"].Value.ToLowerInvariant();
        var interval = period.Contains("year", StringComparison.Ordinal)
            || period.Contains("yr", StringComparison.Ordinal)
            || period.Contains("annual", StringComparison.Ordinal)
            || period.Contains("annum", StringComparison.Ordinal)
            ? "year"
            : period.Contains("month", StringComparison.Ordinal)
                || period.Contains("mo", StringComparison.Ordinal)
                ? "month"
                : period.Contains("week", StringComparison.Ordinal)
                    || period.Contains("wk", StringComparison.Ordinal)
                    ? "week"
                    : period.Contains("day", StringComparison.Ordinal)
                        ? "day"
                        : period.Contains("hour", StringComparison.Ordinal)
                            || period.Contains("hr", StringComparison.Ordinal)
                            ? "hour"
                            : firstScaleGroup.Success || maximumAmount >= 10000
                                ? "year"
                                : jobType?.Contains("contract", StringComparison.OrdinalIgnoreCase) == true
                                    || jobType?.Contains("freelance", StringComparison.OrdinalIgnoreCase) == true
                                    ? "day"
                                    : null;

        return interval is null ? null : new SalaryDetails(minimumAmount, maximumAmount, currency, interval);
    }

    private static decimal ApplyScale(decimal amount, string scale) => scale.Trim().ToLowerInvariant() switch
    {
        "k" when amount < 1000 => amount * 1000,
        "m" when amount < 1000 => amount * 1000000,
        _ => amount,
    };

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
            if (IsLikelySalary(text, match, isTitle))
            {
                return Normalize(match.Value);
            }
        }

        foreach (Match match in SalaryAmountPattern.Matches(text))
        {
            if (IsLikelySalary(text, match, isTitle))
            {
                return Normalize(match.Value);
            }
        }

        return null;
    }

    private static bool IsLikelySalary(string text, Match match, bool isTitle)
    {
        if (RetirementPlanPattern.IsMatch(match.Value))
        {
            return false;
        }

        var firstScale = match.Groups["firstScale"].Success ? match.Groups["firstScale"].Value : match.Groups["scale"].Value;
        var secondScale = match.Groups["secondScale"].Value;
        var hasThousandScale = firstScale.Contains('k', StringComparison.OrdinalIgnoreCase)
            || secondScale.Contains('k', StringComparison.OrdinalIgnoreCase);
        var hasMillionScale = firstScale.Contains('m', StringComparison.OrdinalIgnoreCase)
            || secondScale.Contains('m', StringComparison.OrdinalIgnoreCase);
        var hasCurrency = !string.IsNullOrWhiteSpace(match.Groups["currency"].Value)
            || !string.IsNullOrWhiteSpace(match.Groups["secondCurrency"].Value)
            || !string.IsNullOrWhiteSpace(match.Groups["trailingCurrency"].Value);
        var period = match.Groups["period"].Value;
        var hasStrongPayPeriod = Regex.IsMatch(
            period,
            @"day|hour|year|annum|annual|\byr\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var hasAnnualSizedAmount = HasAnnualSizedAmount(match);
        var hasSalaryContext = HasSalaryContext(text, match);
        if (HasNonSalaryContext(text, match))
        {
            return false;
        }

        if (hasSalaryContext)
        {
            return true;
        }

        if (hasStrongPayPeriod)
        {
            return true;
        }

        if (hasMillionScale)
        {
            return false;
        }

        return hasThousandScale || hasCurrency && hasAnnualSizedAmount;
    }

    private static bool HasAnnualSizedAmount(Match match)
    {
        var first = match.Groups["first"].Success ? match.Groups["first"].Value : match.Groups["amount"].Value;
        var second = match.Groups["second"].Value;
        return decimal.TryParse(first.Replace(",", string.Empty), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var firstAmount)
            && (firstAmount >= 10000
                || decimal.TryParse(second.Replace(",", string.Empty), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var secondAmount)
                && secondAmount >= 10000);
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

    private static bool HasNonSalaryContext(string text, Match match)
    {
        var start = Math.Max(0, match.Index - 100);
        var end = Math.Min(text.Length, match.Index + match.Length + 100);
        return NonSalaryContextPattern.IsMatch(text[start..end]);
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

    private sealed record SalaryDetails(decimal MinAmount, decimal MaxAmount, string? Currency, string Interval);
}
