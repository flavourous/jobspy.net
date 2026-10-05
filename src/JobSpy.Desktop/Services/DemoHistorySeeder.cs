using System;
using System.Collections.Generic;
using System.Linq;
using JobSpy.Desktop.Models;
using JobSpy.Desktop.Repositories;

namespace JobSpy.Desktop.Services;

public static class DemoHistorySeeder
{
    public static void SeedIfRequested(IJobRepository repository)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("JOBSPY_DEMO_DATA"), "1", StringComparison.Ordinal)
            || repository.GetHistory().Count > 0)
        {
            return;
        }

        var roles = new Dictionary<string, JobPosting>
        {
            ["platform"] = CreatePosting("platform", "[Sample] Lead .NET Platform Engineer", "Example Systems", 90000, 110000),
            ["backend"] = CreatePosting("backend", "[Sample] Principal Backend Engineer", "Example Data", 105000, 125000),
            ["cloud"] = CreatePosting("cloud", "[Sample] Staff Cloud Engineer", "Example Cloud", 95000, 115000),
            ["manager"] = CreatePosting("manager", "[Sample] Engineering Manager", "Example Works", 100000, 120000),
            ["distributed"] = CreatePosting("distributed", "[Sample] Distributed Systems Lead", "Example Network", 110000, 130000),
            ["database"] = CreatePosting("database", "[Sample] Database Performance Engineer", "Example Storage", 85000, 105000, "FULL_TIME", "Manchester, United Kingdom", false),
            ["architecture"] = CreatePosting("architecture", "[Sample] Software Architect, C#", "Example Architecture", 90000, 108000),
            ["contract"] = CreatePosting("contract", "[Sample] Contract .NET Engineer", "Example Contracting", 650, 750, "CONTRACT", "Bristol, United Kingdom", false, "day"),
            ["contract2"] = CreatePosting("contract2", "[Sample] Contract Cloud Architect", "Example Contracting", 70000, 85000, "CONTRACT", "Coventry, United Kingdom", false),
            ["parttime"] = CreatePosting("parttime", "[Sample] Part-time Software Engineer", "Example Flexible", 45000, 55000, "PART_TIME", "Remote, United Kingdom", true),
            ["undisclosed"] = CreatePosting("undisclosed", "[Sample] Senior Platform Developer", "Example Undisclosed", null, null, "FULL_TIME", "Manchester, United Kingdom", false),
            ["unknown"] = CreatePosting("unknown", "[Sample] Software Delivery Specialist", "Example Unclassified", 70000, 90000, null, "Coventry, United Kingdom", false),
        };
        var scans = new (int DaysAgo, string[] RoleIds)[]
        {
            (28, new[] { "platform", "backend", "cloud" }),
            (24, new[] { "platform", "backend", "cloud", "manager", "contract" }),
            (20, new[] { "platform", "backend", "cloud", "manager", "contract" }),
            (16, new[] { "platform", "cloud", "manager", "distributed", "contract" }),
            (12, new[] { "platform", "cloud", "manager", "distributed", "contract", "parttime" }),
            (8, new[] { "platform", "cloud", "manager", "distributed", "database", "parttime", "undisclosed", "contract2", "unknown" }),
            (4, new[] { "platform", "cloud", "distributed", "database", "parttime", "undisclosed", "contract2", "unknown" }),
            (0, new[] { "platform", "cloud", "distributed", "database", "architecture", "parttime", "undisclosed", "contract2", "unknown" }),
        };

        foreach (var (daysAgo, roleIds) in scans)
        {
            var capturedAt = DateTime.UtcNow.Date.AddDays(-daysAgo).AddHours(9);
            var postings = roleIds.Select(roleId => roles[roleId]).ToList();
            repository.RecordScan(postings, "DEMO: CV-tuned UK senior engineering profile", capturedAt, isDemo: true);
        }

        foreach (var roleId in new[] { "platform", "manager", "contract", "unknown" })
        {
            repository.SetStarred(roles[roleId].Id, true);
        }
    }

    private static JobPosting CreatePosting(
        string id,
        string title,
        string company,
        decimal? minSalary,
        decimal? maxSalary,
        string? jobType = "FULL_TIME",
        string location = "Remote, United Kingdom",
        bool isRemote = true,
        string interval = "yearly") => new()
    {
        JobUrl = $"https://example.invalid/jobs/{id}",
        Site = "sample",
        Title = title,
        Company = company,
        Location = location,
        JobType = jobType,
        DatePosted = DateTime.UtcNow.AddDays(-14).ToString("yyyy-MM-dd"),
        Interval = interval,
        MinAmount = minSalary,
        MaxAmount = maxSalary,
        Currency = "GBP",
        IsRemote = isRemote,
        Description = "Synthetic sample record used to verify historic availability, salary trends, and saved-interest controls. Not a real vacancy.",
    };
}