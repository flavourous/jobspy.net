using System;
using LiteDB;

namespace JobSpy.Desktop.Models;

public sealed class JobSearchSnapshot
{
    [BsonId]
    public string Id { get; set; } = string.Empty;

    public DateTime CapturedAtUtc { get; set; }

    public string SearchProfile { get; set; } = string.Empty;

    public int AvailableJobCount { get; set; }

    public int SalaryJobCount { get; set; }

    public decimal? MedianAnnualSalaryGbp { get; set; }

    public bool IsDemo { get; set; }
}