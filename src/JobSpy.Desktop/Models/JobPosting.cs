using System;
using System.Globalization;
using System.Linq;
using System.Text.Json.Serialization;
using LiteDB;

namespace JobSpy.Desktop.Models;

public sealed class JobPosting
{
    private const decimal ContractWorkingDaysPerYear = 220;
    private const decimal ContractWorkingHoursPerYear = 1650;
    private const decimal StandardWorkingDaysPerYear = 260;
    private const decimal StandardWorkingHoursPerYear = 1950;

    [BsonId]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("site")]
    public string? Site { get; set; }

    [JsonPropertyName("job_url")]
    public string? JobUrl { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("company")]
    public string? Company { get; set; }

    [JsonPropertyName("location")]
    public string? Location { get; set; }

    [JsonPropertyName("date_posted")]
    public string? DatePosted { get; set; }

    [JsonPropertyName("job_type")]
    public string? JobType { get; set; }

    [JsonPropertyName("interval")]
    public string? Interval { get; set; }

    [JsonPropertyName("min_amount")]
    public decimal? MinAmount { get; set; }

    [JsonPropertyName("max_amount")]
    public decimal? MaxAmount { get; set; }

    [JsonPropertyName("currency")]
    public string? Currency { get; set; }

    [BsonField("enrichedSalary")]
    public string? EnrichedSalary { get; set; }

    [BsonField("enrichedSalaryAmount")]
    public decimal? EnrichedSalaryAmount { get; set; }

    [BsonField("enrichedSalaryMinAmount")]
    public decimal? EnrichedSalaryMinAmount { get; set; }

    [BsonField("enrichedSalaryCurrency")]
    public string? EnrichedSalaryCurrency { get; set; }

    [BsonField("enrichedSalaryInterval")]
    public string? EnrichedSalaryInterval { get; set; }

    [BsonField("enrichedType")]
    public string? EnrichedType { get; set; }

    [BsonField("enrichmentVersion")]
    public int EnrichmentVersion { get; set; }

    [JsonPropertyName("is_remote")]
    public bool IsRemote { get; set; }

    [BsonIgnore]
    [JsonIgnore]
    public string? SearchTerm { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    public DateTime FirstSeenUtc { get; set; }

    public DateTime LastSeenUtc { get; set; }

    public DateTime? DisappearedAtUtc { get; set; }

    [BsonField("status")]
    public string Status { get; set; } = OpportunityStatus.New;

    public bool IsStarred { get; set; }

    public bool? RequiresRelocation { get; set; }

    [BsonIgnore]
    public string DatePostedLabel => string.IsNullOrWhiteSpace(DatePosted) ? "Date not listed" : $"Posted {DatePosted}";

    [BsonIgnore]
    public string EffectiveJobType => !string.IsNullOrWhiteSpace(JobType) ? JobType : EnrichedType ?? string.Empty;

    [BsonIgnore]
    public string EmploymentLabel => IsRemote ? "Remote" : EffectiveJobType;

    [BsonIgnore]
    public string SalaryLabel
    {
        get
        {
            var salaryRange = AnnualSalaryRangeGbp;
            if (!salaryRange.HasValue)
            {
                return "Salary not listed";
            }

            var (minimum, maximum) = salaryRange.Value;
            return minimum == maximum
                ? $"£{maximum:N0} / year"
                : $"£{minimum:N0}-{maximum:N0} / year";
        }
    }

    [BsonIgnore]
    public string DescriptionPreview
    {
        get
        {
            var preview = (Description ?? string.Empty).Replace('\n', ' ').Trim();
            return preview.Length > 260 ? $"{preview[..260]}..." : preview;
        }
    }

    [BsonIgnore]
    public decimal? AnnualSalaryGbp
        => AnnualSalaryRangeGbp?.Max;

    [BsonIgnore]
    public (decimal Min, decimal Max)? AnnualSalaryRangeGbp
    {
        get
        {
            if (MinAmount.HasValue || MaxAmount.HasValue)
            {
                var salaryCurrency = string.IsNullOrWhiteSpace(Currency) ? "GBP" : Currency;
                var minimum = MinAmount ?? MaxAmount!.Value;
                var maximum = MaxAmount ?? MinAmount!.Value;
                var annualMinimum = ToAnnualGbp(minimum, salaryCurrency, Interval);
                var annualMaximum = ToAnnualGbp(maximum, salaryCurrency, Interval);
                if (annualMinimum.HasValue && annualMaximum.HasValue)
                {
                    return (Math.Min(annualMinimum.Value, annualMaximum.Value), Math.Max(annualMinimum.Value, annualMaximum.Value));
                }
            }

            if (!EnrichedSalaryAmount.HasValue)
            {
                return null;
            }

            var enrichedCurrency = EnrichedSalaryCurrency ?? (string.IsNullOrWhiteSpace(Currency) ? "GBP" : Currency);
            var enrichedMinimum = EnrichedSalaryMinAmount ?? EnrichedSalaryAmount.Value;
            var annualEnrichedMinimum = ToAnnualGbp(enrichedMinimum, enrichedCurrency, EnrichedSalaryInterval);
            var annualEnrichedMaximum = ToAnnualGbp(EnrichedSalaryAmount.Value, enrichedCurrency, EnrichedSalaryInterval);
            return annualEnrichedMinimum.HasValue && annualEnrichedMaximum.HasValue
                ? (Math.Min(annualEnrichedMinimum.Value, annualEnrichedMaximum.Value), Math.Max(annualEnrichedMinimum.Value, annualEnrichedMaximum.Value))
                : null;
        }
    }

    private decimal? ToAnnualGbp(decimal amount, string? currency, string? interval)
    {
        var normalizedInterval = interval?.Trim().ToLowerInvariant();
        var isContract = EffectiveJobType.Contains("contract", StringComparison.OrdinalIgnoreCase)
            || EffectiveJobType.Contains("freelance", StringComparison.OrdinalIgnoreCase)
            || EffectiveJobType.Contains("temporary", StringComparison.OrdinalIgnoreCase);
        var annualAmount = normalizedInterval switch
        {
            "year" or "yr" or "yearly" or "annual" or "annually" or "per year" or "per annum" => amount,
            "month" or "mo" or "monthly" or "per month" => amount * 12,
            "week" or "wk" or "weekly" or "per week" => amount * 52,
            "day" or "daily" or "per day" => amount * (isContract ? ContractWorkingDaysPerYear : StandardWorkingDaysPerYear),
            "hour" or "hr" or "hourly" or "per hour" => amount * (isContract ? ContractWorkingHoursPerYear : StandardWorkingHoursPerYear),
            null or "" or "none" or "unknown" when amount >= 10000 => amount,
            _ => 0m,
        };
        if (annualAmount <= 0)
        {
            return null;
        }

        return Services.SalaryCurrencyConverter.ToGbp(annualAmount, currency);
    }

}

public static class OpportunityStatus
{
    public const string New = "new";
    public const string Ignored = "ignored";
    public const string Interested = "interested";
    public const string Applied = "applied";
    public const string Interview = "interview";
    public const string Offer = "offer";
    public const string Rejected = "rejected";

    public static readonly string[] All =
    [
        New,
        Ignored,
        Interested,
        Applied,
        Interview,
        Offer,
        Rejected,
    ];

    public static string Normalize(string? status) => All.FirstOrDefault(value =>
        string.Equals(value, status, StringComparison.OrdinalIgnoreCase)) ?? New;
}