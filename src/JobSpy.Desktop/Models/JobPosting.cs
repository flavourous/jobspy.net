using System;
using System.Globalization;
using System.Linq;
using System.Text.Json.Serialization;
using LiteDB;

namespace JobSpy.Desktop.Models;

public sealed class JobPosting
{
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

    public bool IsStarred { get; set; }

    public bool? RequiresRelocation { get; set; }

    [BsonIgnore]
    public string DatePostedLabel => string.IsNullOrWhiteSpace(DatePosted) ? "Date not listed" : $"Posted {DatePosted}";

    [BsonIgnore]
    public string EmploymentLabel => IsRemote ? "Remote" : JobType ?? string.Empty;

    [BsonIgnore]
    public string SalaryLabel
    {
        get
        {
            var amounts = new[] { MinAmount, MaxAmount }
                .Where(amount => amount.HasValue)
                .Select(amount => amount!.Value.ToString("N0", CultureInfo.CurrentCulture))
                .ToArray();
            if (amounts.Length == 0)
            {
                return "Salary not listed";
            }

            var salary = amounts.Length == 1 ? amounts[0] : string.Join("-", amounts);
            var currency = string.IsNullOrWhiteSpace(Currency) ? string.Empty : $" {Currency}";
            var interval = string.IsNullOrWhiteSpace(Interval) ? string.Empty : $" / {Interval}";
            return $"{salary}{currency}{interval}";
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
    {
        get
        {
            if (!string.Equals(Currency, "GBP", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(Currency, "£", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var salary = MinAmount.HasValue && MaxAmount.HasValue
                ? Math.Max(MinAmount.Value, MaxAmount.Value)
                : MinAmount ?? MaxAmount;
            if (!salary.HasValue)
            {
                return null;
            }

            return Interval?.Trim().ToLowerInvariant() switch
            {
                "year" or "yearly" or "annual" or "annually" => salary,
                "month" or "monthly" => salary * 12,
                "week" or "weekly" => salary * 52,
                "day" or "daily" => salary * 260,
                "hour" or "hourly" => salary * 1950,
                _ => null,
            };
        }
    }

}